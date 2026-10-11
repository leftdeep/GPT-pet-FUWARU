using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GptSpritePet.Core;

public enum ResetNoticeKind { Preview, Completed, Extra, Candidate }

public sealed record ResetNotice(string Id, DateTimeOffset At, ResetNoticeKind Kind, string Summary,
    string? Url, string Source, bool ExactTime = true)
{
    public string Key => Id + ":" + Kind;
    public string Label => Kind switch { ResetNoticeKind.Preview => "重置预告", ResetNoticeKind.Completed => "完成公告",
        ResetNoticeKind.Extra => "重置卡 / 额外额度", _ => "待确认" };
}

public sealed record ResetFeedBatch(ResetNotice[] Events, string Source, bool Stale = false);
public interface IResetFeedSource { Task<ResetFeedBatch> FetchAsync(CancellationToken cancellation); }

public sealed record ResetWatchSnapshot
{
    public bool Enabled { get; init; }
    public bool Busy { get; init; }
    public bool IsOwner { get; init; }
    public bool Initialized { get; init; }
    public bool SourceStale { get; init; }
    public string Source { get; init; } = "codex-reset.com";
    public string Message { get; init; } = "等待首次检查";
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessfulCheckAt { get; init; }
    public DateTimeOffset? NextCheckAt { get; init; }
    public ResetNotice? LastReset { get; init; }
    public ResetNotice[] Events { get; init; } = [];
    public ResetNotice[] Pending { get; init; } = [];
}

