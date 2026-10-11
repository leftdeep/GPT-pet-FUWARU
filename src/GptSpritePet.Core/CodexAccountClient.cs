using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GptSpritePet.Core;

public sealed class UsageRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Account-only JSON-RPC bridge. It never creates threads or submits AI requests.</summary>
public sealed class CodexAccountClient : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId, _disposed;
    public event Action<string>? Notification;
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) &&
            Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(configured) : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try { var file = Path.Combine(directory.Trim('"'), "codex.exe"); if (File.Exists(file)) return file; }
            catch (ArgumentException) { }
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
    public CodexAccountClient(string executable, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        _process = Process.Start(start) ?? throw new IOException("Could not start account connection.");
        _ = ReadAsync(); _ = DrainErrorsAsync();
    }
    public async Task InitializeAsync(CancellationToken cancellation)
    {
        await CallAsync("initialize", new { clientInfo = new { name = "gpt_sprite_pet", title = "Dragon Girl Desktop Pet", version = "0.40" },
            capabilities = new { experimentalApi = true } }, cancellation).ConfigureAwait(false);
        await SendAsync(new { method = "initialized", @params = new { } }, cancellation).ConfigureAwait(false);
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellation)
    {
        if (method is not ("initialize" or "account/read" or "account/usage/read" or "account/rateLimits/read" or
            "account/login/start" or "account/login/cancel")) throw new ArgumentException("Account method is not allowed.", nameof(method));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = source;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, cancellation).ConfigureAwait(false);
            return await source.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(object value, CancellationToken cancellation)
    {
        await _write.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellation).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Account response too large.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var id) && _pending.TryGetValue(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                            completion.TrySetException(new UsageRpcException(error.GetProperty("code").GetInt32(),
                                error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "Account RPC error" : "Account RPC error"));
                        else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    }
                    else if (root.TryGetProperty("method", out var name) && name.ValueKind == JsonValueKind.String)
                        Notification?.Invoke(name.GetString()!);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException) { }
        finally { foreach (var item in _pending.Values) item.TrySetException(new IOException("Account connection closed.")); }
    }
    private async Task DrainErrorsAsync()
    {
        // Do not log raw provider diagnostics: they can contain private account data.
        try { while (await _process.StandardError.ReadLineAsync(_stop.Token).ConfigureAwait(false) is not null) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        foreach (var pending in _pending.Values) pending.TrySetException(new ObjectDisposedException(nameof(CodexAccountClient)));
        try { _process.StandardInput.Close(); if (!_process.WaitForExit(150)) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
        _process.Dispose();
    }
}
