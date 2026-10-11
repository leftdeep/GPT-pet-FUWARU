using System.Text.Json;

namespace GptSpritePet.Core;

public sealed class AccountUsageService(string dataDirectory) : IDisposable
{
    private CancellationTokenSource? _stop;
    private CodexAccountClient? _client;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private PetSettings _settings = new();
    private int _generation;
    private string? _loginId;
    private string? _authStamp;
    private bool _disposed;
    private AccountUsageSnapshot _snapshot = new();
    private readonly object _snapshotGate = new();
    private LocalTokenUsageReader? _localReader;
    private string? _selectedTaskId;
    public AccountUsageSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action<AccountUsageSnapshot>? Changed;
    public event Action<ObservedTokenUsage>? TokenObserved;
    public void Configure(PetSettings settings)
    {
        settings = settings.Normalize();
        if (_stop is not null && _settings.UsageEnabled == settings.UsageEnabled && _settings.CodexExecutablePath == settings.CodexExecutablePath)
        { var intervalChanged = _settings.UsageRefreshSeconds != settings.UsageRefreshSeconds; _settings = settings; if (intervalChanged) RequestRefresh(); return; }
        _settings = settings; var generation = ++_generation;
        _stop?.Cancel(); _client?.Dispose(); _client = null; _loginId = null;
        if (_disposed) return;
        _stop = new CancellationTokenSource();
        Publish(Snapshot with { State = settings.UsageEnabled ? UsageConnectionState.Connecting : UsageConnectionState.Disabled,
            Message = settings.UsageEnabled ? "正在连接账号…" : "账号连接已关闭 · 本机 Token 统计仍在更新", Busy = settings.UsageEnabled });
        // Client discovery and redirected-process startup can run synchronously before the first await.
        // Keep those operations off the WPF dispatcher so the pet remains visible and responsive.
        var stopToken = _stop.Token;
        if (settings.UsageEnabled) _ = Task.Run(() => RunAsync(generation, stopToken));
        _ = Task.Run(() => RunLocalAsync(generation, stopToken));
    }
    public void RequestRefresh()
    {
        if (_disposed) return;
        try { if (_refreshSignal.CurrentCount == 0) _refreshSignal.Release(); } catch (SemaphoreFullException) { }
    }
    public void SelectTask(string? id) { _selectedTaskId = id; PublishLocal(Snapshot.LocalUsage); }
    private void Publish(AccountUsageSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            snapshot = snapshot with { LocalUsage = _snapshot.LocalUsage, SelectedTaskId = _selectedTaskId };
            Volatile.Write(ref _snapshot, snapshot);
        }
        Changed?.Invoke(snapshot);
    }
    private void PublishLocal(LocalTokenUsage? usage)
    {
        AccountUsageSnapshot snapshot;
        lock (_snapshotGate)
        {
            snapshot = _snapshot with { LocalUsage = usage, SelectedTaskId = _selectedTaskId };
            Volatile.Write(ref _snapshot, snapshot);
        }
        Changed?.Invoke(snapshot);
    }
    private async Task RunLocalAsync(int generation, CancellationToken token)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        LocalTokenUsageReader reader;
        lock (_snapshotGate)
        {
            if (_localReader is null)
            {
                _localReader = new LocalTokenUsageReader(home);
                _localReader.TokenObserved += usage => { if (!_disposed) TokenObserved?.Invoke(usage); };
            }
            reader = _localReader;
        }
        while (!token.IsCancellationRequested && generation == _generation && !_disposed)
        {
            try
            {
                var usage = reader.Read(DateTimeOffset.Now);
                if (generation == _generation && !token.IsCancellationRequested) PublishLocal(usage);
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (Recoverable(ex))
            {
                if (generation != _generation || token.IsCancellationRequested) break;
                PublishLocal(new(DateOnly.FromDateTime(DateTime.Now), null, [], DateTimeOffset.Now, "本机任务用量暂不可读", false));
                try { await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task<CodexAccountClient> EnsureClientAsync(int generation, CancellationToken token)
    {
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException(token);
            if (_client is not null) return _client;
            var executable = CodexAccountClient.FindExecutable(_settings.CodexExecutablePath) ?? throw new FileNotFoundException("Codex account connector missing.");
            var client = new CodexAccountClient(executable, AppContext.BaseDirectory);
            try
            {
                await client.InitializeAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                client.Notification += method =>
                {
                    if (generation != _generation || token.IsCancellationRequested) return;
                    if (method is "account/updated" or "account/login/completed")
                    {
                        if (method == "account/login/completed") _loginId = null;
                        Publish(new() { State = UsageConnectionState.Connecting, Message = "正在确认登录账号…", Busy = true });
                        RequestRefresh();
                    }
                    else if (method == "account/rateLimits/updated" && !Snapshot.Busy) RequestRefresh();
                };
                _client = client; return client;
            }
            catch { client.Dispose(); throw; }
        }
        finally { _connectionGate.Release(); }
    }
    private async Task RunAsync(int generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested && generation == _generation && !_disposed)
        {
            try
            {
                Publish(Snapshot with { Busy = true });
                // Detect a desktop account switch without reading login credentials.
                var stamp = ReadAuthStamp();
                if (_authStamp is not null && stamp != _authStamp)
                {
                    _client?.Dispose(); _client = null;
                    Publish(new() { State = UsageConnectionState.Connecting, Message = "登录状态已变化，正在重新读取账号…", Busy = true });
                }
                _authStamp = stamp;
                var client = await EnsureClientAsync(generation, token).ConfigureAwait(false);
                var account = AccountUsageParser.Account(await client.CallAsync("account/read", new { refreshToken = false }, token).ConfigureAwait(false));
                token.ThrowIfCancellationRequested();
                if (account is null)
                    Publish(new() { State = UsageConnectionState.SignedOut, Message = "尚未登录，请登录 ChatGPT" });
                else if (account.Type is not ("chatgpt" or "chatgptAuthTokens" or "agentIdentity" or "personalAccessToken"))
                    Publish(new() { State = UsageConnectionState.Unsupported, Account = account, Message = "当前登录方式不提供 ChatGPT 账号用量，请使用 ChatGPT 登录" });
                else
                {
                    var old = Snapshot.Account?.Identity == account.Identity && account.Identity.Length > 0 ? Snapshot :
                        ReadCache(account) ?? new AccountUsageSnapshot { Account = account };
                    var result = old with { Account = account, Busy = false, LimitsAvailable = false };
                    string? tokenError = null, limitError = null;
                    try
                    {
                        var data = await client.CallAsync("account/usage/read", null, token).ConfigureAwait(false);
                        result = result with { Tokens = AccountUsageParser.Tokens(data), TokensUpdatedAt = DateTimeOffset.Now };
                    }
                    catch (Exception ex) when (Recoverable(ex)) { tokenError = Explain(ex, true); }
                    try
                    {
                        var data = await client.CallAsync("account/rateLimits/read", null, token).ConfigureAwait(false);
                        result = result with { Quotas = AccountUsageParser.Quotas(data), LimitsUpdatedAt = DateTimeOffset.Now, LimitsAvailable = true };
                    }
                    catch (Exception ex) when (Recoverable(ex)) { limitError = Explain(ex, false); }
                    token.ThrowIfCancellationRequested();
                    result = result with
                    {
                        State = tokenError is null && limitError is null ? UsageConnectionState.Ready :
                            tokenError is not null && limitError is not null ? UsageConnectionState.Offline : UsageConnectionState.Partial,
                        Message = tokenError is null && limitError is null ? "已连接 · 自动更新" :
                            string.Join("；", new[] { tokenError, limitError }.Where(x => x is not null))
                    };
                    Publish(result); WriteCache(result);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (Recoverable(ex))
            {
                if (generation != _generation || _disposed) break;
                _client?.Dispose(); _client = null;
                Publish(Snapshot with { Busy = false, State = ex is FileNotFoundException ? UsageConnectionState.MissingClient : UsageConnectionState.Offline,
                    Message = ex is FileNotFoundException ? "未找到本机 Codex，请在设置中选择连接程序" : "连接中断，稍后自动重试；已有数据仅供查看" });
            }
            try { await _refreshSignal.WaitAsync(TimeSpan.FromSeconds(_settings.UsageRefreshSeconds), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
    public async Task<Uri> StartLoginAsync()
    {
        if (!_settings.UsageEnabled) throw new InvalidOperationException("请先开启账号用量显示。" );
        var token = _stop?.Token ?? throw new InvalidOperationException("账号连接尚未启动。" );
        var client = await EnsureClientAsync(_generation, token).ConfigureAwait(false);
        if (_loginId is not null) await CancelLoginAsync().ConfigureAwait(false);
        var result = await client.CallAsync("account/login/start", new { type = "chatgpt", useHostedLoginSuccessPage = true }, token).ConfigureAwait(false);
        var url = result.GetProperty("authUrl").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 ||
            uri.Host is not ("auth.openai.com" or "chatgpt.com")) throw new InvalidOperationException("授权页面地址未通过检查。" );
        _loginId = result.GetProperty("loginId").GetString();
        RequestRefresh(); return uri;
    }
    public async Task CancelLoginAsync()
    {
        var id = _loginId; _loginId = null;
        if (id is null || _client is null || _stop is null) return;
        try { await _client.CallAsync("account/login/cancel", new { loginId = id }, _stop.Token).ConfigureAwait(false); }
        catch (Exception ex) when (Recoverable(ex) || ex is OperationCanceledException) { }
        RequestRefresh();
    }
    private AccountUsageSnapshot? ReadCache(UsageAccount account)
    {
        if (account.Identity.Length == 0) return null;
        try
        {
            var path = Path.Combine(dataDirectory, "usage-cache.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return null;
            var saved = JsonSerializer.Deserialize<AccountUsageSnapshot>(File.ReadAllText(path));
            return saved?.Account?.Identity == account.Identity ? saved with { State = UsageConnectionState.Offline, Busy = false, LimitsAvailable = false } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private void WriteCache(AccountUsageSnapshot snapshot)
    {
        if (snapshot.Account?.Identity.Length is not > 0) return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, "usage-cache.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(snapshot with { LocalUsage = null, SelectedTaskId = null })); File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or TimeoutException or
        UsageRpcException or System.ComponentModel.Win32Exception or InvalidOperationException;
    private static string ReadAuthStamp()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var info = new FileInfo(Path.Combine(root, "auth.json"));
            return info.Exists ? info.LastWriteTimeUtc.Ticks + "|" + info.Length : "none";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unavailable"; }
    }
    private static string Explain(Exception ex, bool tokens)
    {
        var name = tokens ? "Token 统计" : "额度";
        if (ex is UsageRpcException { Code: -32601 }) return "本机连接程序暂不支持" + name + "，请更新 Codex";
        return name + "暂时无法更新，保留上次记录";
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ++_generation;
        _stop?.Cancel(); _client?.Dispose(); _client = null;
    }
}
