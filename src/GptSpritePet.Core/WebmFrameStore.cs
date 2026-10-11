using System.Diagnostics;
using System.Globalization;

namespace GptSpritePet.Core;

/// <summary>Bounded, on-demand alpha VP9 decoding. No raw disk cache.</summary>
public sealed class WebmFrameStore : IDisposable
{
    public const int BufferFrames = 6;
    public const int MaximumActiveDecoders = 2;
    private static readonly object PoolGate = new();
    private static readonly List<WebmFrameStore> Active = [];
    private static readonly SemaphoreSlim DecoderSlots = new(MaximumActiveDecoders);
    private readonly object _gate = new();
    private readonly Dictionary<int, byte[]> _frames = [];
    private readonly string _source, _ffmpeg;
    private readonly int _frameBytes;
    private CancellationTokenSource? _cancellation;
    private Process? _process;
    private Task? _worker;
    private int _requested, _next;
    private bool _disposed, _ended;
    private Exception? _error;
    public event Action? FrameReady;
    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public long CachedBytes { get { lock (_gate) return _frames.Count * (long)_frameBytes; } }
    public static int ActiveDecoderCount { get { lock (PoolGate) return Active.Count; } }
    public static long TotalCachedBytes { get { lock (PoolGate) return Active.Sum(s => s.CachedBytes); } }

