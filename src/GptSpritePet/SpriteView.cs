using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GptSpritePet.Core;

namespace GptSpritePet;

internal sealed partial class SpriteView : FrameworkElement, IDisposable
{
    internal sealed record PreviewAction(string Key, string Label, PetState? State, AnimationPreviewGroup Group);
    private sealed record Frame(BitmapSource Image, byte[] Pixels, int Stride, string Fingerprint, Rect VisiblePixels)
    { internal int VisibleTop => (int)VisiblePixels.Top; }
    private sealed record SpriteClip(Func<Frame?>[] Frames, double FrameMilliseconds, bool Loop, double AnchorX, double AnchorY,
        FrameAnchor[] FrameAnchors, double? WalkingStride = null, FrameAnchor[]? FootContacts = null, int[]? ContactLegIds = null,
        int[]? BlinkFrameIndices = null, bool DisableProceduralMotion = false, AnimationAlignment? Alignment = null);
    private sealed record CachedFrame(Frame Frame, long Bytes, long Stamp);
    private long _preloadedBytes, _streamedBytes;
    private readonly List<WebmFrameStore> _videoStores = new();
    private bool _frameNotificationPending;
    internal bool ExactFrameReads { get; set; }
    private Frame? _presentedFrame;
    private SpriteClip? _presentedClip;
    private int _presentedIndex;
    private Rect _presentedBounds;
    private Matrix _presentedMatrix;
    private bool _waitingForFrame;
    internal int PresentedFrameIndex => _presentedFrame is null ? -1 : _presentedIndex;
    internal bool WaitingForVideoFrame => _waitingForFrame;
    private Dictionary<PetState, SpriteClip> _clips = new();
    private Dictionary<string, SpriteClip> _namedClips = new(StringComparer.OrdinalIgnoreCase);
    internal PreviewAction[] AvailableActions { get; private set; } = [];
    internal string? AutomaticMovementKey { get; set; }
    internal string? PreviewKey { get; private set; }
    internal WalkingPlayback? WalkingPlayback { get; set; }
    internal WalkingTiming? WalkingTiming { get; private set; }
    private double _walkingSpeedAt240 = 55;
    private Frame[] _blinkFrames = [];
    private int _blinkFrameMs = 80;
    private int _blinkIntervalMs = 4600;
    private double _displayScale = 0.96;
    private double? _standingHeightRatio;
    private double _animationTime;
    private double _stateTime;
    private PetState _state;
    private int _direction = 1;
    private Frame? _transitionFrame;
    private Rect _transitionBounds;
    private Matrix _transitionMatrix;
    private double _transitionTime = 0.18;
    public PetState State
    {
        get => _state;
        set => SetBehaviorState(value, value == PetState.Walking ? WalkingPlayback : null, Direction);
    }
    internal void SetBehaviorState(PetState value, WalkingPlayback? playback, int direction)
    {
        var facing = direction < 0 ? -1 : 1;
        if (_state != value || value == PetState.Walking && _direction != facing)
        {
            var previous = _state;
            CaptureTransitionSource();
            if (_state != value) _stateTime = 0;
            _state = value;
            _direction = facing;
            WalkingPlayback = playback;
            _idleFrameOffset = value == PetState.Idle && previous == PetState.Walking
                ? _transitions?.IdleEntryFrame ?? 0 : 0;
            _transitionAlignment = TransitionAlignment(previous, value);
        }
        else { WalkingPlayback = playback; _direction = facing; }
        InvalidateVisual();
    }
    public int Direction { get => _direction; set => SetBehaviorState(_state, WalkingPlayback, value); }
    public string SkinName { get; private set; } = "功能占位素材";
    public string? LoadWarning { get; private set; }
    public bool IsPlaceholder => _clips.Count == 0;
    internal bool UsesImageFrames => !IsPlaceholder;
    internal bool HasAnimation(PetState state) => _clips.ContainsKey(state);
    internal bool UsesWebm { get; private set; }
    internal int FrameCount(PetState state) => _clips.TryGetValue(state, out var clip) ? clip.Frames.Length : 0;
    internal int GetFrameCount(PetState state) => FrameCount(state);
    internal int DistinctFrameCount(PetState state)
    {
        var exact = ExactFrameReads; ExactFrameReads = true;
        try { return _clips.TryGetValue(state, out var clip)
            ? clip.Frames.Select(frame => frame()!.Fingerprint).Distinct(StringComparer.Ordinal).Count() : 0; }
        finally { ExactFrameReads = exact; }
    }
    internal bool HasBlinkFrames => _blinkFrames.Length > 0 || _clips.GetValueOrDefault(PetState.Idle)?.BlinkFrameIndices is { Length: > 0 };
    internal bool HasBlinkFrame => HasBlinkFrames;
    internal double AnimationTime => _animationTime;
    internal double FrameMillisecondsFor(PetState state) => FindClip(state)?.FrameMilliseconds ?? 120;
    internal int CurrentSupportLeg => (_presentedClip ?? FindClip(State))?.ContactLegIds is { Length: > 0 } legs
        ? legs[_presentedIndex] : CurrentFrameIndex < FrameCount(State) / 2 ? 0 : 1;
    internal int CurrentFrameIndex => State == PetState.Walking && WalkingPlayback is { } walking ? walking.FrameIndex
        : CurrentBlinkIndex() is var blink && blink >= 0 ? blink
        : FindClip(State) is { } clip ? ClipFrameIndex(clip) : 0;
    private int ClipFrameIndex(SpriteClip clip)
    {
        var index = AnimationClock.FrameIndex(IsRestPreview ? RestPreviewPosition.Time : _stateTime,
            clip.Frames.Length, clip.FrameMilliseconds, !IsRestPreview && clip.Loop);
        return State == PetState.Idle && PreviewKey is null && clip.Loop
            ? (index + _idleFrameOffset) % clip.Frames.Length : index;
    }
    internal long DecodedCacheBytes => _preloadedBytes + _streamedBytes + _peekBytes + WebmFrameStore.TotalCachedBytes;
    internal double ClipSeconds(PetState state) => _clips.TryGetValue(state,out var clip) ? clip.Frames.Length*clip.FrameMilliseconds/1000d : 0;
    internal bool HasFullCharacterWalk => _clips.TryGetValue(PetState.Walking, out var clip) && clip.WalkingStride.HasValue;
    internal bool HasMeasuredWalkingContacts => HasFullCharacterWalk;
    internal int WalkingRenderMilliseconds => HasFullCharacterWalk ? 20 : 40;
    internal double DisplayScale => _displayScale;
    internal double CurrentAlignmentScale => (_presentedClip ?? FindClip(State))?.Alignment?.Scale ?? 1;
    internal bool UsesStandingHeight => _standingHeightRatio.HasValue;
    internal double ViewportSizeAt(double requestedHeight) => _standingHeightRatio is { } ratio
        ? requestedHeight / (ratio * _displayScale) : requestedHeight;
    internal double WalkingSpeedAt(double size) => _clips.TryGetValue(PetState.Walking, out var clip) && clip.WalkingStride is { } stride
            ? stride / (clip.Frames.Length * clip.FrameMilliseconds / 1000d) * ViewportSizeAt(size) / 256 * _displayScale
            : WalkingTiming is not null ? _walkingSpeedAt240 * size / 240 : 45;
    internal Point WalkingSolePoint(bool near)
    {
        if (State != PetState.Walking || CurrentFrame() is not { } frame) throw new InvalidOperationException();
        var bounds = ImageBounds(frame);
        if (HasFullCharacterWalk && (_presentedClip ?? FindClip(State))?.FootContacts is { } contacts)
        {
            var contact = contacts[_presentedIndex];
            return ImageTransform(frame).Transform(new Point(bounds.X + contact.X * bounds.Width, bounds.Y + contact.Y * bounds.Height));
        }

        if (_waitingForFrame)
        {
            var anchor = CurrentAnchor();
            return ImageTransform(frame).Transform(new Point(bounds.X + anchor.X * bounds.Width, bounds.Y + anchor.Y * bounds.Height));
        }
        throw new InvalidOperationException("没有标定走路接触点。");
    }