public static class ResetFeedParser
{
    private static readonly Regex Tweet = new(@"https://(?:www\.)?(?:x\.com|twitter\.com)/thsottiaux/status/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Reset = new(@"\breset(?:s|ting|ing)?\b|重置", RegexOptions.IgnoreCase);
    private static readonly Regex Extra = new(@"\bbanked\b|reset\s+cards?|one[- ]time|重置卡|额外额度", RegexOptions.IgnoreCase);
    private static readonly Regex Completed = new(@"resets?\s+(?:(?:has|have|is|are)\s+)?(?:all\s+)?(?:been\s+)?propagated|all\s+reset\s+for\s+everyone|(?:have|has|now|just)\s+(?:been\s+)?reset\s+(?:usage|limits|all)|(?:usage|limits).{0,40}(?:have been|has been|are now)\s+reset|reset.{0,30}(?:completed|done|rolled out)|重置.{0,12}(?:完成|生效)|已.{0,8}重置", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Upcoming = new(@"\b(?:will|we.ll|going to|promised|promise|landing|lands|coming|tomorrow|tonight)\b|next\s+(?:\d+\s+)?(?:hours?|days?|week)|即将|将.{0,8}重置|明天|今晚", RegexOptions.IgnoreCase);

    public static string? TweetUrl(string? value)
    {
        var match = Tweet.Match(value ?? "");
        return match.Success ? "https://x.com/thsottiaux/status/" + match.Groups[1].Value : null;
    }
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static IEnumerable<JsonElement> Rows(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static (DateTimeOffset? At, bool Exact) Time(JsonElement row, params string[] fields)
    {
        foreach (var field in fields)
        {
            var text = String(row, field);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return (at, text.Contains('T'));
        }
        return (null, false);
    }
    public static ResetNoticeKind? Classify(string text)
    {
        if (!Reset.IsMatch(text)) return null;
        // A question, denial or joke cannot promise a future refill.
        if (Regex.IsMatch(text, @"\b(?:no reset|not resetting|won.t reset|will not reset|if we|might reset|maybe reset)\b|(?:will|when).{0,40}reset.{0,20}\?|\b(?:not|never|hasn.t|haven.t).{0,25}(?:reset|propagat)|\bno\s+(?:banked|free|one[- ]time)\s+reset|\b(?:not|won.t|will not)\s+(?:be\s+)?(?:giv(?:e|ing)|load(?:ing)?|grant(?:ing)?)\b.{0,30}reset|cancelled|canceled|不会重置|是否|会不会|可能重置|不会赠送|不.{0,4}送.{0,6}重置卡", RegexOptions.IgnoreCase))
            return ResetNoticeKind.Candidate;
        if (Extra.IsMatch(text)) return ResetNoticeKind.Extra;
        if (Completed.IsMatch(text)) return ResetNoticeKind.Completed;
        return Upcoming.IsMatch(text) ? ResetNoticeKind.Preview : ResetNoticeKind.Candidate;
    }
    private static string Summary(ResetNoticeKind kind) => kind switch
    {
        ResetNoticeKind.Preview => "Tibo 公告提及后续重置安排。具体时间与适用范围请查看原帖。",
        ResetNoticeKind.Completed => "公开公告称额度重置已完成；本账号余额仍以用量页面为准。",
        ResetNoticeKind.Extra => "Tibo 公告涉及赠送重置卡或额外额度。领取和使用方式请查看原帖。",
        _ => "这条记录提及重置，但还不能确认是明确预告或完成公告。"
    };
    public static ResetFeedBatch ParsePrimary(JsonElement timeline, JsonElement feed)
    {
        if (!timeline.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array
            || !feed.TryGetProperty("tweets", out var tweets) || tweets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("公告接口结构发生变化");
        if (feed.TryGetProperty("profile", out var profile) && String(profile, "handle") is { Length: > 0 } handle && handle != "thsottiaux")
            throw new InvalidDataException("公告作者不匹配");
        var found = new Dictionary<string, ResetNotice>();
        void Add(JsonElement row, bool isTimeline)
        {
            var url = TweetUrl(String(row, "url"));
            var time = Time(row, isTimeline ? "announced_at" : "at", "published_at", "date");
            if (url is null || time.At is null) return;
            var text = String(row, isTimeline ? "summary" : "text");
            var kind = Classify(text);
            var group = String(row, "group");
            if (isTimeline && group == "reset" && String(row, "announcement_state") == "announced" && !Extra.IsMatch(text))
                kind = ResetNoticeKind.Completed;
            else if (isTimeline && (group is "credits" or "unlock") && kind != ResetNoticeKind.Candidate) kind = ResetNoticeKind.Extra;
            if (kind is null) return;
            var id = Tweet.Match(url).Groups[1].Value;
            var notice = new ResetNotice(id, time.At.Value, kind.Value, Summary(kind.Value), url, "codex-reset.com", time.Exact);
            // Feed text is often fuller than timeline summaries, but never demote a confirmed timeline entry.
            if (!found.TryGetValue(id, out var old) || (old.Kind != ResetNoticeKind.Completed
                && ((int)notice.Kind < (int)old.Kind || notice.Kind == ResetNoticeKind.Completed)))
                found[id] = notice;
        }
        foreach (var row in Rows(timeline, "events")) Add(row, true);
        foreach (var row in Rows(feed, "tweets")) Add(row, false);
        var stale = feed.TryGetProperty("stale", out var staleValue) && staleValue.ValueKind == JsonValueKind.True;
        return new(found.Values.OrderByDescending(e => e.At).ToArray(), "codex-reset.com", stale);
    }
    public static ResetFeedBatch ParseFallback(JsonElement root)
    {
        if (!root.TryGetProperty("events", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("备用公告接口结构发生变化");
        var events = new List<ResetNotice>();
        foreach (var row in Rows(root, "events"))
        {
            var time = Time(row, "published_at", "date");
            if (time.At is null) continue;
            var url = TweetUrl(String(row, "source"));
            var text = String(row, "summary") + " " + String(row, "title");
            var kind = Classify(text);
            if (kind is null) continue;
            // Manual references without an original tweet remain useful history, never an urgent claim.
            if (url is null) kind = ResetNoticeKind.Candidate;
            var id = url is null ? "fallback-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..20]
                : Tweet.Match(url).Groups[1].Value;
            events.Add(new(id, time.At.Value, kind.Value, Summary(kind.Value), url, "codexreset.today", time.Exact));
        }
        return new(events.DistinctBy(e => e.Id).OrderByDescending(e => e.At).ToArray(), "codexreset.today");
    }
}

public sealed class ResetRateLimitException(TimeSpan retryAfter) : IOException("数据源要求稍后重试")
{ public TimeSpan RetryAfter { get; } = retryAfter; }

public sealed class PublicResetFeedSource : IResetFeedSource, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    public PublicResetFeedSource(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(); _ownsClient = client is null;
    }
    private async Task<JsonDocument> GetAsync(string url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("DragonGirlDesktopPet/0.40 (local-personal-desktop-app)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(15);
            throw new ResetRateLimitException(delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1));
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 512 * 1024) throw new InvalidDataException("公告响应过大");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 512 * 1024) throw new InvalidDataException("公告响应过大");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
        }
        return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
    public async Task<ResetFeedBatch> FetchAsync(CancellationToken cancellation)
    {
        try
        {
            // Keep both documents scoped, including when the other endpoint fails.
            using var timeline = await GetAsync("https://codex-reset.com/api/timeline", cancellation).ConfigureAwait(false);
            using var feed = await GetAsync("https://codex-reset.com/api/feed", cancellation).ConfigureAwait(false);
            return ResetFeedParser.ParsePrimary(timeline.RootElement, feed.RootElement);
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (ex is ResetRateLimitException) throw;
            using var fallback = await GetAsync("https://codexreset.today/reset-events.json", cancellation).ConfigureAwait(false);
            return ResetFeedParser.ParseFallback(fallback.RootElement);
        }
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

public sealed class ResetWatchCache
{
    public int Version { get; set; } = 1;
    public bool Initialized { get; set; }
    public string Source { get; set; } = "codex-reset.com";
    public bool SourceStale { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessfulCheckAt { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public ResetNotice? LastReset { get; set; }
    public List<ResetNotice> Events { get; set; } = [];
    public List<ResetNotice> Pending { get; set; } = [];
    public List<string> Seen { get; set; } = [];
}

public sealed class ResetWatchService : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private static bool CanNotify(ResetNotice notice) => notice.Kind is ResetNoticeKind.Preview or ResetNoticeKind.Completed or ResetNoticeKind.Extra;
    // Extra notices used to be marked seen for history only. A separate notification key lets
    // a recent card announce once after upgrading, without changing its acknowledgment key.
    private static string SeenKey(ResetNotice notice) => notice.Kind == ResetNoticeKind.Extra ? "alert:" + notice.Key : notice.Key;
    private readonly string _directory, _cacheFile;
    private readonly IResetFeedSource _source;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Timer? _timer;
    private FileStream? _lease;
    private ResetWatchCache _cache;
    private volatile ResetWatchSnapshot _snapshot = new();
    private volatile bool _enabled, _disposed;
    private int _failures;
    public ResetWatchSnapshot Snapshot => _snapshot;
    public event Action<ResetWatchSnapshot>? Changed;
    public ResetWatchService(string directory, IResetFeedSource? source = null, Func<DateTimeOffset>? now = null, bool schedule = true)
    {
        _directory = Path.GetFullPath(directory); _cacheFile = Path.Combine(_directory, "reset-watch.json");
        _source = source ?? new PublicResetFeedSource(); _now = now ?? (() => DateTimeOffset.UtcNow);
        _cache = LoadCache(); Publish("等待首次检查");
        if (schedule) _timer = new Timer(_ => { _ = CheckAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public void Configure(bool enabled)
    {
        if (_disposed) return;
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled) { _cache.NextCheckAt = _now(); Publish("等待检查"); _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
        else
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_gate.Wait(0)) { try { ReleaseLease(); } finally { _gate.Release(); } }
            Publish("监测已暂停");
        }
    }
    private ResetWatchCache LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return new();
            if (new FileInfo(_cacheFile).Length > 100 * 1024) return new();
            var cache = JsonSerializer.Deserialize<ResetWatchCache>(File.ReadAllText(_cacheFile));
            if (cache is null || cache.Version != 1 || cache.Events is null || cache.Pending is null || cache.Seen is null) return new();
            // Treat cached public data as untrusted too; reject bad URLs, ids and future dates.
            bool Valid(ResetNotice e) => e is not null && Enum.IsDefined(e.Kind) && e.Id is { Length: > 0 and <= 80 } && e.Summary is { Length: <= 400 }
                && e.Source is "codex-reset.com" or "codexreset.today" && e.At <= _now().AddMinutes(5)
                && (e.Url is null || ResetFeedParser.TweetUrl(e.Url) == e.Url);
            cache.Events = cache.Events.Where(Valid).Take(30).ToList();
            cache.Pending = cache.Pending.Where(e => Valid(e) && CanNotify(e)).Take(5).ToList();
            if (cache.LastReset is not null && (!Valid(cache.LastReset) || cache.LastReset.Kind != ResetNoticeKind.Completed)) cache.LastReset = null;
            cache.Seen = cache.Seen.Where(s => s is not null && s.Length < 100).TakeLast(512).ToList();
            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    private bool AcquireLease()
    {
        if (_lease is not null) return true;
        try
        {
            Directory.CreateDirectory(_directory);
            _lease = new FileStream(Path.Combine(_directory, "reset-watch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private void ReleaseLease() { _lease?.Dispose(); _lease = null; }
    private void SaveCache()
    {
        Directory.CreateDirectory(_directory);
        var data = JsonSerializer.SerializeToUtf8Bytes(_cache);
        if (data.Length > 100 * 1024) throw new IOException("监测缓存超过上限");
        var temporary = _cacheFile + ".tmp";
        File.WriteAllBytes(temporary, data); File.Move(temporary, _cacheFile, true);
    }
    private void Publish(string message, bool busy = false)
    {
        _snapshot = new() { Enabled = _enabled, Busy = busy, IsOwner = _lease is not null, Initialized = _cache.Initialized,
            Source = _cache.Source, SourceStale = _cache.SourceStale, Message = message,
            LastAttemptAt = _cache.LastAttemptAt, LastSuccessfulCheckAt = _cache.LastSuccessfulCheckAt, NextCheckAt = _cache.NextCheckAt,
            LastReset = _cache.LastReset, Events = _cache.Events.ToArray(), Pending = _lease is null ? [] : _cache.Pending.ToArray() };
        Changed?.Invoke(_snapshot);
    }
    private void Schedule()
    {
        if (!_enabled || _disposed) return;
        var delay = (_cache.NextCheckAt ?? _now().Add(CheckInterval)) - _now();
        _timer?.Change(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }
    public async Task CheckAsync(bool force = false)
    {
        if (_disposed || !_enabled || !await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var alreadyOwner = _lease is not null;
            if (!AcquireLease())
            {
                _cache = LoadCache(); _cache.NextCheckAt = _now().AddMinutes(5);
                Publish("由另一个桌宠实例监测"); return;
            }
            if (!alreadyOwner) _cache = LoadCache();
            var now = _now();
            if (alreadyOwner && !force && _cache.NextCheckAt > now) return;
            // Manual refresh is also bounded by the source's once-per-minute limit.
            if (_cache.LastAttemptAt is { } last && now < last.AddMinutes(1))
            { if (_cache.NextCheckAt < last.AddMinutes(1)) _cache.NextCheckAt = last.AddMinutes(1); Publish("刚刚已检查，请稍后再试"); return; }
            _cache.LastAttemptAt = now; _cache.NextCheckAt = now.Add(CheckInterval);
            Publish("正在检查公告…", true);
            var batch = await _source.FetchAsync(_stop.Token).ConfigureAwait(false);
            if (_disposed || !_enabled) return;
            var baseline = !_cache.Initialized;
            var seen = _cache.Seen.ToHashSet(StringComparer.Ordinal);
            var incoming = batch.Events.Where(e => e.At <= now.AddMinutes(5)).DistinctBy(e => e.Id).ToArray();
            var completedAt = incoming.Where(e => e.Kind == ResetNoticeKind.Completed).OrderByDescending(e => e.At).FirstOrDefault();
            if (completedAt is not null && (_cache.LastReset is null || completedAt.At > _cache.LastReset.At)) _cache.LastReset = completedAt;
            foreach (var notice in incoming.OrderBy(e => e.At))
            {
                if (!baseline && !batch.Stale && !seen.Contains(SeenKey(notice)) && notice.ExactTime && notice.At >= now.AddHours(-48)
                    && CanNotify(notice)
                    && !(notice.Kind == ResetNoticeKind.Preview && _cache.LastReset is { } reset && reset.At >= notice.At))
                    _cache.Pending.Add(notice);
                seen.Add(SeenKey(notice));
            }
            _cache.Seen = _cache.Seen.Concat(incoming.Where(e => !batch.Stale || baseline || e.Kind == ResetNoticeKind.Candidate)
                .Select(SeenKey)).Distinct().TakeLast(512).ToList();
            _cache.Pending = _cache.Pending.DistinctBy(e => e.Key).OrderByDescending(e => e.At).Take(5).ToList();
            _cache.Events = incoming.Concat(_cache.Events).DistinctBy(e => e.Id).OrderByDescending(e => e.At).Take(30).ToList();
            _cache.Initialized = true; _cache.LastSuccessfulCheckAt = _now(); _cache.Source = batch.Source; _cache.SourceStale = batch.Stale; _failures = 0;
            SaveCache();
            Publish(batch.Stale ? "数据源标记为过期，已保留记录" : batch.Source == "codexreset.today" ? "已通过备用数据源检查" : "已检查，等待新公告");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or OperationCanceledException)
        {
            _failures++;
            var delay = ex is ResetRateLimitException limit ? limit.RetryAfter : TimeSpan.FromMinutes(_failures == 1 ? 5 : _failures == 2 ? 15 : 60);
            _cache.NextCheckAt = _now().Add(delay > TimeSpan.FromMinutes(1) ? delay : TimeSpan.FromMinutes(1));
            try { SaveCache(); } catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException) { }
            Publish("检查暂时失败，保留上次记录并稍后重试");
        }
        finally
        {
            if (_disposed || !_enabled) ReleaseLease();
            _gate.Release(); Schedule();
        }
    }
    public async Task AcknowledgeAsync(string key)
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lease is null) { if (!AcquireLease()) return; _cache = LoadCache(); }
            var previous = _cache.Pending.ToList();
            _cache.Pending.RemoveAll(e => e.Key == key);
            try { SaveCache(); }
            catch { _cache.Pending = previous; throw; }
            Publish("公告已读");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Publish("暂时无法保存已读状态，请稍后重试"); }
        finally { if (!_enabled) ReleaseLease(); _gate.Release(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer?.Dispose(); _stop.Cancel();
        if (_gate.Wait(0)) { try { ReleaseLease(); } finally { _gate.Release(); } }
        // HttpClient disposal cancels its own requests. Keep cancellation/gate alive until the in-flight callback returns.
        if (_source is IDisposable disposable) disposable.Dispose();
    }
}