    public WebmFrameStore(string directory, AnimationClip clip, string ffmpeg)
    {
        if (clip.Video is null) throw new InvalidDataException("未指定 WebM 动画。");
        _source = SkinDefinition.ResolveVideo(directory, clip.Video);
        var info = new FileInfo(_source);
        if (!info.Exists || info.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("WebM 动画不存在，或超过 64 MB。");
        if (!File.Exists(ffmpeg)) throw new InvalidDataException("透明视频解码器缺失，请保留程序的 runtime 文件夹。");
        if (clip.VideoWidth is < 1 or > 4096 || clip.VideoHeight is < 1 or > 4096 || clip.VideoFrameCount is < 1 or > 3600)
            throw new InvalidDataException("WebM 视频尺寸或帧数无效。");
        _ffmpeg = ffmpeg;
        var scale = Math.Min(1, 640d / Math.Max(clip.VideoWidth, clip.VideoHeight));
        Width = Math.Max(1, (int)Math.Floor(clip.VideoWidth * scale));
        Height = Math.Max(1, (int)Math.Floor(clip.VideoHeight * scale));
        FrameCount = clip.VideoFrameCount;
        _frameBytes = checked(Width * Height * 4);
    }

    // Compatibility with older callers. No files are created in this directory.
    public WebmFrameStore(string directory, AnimationClip clip, string ffmpeg, string cacheDirectory)
        : this(directory, clip, ffmpeg) { }

    public void RequestFrame(int index)
    {
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        lock (PoolGate)
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            Active.Remove(this);
            if (Active.Count >= MaximumActiveDecoders)
            {
                var oldest = Active[0]; Active.RemoveAt(0); oldest.Suspend();
            }
            Active.Add(this);
            lock (_gate)
            {
                _requested = index;
                foreach (var key in _frames.Keys.Where(key => key < index).ToArray()) _frames.Remove(key);
                if (_worker is null || !_frames.ContainsKey(index) && (index < _next || _ended))
                {
                    StopLocked(); _frames.Clear(); _next = 0; _ended = false; _error = null;
                    _cancellation = new CancellationTokenSource();
                    var token = _cancellation.Token;
                    _worker = Task.Run(() => DecodeAsync(token));
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    public bool TryReadFrame(int index, out byte[]? pixels)
    {
        RequestFrame(index);
        lock (_gate)
        {
            if (_error is { } error) throw new InvalidDataException("透明视频解码失败。", error);
            return _frames.TryGetValue(index, out pixels);
        }
    }

    /// <summary>Exact, blocking read for import validation and deterministic diagnostics.</summary>
    public byte[] ReadFrame(int index)
    {
        RequestFrame(index);
        var timeout = Stopwatch.StartNew();
        lock (_gate)
        {
            while (!_frames.ContainsKey(index))
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_error is { } error) throw new InvalidDataException("透明视频解码失败。", error);
                if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidDataException("透明视频解码超时。");
                Monitor.Wait(_gate, 100);
            }
            return _frames[index];
        }
    }

    private Process StartDecoder()
    {
        var start = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1",
            "-c:v", "libvpx-vp9", "-i", _source, "-map", "0:v:0", "-vf", $"scale={Width}:{Height}",
            "-frames:v", (FrameCount + 1).ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough",
            "-pix_fmt", "bgra", "-an", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidDataException("无法启动透明视频解码器。");
    }

    private static bool HasTransparency(byte[] frame)
    {
        for (var i = 3; i < frame.Length; i += 4) if (frame[i] < 12) return true;
        return false;
    }

    private async Task DecodeAsync(CancellationToken token)
    {
        var acquired = false;
        try
        {
            await DecoderSlots.WaitAsync(token).ConfigureAwait(false); acquired = true;
            token.ThrowIfCancellationRequested();
            using var process = StartDecoder();
            using var cleanup = new DecoderCleanup(process);
            using var registration = token.Register(() => Kill(process));
            lock (_gate) { token.ThrowIfCancellationRequested(); _process = process; }
            var errors = process.StandardError.ReadToEndAsync(token);
            var index = 0;
            while (true)
            {
                lock (_gate)
                {
                    while (_frames.Count >= BufferFrames && !token.IsCancellationRequested) Monitor.Wait(_gate, 100);
                    token.ThrowIfCancellationRequested();
                }
                var frame = new byte[_frameBytes];
                var count = await ReadCompleteFrame(process.StandardOutput.BaseStream, frame, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count != frame.Length || index >= FrameCount) throw new InvalidDataException("视频实际帧数与 skin.json 不一致，请重新导入素材。");
                if (index == 0 && !HasTransparency(frame)) throw new InvalidDataException("视频首帧没有透明像素，请导入 VP9 Alpha WebM。");
                var publish = false;
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    _next = index + 1;
                    if (index >= _requested) { _frames[index] = frame; publish = true; }
                    Monitor.PulseAll(_gate);
                }
                if (publish) FrameReady?.Invoke();
                index++;
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var message = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("透明视频解码失败：" + message[..Math.Min(message.Length, 500)]);
            if (index != FrameCount) throw new InvalidDataException("视频实际帧数与 skin.json 不一致，请重新导入素材。");
            lock (_gate) { token.ThrowIfCancellationRequested(); _ended = true; Monitor.PulseAll(_gate); }
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (_gate) if (!token.IsCancellationRequested) { _error = ex; Monitor.PulseAll(_gate); }
            if (!token.IsCancellationRequested) FrameReady?.Invoke();
        }
        finally
        {
            lock (_gate) if (!token.IsCancellationRequested) _process = null;
            if (acquired) DecoderSlots.Release();
        }
    }

    private static async Task<int> ReadCompleteFrame(Stream stream, byte[] frame, CancellationToken token)
    {
        var read = 0;
        while (read < frame.Length)
        {
            var count = await stream.ReadAsync(frame.AsMemory(read), token).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return read;
    }

    /// <summary>Checks every frame, alpha and exact EOF without retaining or writing the clip.</summary>
    public async Task ValidateAsync(CancellationToken token = default)
    {
        lock (PoolGate)
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            Active.Remove(this); Suspend();
            if (Active.Count >= MaximumActiveDecoders)
            { var oldest = Active[0]; Active.RemoveAt(0); oldest.Suspend(); }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30)); token = deadline.Token;
        await DecoderSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var process = StartDecoder();
            using var cleanup = new DecoderCleanup(process);
            using var registration = token.Register(() => Kill(process));
            var errors = process.StandardError.ReadToEndAsync(token);
            var frame = new byte[_frameBytes]; var index = 0;
            while (true)
            {
                var count = await ReadCompleteFrame(process.StandardOutput.BaseStream, frame, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count != frame.Length || index >= FrameCount) throw new InvalidDataException("视频实际帧数与 skin.json 不一致。");
                if (index == 0 && !HasTransparency(frame)) throw new InvalidDataException("视频首帧没有透明像素。");
                index++;
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var message = await errors.ConfigureAwait(false);
            if (index != FrameCount || process.ExitCode != 0) throw new InvalidDataException("视频帧数或解码无效：" + message[..Math.Min(message.Length, 500)]);
        }
        finally { DecoderSlots.Release(); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private sealed class DecoderCleanup(Process process) : IDisposable
    {
        public void Dispose() => Kill(process);
    }
    private void StopLocked()
    {
        _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        if (_process is { } process) Kill(process);
        _process = null; _worker = null; Monitor.PulseAll(_gate);
    }
    private void Suspend()
    {
        lock (_gate) { StopLocked(); _frames.Clear(); _next = 0; _ended = false; }
    }
    public void Dispose()
    {
        lock (PoolGate)
        {
            Active.Remove(this);
            lock (_gate) { _disposed = true; StopLocked(); _frames.Clear(); }
        }
    }
}