    public SpriteView()
    {
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
#if NATIVE_CHECKS
        ExactFrameReads = true;
#endif
    }

    public void LoadSkin(string? directory)
    {
        DisposeVideoStores();
        _presentedFrame = null; _presentedClip = null; _waitingForFrame = false;
        PreviewKey = null; AvailableActions = []; _namedClips = new(StringComparer.OrdinalIgnoreCase);
        LoadWarning = null; UsesWebm = false; _transitionFrame = null; _clips = new(); _blinkFrames = []; _displayScale = 0.96; SkinName = "功能占位素材";
        _standingHeightRatio = null;
        _transitions = null;
        WalkingTiming = null; WalkingPlayback = null;
        _preloadedBytes = _streamedBytes = 0; ResetAnimation();
        directory = string.IsNullOrWhiteSpace(directory) ? ProjectPaths.DefaultSkin : directory;
        try
        {
            var definition = SkinDefinition.Load(directory);
            var clips = new Dictionary<PetState, SpriteClip>();
            var namedClips = new Dictionary<string, SpriteClip>(StringComparer.OrdinalIgnoreCase);
            var actions = new List<PreviewAction>();
            var sources = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var cachedFrames = new Dictionary<string, Frame>(StringComparer.OrdinalIgnoreCase);
            long decodedBytes = 0;
            void Reserve(long bytes)
            {
                decodedBytes += bytes;
                if (decodedBytes > 80 * 1024 * 1024) throw new InvalidDataException("素材解码内存超过 80 MB，请缩小图片或减少帧数。");
            }
            BitmapSource Source(string relativePath, int maxWidth, int maxHeight)
            {
                var path = SkinDefinition.ResolveFrame(directory, relativePath);
                var key = path + "|" + maxWidth + "|" + maxHeight;
                if (sources.TryGetValue(key, out var cached)) return cached;
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("单个素材文件超过 8 MB。");
                int width, height;
                using (var metadata = File.OpenRead(path))
                {
                    var decoder = BitmapDecoder.Create(metadata, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
                }
                if (width > 4096 || height > 4096 || width <= 0 || height <= 0)
                    throw new InvalidDataException("素材图片尺寸必须在 1～4096 像素之间。");
                var scale = Math.Min(1, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
                var decodeWidth = Math.Max(1, (int)Math.Floor(width * scale));
                var decodeHeight = Math.Max(1, (int)Math.Floor(height * scale));
                Reserve(decodeWidth * (long)decodeHeight * 8); // Decoder and converted bitmap, conservatively counted.
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream; bitmap.DecodePixelWidth = decodeWidth; bitmap.DecodePixelHeight = decodeHeight;
                bitmap.EndInit(); bitmap.Freeze();
                BitmapSource image = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                image.Freeze(); sources[key] = image; return image;
            }
            Frame MakeFrame(string key, BitmapSource image)
            {
                if (cachedFrames.TryGetValue(key, out var cached)) return cached;
                var stride = image.PixelWidth * 4;
                Reserve(stride * (long)image.PixelHeight * 2); // Hit pixels plus possible crop materialization.
                var pixels = new byte[stride * image.PixelHeight]; image.CopyPixels(pixels, stride, 0);
                var fingerprint = image.PixelWidth + "x" + image.PixelHeight + "|" + Convert.ToHexString(SHA256.HashData(pixels));
                var frame = DecodedFrame(image, pixels, stride, fingerprint); cachedFrames[key] = frame; return frame;
            }
            Frame SingleFrame(string path) => MakeFrame("png|" + SkinDefinition.ResolveFrame(directory, path), Source(path, 512, 512));
            var deferred = new Dictionary<string,CachedFrame>(StringComparer.OrdinalIgnoreCase);
            var videos = new Dictionary<string,WebmFrameStore>(StringComparer.OrdinalIgnoreCase);
            var frameBudget = definition.NamedClips().Any(pair => pair.Value.Video is not null)
                ? 56L * 1024 * 1024 : 80L * 1024 * 1024;
            var checkedPaths = new Dictionary<string,(int Width,int Height)>(StringComparer.OrdinalIgnoreCase);
            long deferredBytes = 0, stamp = 0;
            Func<Frame?> StreamFrame(string relativePath)
            {
                var path = SkinDefinition.ResolveFrame(directory,relativePath);
                if (!checkedPaths.ContainsKey(path))
                {
                    if (new FileInfo(path).Length > 8*1024*1024) throw new InvalidDataException("单个素材文件超过 8 MB。");
                    using var stream = File.OpenRead(path);
                    var decoder = BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
                    var info = decoder.Frames[0];
                    if (info.PixelWidth is <= 0 or > 4096 || info.PixelHeight is <= 0 or > 4096)
                        throw new InvalidDataException("素材图片尺寸必须在 1～4096 像素之间。");
                    checkedPaths[path] = (info.PixelWidth,info.PixelHeight);
                }
                var dimensions = checkedPaths[path]; var scale = Math.Min(1,384d/Math.Max(dimensions.Width,dimensions.Height));
                var decodeWidth = Math.Max(1,(int)Math.Floor(dimensions.Width*scale));
                var decodeHeight = Math.Max(1,(int)Math.Floor(dimensions.Height*scale));
                return () =>
                {
                    if (deferred.TryGetValue(path,out var entry))
                    { deferred[path] = entry with { Stamp = ++stamp }; return entry.Frame; }
                    SkinDefinition.ResolveFrame(directory,relativePath);
                    using var stream = File.OpenRead(path);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream; bitmap.DecodePixelWidth = decodeWidth; bitmap.DecodePixelHeight = decodeHeight; bitmap.EndInit(); bitmap.Freeze();
                    BitmapSource converted = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);
                    var width = converted.PixelWidth; var height = converted.PixelHeight; var stride = width*4;
                    var bytes = width*(long)height*8;
                    if (decodedBytes+bytes > frameBudget) throw new InvalidDataException("单帧与预加载素材合计超过解码预算。");
                    while (decodedBytes+deferredBytes+bytes > frameBudget && deferred.Count > 0)
                    {
                        var oldest = deferred.MinBy(pair => pair.Value.Stamp);
                        deferredBytes -= oldest.Value.Bytes; deferred.Remove(oldest.Key);
                    }
                    var pixels = new byte[stride*height]; converted.CopyPixels(pixels,stride,0);
                    var image = BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,stride); image.Freeze();
                    var frame = DecodedFrame(image, pixels, stride, width+"x"+height+"|"+Convert.ToHexString(SHA256.HashData(pixels)));
                    deferredBytes += bytes; _streamedBytes = deferredBytes;
                    deferred[path] = new(frame,bytes,++stamp); return frame;
                };
            }
            Func<Frame?> VideoFrame(WebmFrameStore video, string path, int index)
            {
                var key = "webm|" + path + "|" + index;
                return () =>
                {
                    if (deferred.TryGetValue(key, out var cached))
                    { deferred[key] = cached with { Stamp = ++stamp }; return cached.Frame; }
                    byte[]? pixels;
                    try
                    {
                        if (ExactFrameReads) pixels = video.ReadFrame(index);
                        else if (!video.TryReadFrame(index, out pixels)) return null;
                    }
                    catch (InvalidDataException ex) when (!ExactFrameReads)
                    { LoadWarning = "素材无法解码：" + (ex.InnerException?.Message ?? ex.Message); return null; }
                    var bytes = video.Width * (long)video.Height * 8;
                    if (decodedBytes + bytes > frameBudget) throw new InvalidDataException("单帧与预加载素材合计超过解码预算。");
                    while (decodedBytes + deferredBytes + bytes > frameBudget && deferred.Count > 0)
                    {
                        var oldest = deferred.MinBy(pair => pair.Value.Stamp);
                        deferredBytes -= oldest.Value.Bytes; deferred.Remove(oldest.Key);
                    }
                    var stride = video.Width * 4;
                    var image = BitmapSource.Create(video.Width, video.Height, 96, 96, PixelFormats.Bgra32, null, pixels!, stride); image.Freeze();
                    var frame = DecodedFrame(image, pixels!, stride, video.Width + "x" + video.Height + "|" + Convert.ToHexString(SHA256.HashData(pixels!)));
                    deferredBytes += bytes; _streamedBytes = deferredBytes;
                    deferred[key] = new(frame, bytes, ++stamp); return frame;
                };
            }
            foreach (var (name, clip) in definition.NamedClips())
            {
                Func<Frame?>[] frames;
                var sheets = clip.SpriteSheet is { } single ? new[] { single } : clip.SpriteSheets;
                if (clip.Video is { } relativeVideo)
                {
                    var path = SkinDefinition.ResolveVideo(directory, relativeVideo);
                    var key = path + "|" + clip.VideoWidth + "|" + clip.VideoHeight + "|" + clip.VideoFrameCount;
                    if (!videos.TryGetValue(key, out var video))
                    {
                        video = new WebmFrameStore(directory, clip, ProjectPaths.Decoder);
                        video.FrameReady += OnVideoFrameReady;
                        _videoStores.Add(video);
                        videos[key] = video;
                    }
                    frames = Enumerable.Range(0, video.FrameCount).Select(index => VideoFrame(video, key, index)).ToArray();
                    UsesWebm = true;
                }
                else if (sheets.Length > 0)
                {
                    var perSheet = clip.Columns * clip.Rows;
                    var indices = clip.FrameIndices.Length > 0 ? clip.FrameIndices : Enumerable.Range(0, perSheet * sheets.Length).ToArray();
                    frames = indices.Select(index =>
                    {
                        var sheet = sheets[index / perSheet];
                        var source = Source(sheet, clip.Columns * 512, clip.Rows * 512);
                        if (source.PixelWidth < clip.Columns || source.PixelHeight < clip.Rows)
                            throw new InvalidDataException("图集尺寸小于指定行列数。");
                        var local = index % perSheet;
                        var column = local % clip.Columns; var row = local / clip.Columns;
                        var left = (int)Math.Round(source.PixelWidth * column / (double)clip.Columns);
                        var top = (int)Math.Round(source.PixelHeight * row / (double)clip.Rows);
                        var right = (int)Math.Round(source.PixelWidth * (column + 1) / (double)clip.Columns);
                        var bottom = (int)Math.Round(source.PixelHeight * (row + 1) / (double)clip.Rows);
                        var crop = new CroppedBitmap(source, new Int32Rect(left, top, right - left, bottom - top)); crop.Freeze();
                        var frame = MakeFrame("sheet|" + SkinDefinition.ResolveFrame(directory, sheet) + "|" + clip.Columns + "|" + clip.Rows + "|" + index, crop);
                        return FixedFrame(frame);
                    }).ToArray();
                }
                else frames = clip.Frames.Select(path => clip.StreamFrames ? StreamFrame(path) : FixedFrame(SingleFrame(path))).ToArray();
                var loaded = new SpriteClip(frames, clip.EffectiveFrameMilliseconds, clip.Loop, clip.AnchorX, clip.AnchorY, clip.FrameAnchors, clip.WalkingStride, clip.FootContacts, clip.ContactLegIds,clip.BlinkFrameIndices,clip.DisableProceduralMotion,clip.Alignment);
                namedClips[name] = loaded;
                PetState? known = Enum.TryParse<PetState>(name, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
                if (known.HasValue) clips[known.Value] = loaded;
                var isWalkingSegment = definition.WalkingSequence is not null
                    && (name.Equals("WalkStart", StringComparison.OrdinalIgnoreCase) || name.Equals("WalkEnd", StringComparison.OrdinalIgnoreCase));
                if (known != PetState.Dragging && !isWalkingSegment) actions.Add(new(name, clip.DisplayName ?? (known.HasValue ? StateNames.Label(known.Value) : name), known,
                    clip.PreviewGroup ?? (known == PetState.Idle ? AnimationPreviewGroup.Idle : AnimationPreviewGroup.ClickInteraction)));
            }
            var blinkFrames = definition.BlinkFrames.Select(SingleFrame).ToArray();
            _clips = clips; _blinkFrames = blinkFrames; _blinkFrameMs = definition.BlinkFrameMilliseconds;
            _blinkIntervalMs = definition.BlinkIntervalMilliseconds; _displayScale = definition.DisplayScale; SkinName = definition.Name;
            _standingHeightRatio = definition.StandingHeightRatio;
            _transitions = definition.Transitions;
            _preloadedBytes = decodedBytes;
            _namedClips = namedClips;
            AvailableActions = actions.Where(action => !HasRestSequence || action.State is not (PetState.LyingDown or PetState.Waking)).ToArray();
            if (definition.WalkingSequence is { } sequence)
            { WalkingTiming = sequence.Timing(definition.NamedClips()); _walkingSpeedAt240 = sequence.SpeedAt240; }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or ArgumentException or NotSupportedException or FileFormatException or System.ComponentModel.Win32Exception)
        {
            DisposeVideoStores();
            _clips = new(); _namedClips = new(StringComparer.OrdinalIgnoreCase); AvailableActions = [];
            LoadWarning = "素材无法加载：" + ex.Message;
        }
        try { WebmCacheMaintenance.Trim(Path.Combine(Path.GetTempPath(), "GptSpritePet", "webm-cache"), budget: 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        InvalidateVisual();
    }

    private void DisposeVideoStores()
    {
        foreach (var store in _videoStores) { store.FrameReady -= OnVideoFrameReady; store.Dispose(); }
        _videoStores.Clear();
    }
    private void OnVideoFrameReady()
    {
        lock (_videoStores)
        {
            if (_frameNotificationPending || Dispatcher.HasShutdownStarted) return;
            _frameNotificationPending = true;
        }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            lock (_videoStores) _frameNotificationPending = false;
            InvalidateVisual();
        }));
    }
    public void Dispose() => DisposeVideoStores();

    private static Func<Frame?> FixedFrame(Frame frame) => () => frame;

    private static Frame DecodedFrame(BitmapSource image, byte[] pixels, int stride, string fingerprint)
    {
        var left = image.PixelWidth; var top = image.PixelHeight; var right = -1; var bottom = -1;
        for (var y = 0; y < image.PixelHeight; y++)
        for (var x = 0; x < image.PixelWidth; x++)
        {
            if (pixels[y * stride + x * 4 + 3] < 12) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = y;
        }
        var visible = right >= left ? new Rect(left, top, right - left + 1, bottom - top + 1)
            : new Rect(0, 0, image.PixelWidth, image.PixelHeight);
        return new(image, pixels, stride, fingerprint, visible);
    }

    public void Advance(double seconds)
    {
        if (EdgePeek != ScreenEdge.None) return;
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        if (PreviewKey is null && State == PetState.Sleeping && FindClip(State) is { Loop: true } sleeping)
        {
            var cycle = sleeping.Frames.Length * sleeping.FrameMilliseconds / 1000d;
            if (Math.Floor((_stateTime + seconds + 1e-8) / cycle) > Math.Floor((_stateTime + 1e-8) / cycle))
                CaptureTransitionSource();
        }
        _animationTime += seconds; _stateTime += seconds;
        _transitionTime += seconds;
        if (_transitionTime >= TransitionSeconds) ClearTransition();
        InvalidateVisual();
    }
    internal void ResetAnimation() { _animationTime = 0; _stateTime = 0; _idleFrameOffset = 0; ClearTransition(); InvalidateVisual(); }
    internal void RestartAnimation()
    {
        CaptureTransitionSource();
        _animationTime = 0; _stateTime = 0; _idleFrameOffset = 0; _transitionAlignment = null;
        InvalidateVisual();
    }
    internal void PrepareWalkingAdvance(double seconds)
    {
        if (State != PetState.Walking || WalkingPlayback is not { Playing: true } walk || seconds <= 0) return;
        var next = Math.Min(walk.ElapsedSeconds + seconds, walk.FinishSeconds);
        var nextKey = next + 1e-8 < walk.Timing.StartFrames / walk.Timing.Fps ? "WalkStart"
            : next + 1e-8 < walk.FinishSeconds - walk.Timing.EndFrames / walk.Timing.Fps ? "Walking" : "WalkEnd";
        if (nextKey == walk.ClipKey) return;
        CaptureTransitionSource(); _transitionAlignment = null;
    }
    internal void BeginPreview(string key, WalkingPlayback? playback = null, int? direction = null)
    {
        if (!_namedClips.ContainsKey(key)) throw new ArgumentException("该动作尚未加载。", nameof(key));
        var previous = State;
        CaptureTransitionSource();
        PreviewKey = key;
        _state = AvailableActions.First(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).State ?? PetState.Idle;
        WalkingPlayback = _state == PetState.Walking ? playback : null;
        _direction = (direction ?? Direction) < 0 ? -1 : 1;
        _animationTime = 0; _stateTime = 0; _idleFrameOffset = 0;
        _transitionAlignment = TransitionAlignment(previous, State);
        InvalidateVisual();
    }
    internal void EndPreview(PetState state = PetState.Idle, WalkingPlayback? playback = null, int? direction = null)
    {
        var previous = State;
        CaptureTransitionSource();
        PreviewKey = null; _state = state; WalkingPlayback = state == PetState.Walking ? playback : null;
        _direction = (direction ?? Direction) < 0 ? -1 : 1;
        _animationTime = 0; _stateTime = 0;
        _idleFrameOffset = state == PetState.Idle && previous == PetState.Walking ? IdleEntryFrame : 0;
        _transitionAlignment = TransitionAlignment(previous, state);
        InvalidateVisual();
    }
    private bool HasRestSequence => HasAnimation(PetState.LyingDown) && HasAnimation(PetState.Sleeping) && HasAnimation(PetState.Waking);
    private bool IsRestPreview => HasRestSequence && string.Equals(PreviewKey, nameof(PetState.Sleeping), StringComparison.OrdinalIgnoreCase);
    internal PetState? RestPreviewPhase => IsRestPreview ? RestPreviewPosition.State : null;
    private (PetState State, double Time) RestPreviewPosition
    {
        get
        {
            var lying = ClipSeconds(PetState.LyingDown);
            var sleeping = ClipSeconds(PetState.Sleeping);
            if (_stateTime + 1e-8 < lying) return (PetState.LyingDown, _stateTime);
            if (_stateTime + 1e-8 < lying + sleeping) return (PetState.Sleeping, Math.Max(0, _stateTime - lying));
            return (PetState.Waking, Math.Max(0, _stateTime - lying - sleeping));
        }
    }
    internal double PreviewSeconds(string key) => HasRestSequence && key.Equals(nameof(PetState.Sleeping), StringComparison.OrdinalIgnoreCase)
        ? ClipSeconds(PetState.LyingDown) + ClipSeconds(PetState.Sleeping) + ClipSeconds(PetState.Waking)
        : _namedClips.TryGetValue(key, out var clip) ? clip.Frames.Length * clip.FrameMilliseconds / 1000d : 0;
    private SpriteClip? FindClip(PetState state) => IsRestPreview ? _clips[RestPreviewPosition.State]
        : state == PetState.Walking && WalkingPlayback is { } walking
        ? _namedClips.GetValueOrDefault(walking.ClipKey) : PreviewKey is { } key ? _namedClips.GetValueOrDefault(key) :
        state == PetState.Walking && AutomaticMovementKey is { } movementKey && _namedClips.ContainsKey(movementKey) ? _namedClips[movementKey] :
        _clips.TryGetValue(state, out var clip) ? clip : _clips.GetValueOrDefault(PetState.Idle);
    private int CurrentBlinkIndex() => PreviewKey is null && State == PetState.Idle && _clips.ContainsKey(PetState.Idle)
        ? AnimationClock.BlinkFrameIndex(_animationTime, _blinkFrames.Length, _blinkFrameMs, _blinkIntervalMs) : -1;
    private Frame? CurrentFrame()
    {
        if (EdgePeek != ScreenEdge.None)
        {
            var peek = _peekFrames.GetValueOrDefault(EdgePeek);
            if (peek is not null)
            {
                // A restored docked pet may not have decoded any video yet. Keep its
                // static image too, so the first drag can wait for video without flashing.
                _waitingForFrame = false;
                _presentedFrame = peek; _presentedClip = null; _presentedIndex = 0;
                _presentedBounds = PeekImageBounds(peek); _presentedMatrix = Matrix.Identity;
            }
            return peek;
        }
        var blink = CurrentBlinkIndex();
        if (blink >= 0) return _blinkFrames[blink];
        var clip = FindClip(State);
        var index = CurrentFrameIndex;
        var frame = clip?.Frames[index]();
        if (frame is null) { _waitingForFrame = clip is not null; return _presentedFrame; }
        _waitingForFrame = false;
        _presentedFrame = frame; _presentedClip = clip; _presentedIndex = index;
        _presentedBounds = ImageBounds(frame); _presentedMatrix = ImageTransform(frame);
        return frame;
    }
    private (double X, double Y) CurrentAnchor()
    {
        var clip = _presentedClip ?? FindClip(State);
        if (clip is null) return (0.5, 0.96);
        if (CurrentBlinkIndex() < 0 && clip.FrameAnchors.Length > 0)
        {
            var index = _presentedClip is not null ? _presentedIndex : CurrentFrameIndex;
            var anchor = clip.FrameAnchors[index]; return (anchor.X, anchor.Y);
        }
        return (clip.AnchorX, clip.AnchorY);
    }
    private Rect ImageBounds(Frame frame)
    {
        if (EdgePeek != ScreenEdge.None) return PeekImageBounds(frame);
        if (_waitingForFrame && ReferenceEquals(frame, _presentedFrame)) return _presentedBounds;
        var factor = Math.Min(ActualWidth / frame.Image.PixelWidth, ActualHeight / frame.Image.PixelHeight) * _displayScale;
        var width = frame.Image.PixelWidth * factor; var height = frame.Image.PixelHeight * factor;
        var anchor = CurrentAnchor();
        // Rounded atlas cells can differ by one pixel; keep their measured soles on one screen baseline.
        var top = State == PetState.Walking && HasFullCharacterWalk
            ? ActualHeight * (0.5 + _displayScale * 0.46) - height * anchor.Y
            : (ActualHeight - height) / 2 + height * (0.96 - anchor.Y);
        return new((ActualWidth - width) / 2 + width * (0.5 - anchor.X),
            top, width, height);
    }
    private Matrix ImageTransform(Frame frame)
    {
        if (EdgePeek != ScreenEdge.None) return Matrix.Identity;
        if (_waitingForFrame && ReferenceEquals(frame, _presentedFrame)) return _presentedMatrix;
        var bounds = ImageBounds(frame);
        var matrix = Matrix.Identity;
        if (State == PetState.Walking && Direction < 0) matrix.ScaleAt(-1, 1, ActualWidth / 2, ActualHeight / 2);
        if (State == PetState.Walking && HasMeasuredWalkingContacts)
        {
            if (HasFullCharacterWalk && FindClip(State) is { } clip && clip.WalkingStride is { } stride)
            {
                // Translate the intact character between authored frames; never deform or assemble its limbs.
                var framePosition = _stateTime * 1000 / clip.FrameMilliseconds;
                var fraction = framePosition - Math.Floor(framePosition + 1e-9);
                matrix.Translate(-Direction * Math.Max(0, fraction) * stride / clip.Frames.Length / 256 * bounds.Width, 0);
            }
            return CalibratedTransform(matrix, frame);
        }
        if (_clips.TryGetValue(State,out var authored) && authored.DisableProceduralMotion) return CalibratedTransform(matrix, frame);
        var breath = Math.Sin(_animationTime * (State == PetState.Sleeping ? 1.1 : 1.8));
        var feet = bounds.Y + bounds.Height * CurrentAnchor().Y;
        matrix.ScaleAt(1 + breath * 0.002, 1 + breath * 0.004, ActualWidth / 2, feet);
        if (State == PetState.Walking)
        {
            var clip = FindClip(State);
            var cycle = (clip?.Frames.Length ?? 8) * (clip?.FrameMilliseconds ?? 120) / 1000d;
            matrix.Translate(0, -Math.Abs(Math.Sin(_stateTime / cycle * Math.PI * 2)) * ActualHeight * 0.006);
        }
        else if (State == PetState.Dragging) matrix.RotateAt(Math.Sin(_stateTime * 4) * 2, ActualWidth / 2, bounds.Top + bounds.Height * 0.15);
        else if (State == PetState.Happy) matrix.Translate(0, -Math.Abs(Math.Sin(_stateTime * 7)) * ActualHeight * 0.015);
        return CalibratedTransform(matrix, frame);
    }
    internal double VisibleTop
    {
        get
        {
            if (CurrentFrame() is not { } frame) return ActualHeight * 0.035;
            var bounds = ImageBounds(frame);
            return ImageTransform(frame).Transform(new Point(bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height * frame.VisibleTop / frame.Image.PixelHeight)).Y;
        }
    }
    internal Rect VisibleBounds
    {
        get
        {
            if (CurrentFrame() is not { } frame) return new(ActualWidth * .18, ActualHeight * .05, ActualWidth * .64, ActualHeight * .87);
            var image = ImageBounds(frame); var pixels = frame.VisiblePixels;
            var visible = new Rect(image.X + pixels.X / frame.Image.PixelWidth * image.Width,
                image.Y + pixels.Y / frame.Image.PixelHeight * image.Height,
                pixels.Width / frame.Image.PixelWidth * image.Width, pixels.Height / frame.Image.PixelHeight * image.Height);
            visible.Transform(ImageTransform(frame));
            return visible;
        }
    }
    public bool IsOpaqueHit(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        if (CurrentFrame() is { } frame)
        {
            if (_transitionFrame is not null)
            {
                var blend = TransitionImage(frame);
                if (point.X < 0 || point.Y < 0 || point.X >= ActualWidth || point.Y >= ActualHeight) return false;
                var x = Math.Clamp((int)(point.X / ActualWidth * blend.PixelWidth), 0, blend.PixelWidth - 1);
                var y = Math.Clamp((int)(point.Y / ActualHeight * blend.PixelHeight), 0, blend.PixelHeight - 1);
                return _blendPixels![(y * blend.PixelWidth + x) * 4 + 3] >= 12;
            }
            var alpha = PixelAlpha(frame, point, ImageBounds(frame), ImageTransform(frame));
            if (_transitionFrame is { } old)
            {
                var t = Math.Clamp(_transitionTime / 0.18, 0, 1);
                var before = PixelAlpha(old, point, _transitionBounds, _transitionMatrix);
                // WPF draws the old image first, then the new one (source-over).
                alpha = alpha * t + before * (1 - t) * (1 - alpha * t / 255);
            }
            return alpha >= 12;
        }
        if (!IsPlaceholder) return false;
        var px = point.X / ActualWidth * 240; var py = point.Y / ActualHeight * 240;
        return Math.Pow((px - 120) / 77, 2) + Math.Pow((py - 129) / 91, 2) <= 1
            || (px >= 110 && px <= 130 && py >= 12 && py <= 42);
    }
    private static double PixelAlpha(Frame frame, Point point, Rect bounds, Matrix transform)
    {
        if (!transform.HasInverse || bounds.Width <= 0 || bounds.Height <= 0) return 0;
        transform.Invert(); point = transform.Transform(point);
        if (!bounds.Contains(point)) return 0;
        var x = (int)((point.X - bounds.X) / bounds.Width * frame.Image.PixelWidth);
        var y = (int)((point.Y - bounds.Y) / bounds.Height * frame.Image.PixelHeight);
        return x >= 0 && y >= 0 && x < frame.Image.PixelWidth && y < frame.Image.PixelHeight
            ? frame.Pixels[y * frame.Stride + x * 4 + 3] : 0;
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        IsOpaqueHit(parameters.HitPoint) ? new PointHitTestResult(this, parameters.HitPoint) : null;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        DrawFrame(dc);
    }

    internal void DrawFrame(DrawingContext dc)
    {
        if (DrawPlacementPresentation(dc)) return;
        if (CurrentFrame() is { } frame)
        {
            if (_transitionFrame is not null)
            {
                dc.DrawImage(TransitionImage(frame), new Rect(0, 0, ActualWidth, ActualHeight));
                return;
            }
            dc.PushTransform(new MatrixTransform(ImageTransform(frame)));
            dc.DrawImage(frame.Image, ImageBounds(frame));
            dc.Pop();
            return;
        }
        // A pending video frame is not a missing skin. Only a genuine load failure
        // or absent character art should display the geometric stand-in.
        if (!IsPlaceholder) return;
        // A neutral geometric stand-in: final character art is deliberately supplied later.
        dc.PushTransform(new ScaleTransform(ActualWidth / 240, ActualHeight / 240));
        var bob = State == PetState.Walking ? Math.Sin(_animationTime * 12) * 4 : Math.Sin(_animationTime * 2) * 2;
        if (State == PetState.Happy) bob -= Math.Abs(Math.Sin(_stateTime * 7)) * 9;
        dc.PushTransform(new TranslateTransform(0, bob));
        var edge = new Pen(Brush("#197F73"), 3);
        dc.DrawLine(edge, new(120, 34), new(120, 23));
        dc.DrawEllipse(Brush(State == PetState.Thinking ? "#FFD07C" : "#72DBC5"), edge, new(120, 17), 7, 7);
        var fill = new LinearGradientBrush(Color.FromRgb(202, 248, 235), Color.FromRgb(102, 206, 183), 90); fill.Freeze();
        dc.DrawEllipse(fill, edge, new(120, 129), 74, 86);
        dc.DrawEllipse(Brush("#DDFFF2"), null, new(90, 88), 18, 25);
        var ink = Brush("#21574F");
        var blink = _animationTime % 4.6 > 4.42;
        if (State == PetState.Sleeping || blink)
        {
            dc.DrawLine(new Pen(ink, 5), new(86, 118), new(103, 118));
            dc.DrawLine(new Pen(ink, 5), new(137, 118), new(154, 118));
        }
        else
        {
            var eyeHeight = State == PetState.Surprised ? 12 : 8;
            dc.DrawEllipse(ink, null, new(94, 115), 6, eyeHeight);
            dc.DrawEllipse(ink, null, new(146, 115), 6, eyeHeight);
        }
        if (State == PetState.Surprised || State == PetState.Talking)
            dc.DrawEllipse(ink, null, new(120, 142), 6, State == PetState.Talking ? 4 + Math.Abs(Math.Sin(_animationTime * 10)) * 4 : 8);
        else
        {
            var mouth = new StreamGeometry();
            using (var context = mouth.Open()) { context.BeginFigure(new(110, 139), false, false); context.QuadraticBezierTo(new(120, 149), new(130, 139), true, false); }
            dc.DrawGeometry(null, new Pen(ink, 3), mouth);
        }
        dc.DrawEllipse(Brush("#EFABB0"), null, new(76, 136), 10, 5);
        dc.DrawEllipse(Brush("#EFABB0"), null, new(164, 136), 10, 5);
        var label = new FormattedText(StateNames.Label(State), CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 13, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new(120 - label.Width / 2, 177));
        dc.Pop(); dc.Pop();
    }
    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
}

internal static class StateNames
{
    internal static string Label(PetState state) => state switch
    {
        PetState.Idle => "待机", PetState.Walking => "走动", PetState.Dragging => "被提起",
        PetState.Happy => "开心", PetState.Surprised => "惊讶", PetState.Thinking => "发呆",
        PetState.Talking => "打招呼", PetState.Sleeping => "睡觉", PetState.LyingDown => "躺下", PetState.Waking => "醒来", _ => "待机"
    };
}
