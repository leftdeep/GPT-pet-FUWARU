using System.Text;
using System.Text.Json;

namespace GptSpritePet.Core;

public sealed record ActiveTaskUsage(string Id, string Title, string? TurnId, long? TotalTokens,
    long? TurnTokens, DateTimeOffset StartedAt, DateTimeOffset? UpdatedAt)
{
    public OutputRateSample? OutputRate { get; init; }
}
public sealed record LocalTokenUsage(DateOnly Date, long? TodayTokens, ActiveTaskUsage[] Tasks,
    DateTimeOffset UpdatedAt, string Message, bool Available);
public sealed record LocalUsageScanMetrics(long FullScans, long DiscoveredFiles, long FilePolls);

/// <summary>Read-only incremental observer of Codex usage events, never of credentials.</summary>
public sealed class LocalTokenUsageReader(string home, TimeZoneInfo? timeZone = null)
{
    private readonly TimeZoneInfo _zone = timeZone ?? TimeZoneInfo.Local;
    private readonly Dictionary<string, Rollout> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private DateTime _indexStamp;
    private readonly object _gate = new();
    private DateTimeOffset _nextFullScan;
    private long _fullScans, _discoveredFiles, _filePolls;
    public LocalUsageScanMetrics ScanMetrics { get { lock (_gate) return new(_fullScans, _discoveredFiles, _filePolls); } }
    public event Action<ObservedTokenUsage>? TokenObserved;
    private sealed class Rollout
    {
        public long Offset;
        public string Path = "";
        public readonly MemoryStream Pending = new();
        public bool SkipLine, Subagent, Running, Missing;
        public bool Initialized;
        public readonly OutputRateTracker Rate = new();
        public string? Id, TurnId;
        public long? Total, TurnTokens;
        public long? Input, Cached, Output;
        public ObservedTokenUsage? Observed;
        public DateTimeOffset Started, LastEvent;
        public DateTimeOffset? Updated;
        public readonly Dictionary<DateOnly, long> Daily = new();
    }

    public LocalTokenUsage Read(DateTimeOffset now)
    {
        lock (_gate) return ReadCore(now);
    }
    private LocalTokenUsage ReadCore(DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);
        if (!Directory.Exists(home)) return new(date, null, [], now, "未找到本机 Codex 用量记录", false);
        var errors = 0;
        UpdateTitles();
        var todayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), _zone.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue))).UtcDateTime;
        // Poll known logs every 2 seconds; discover the whole history only once a minute.
        // Recent UTC folders and flat session layouts still discover new tasks on each poll.
        if (now >= _nextFullScan)
        {
            _fullScans++;
            foreach (var directory in new[] { "sessions", "archived_sessions" })
                Discover(Path.Combine(home, directory), true, todayStart, ref errors);
            _nextFullScan = now.AddMinutes(1);
        }
        else
        {
            Discover(Path.Combine(home, "sessions"), false, todayStart, ref errors);
            foreach (var day in new[] { now.UtcDateTime.Date.AddDays(-1), now.UtcDateTime.Date, now.UtcDateTime.Date.AddDays(1) })
                Discover(Path.Combine(home, "sessions", day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd")),
                    false, todayStart, ref errors);
        }
        byte[]? buffer = null;
        foreach (var (key, tracked) in _files.ToArray())
        {
            var state = tracked;
            try
            {
                _filePolls++;
                var info = new FileInfo(state.Path);
                if (!info.Exists) { if (!state.Missing) _nextFullScan = now; state.Missing = true; continue; }
                state.Missing = false;
                if (info.Length == state.Offset) continue;
                if (info.Length < state.Offset) _files[key] = state = new() { Path = state.Path };
                using var stream = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Position = state.Offset;
                buffer ??= new byte[65536]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                { Feed(state, buffer.AsSpan(0, count)); state.Offset = stream.Position; }
                state.Offset = stream.Position;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
            finally { state.Rate.CompleteRead(now, state.Initialized); state.Initialized = true; }
        }
        // One id may occur at two paths during an archive operation. Count it only once.
        var states = _files.Values.Where(s => s.Id is not null).GroupBy(s => s.Id)
            .Select(g => g.OrderByDescending(s => s.LastEvent).First()).ToArray();
        var records = states.Where(s => s.Daily.ContainsKey(date)).ToArray();
        long? total = null;
        try { if (records.Length > 0) total = records.Sum(s => s.Daily[date]); } catch (OverflowException) { errors++; }
        var tasks = states.Where(s => s.Running && !s.Subagent && now - s.LastEvent < TimeSpan.FromDays(1))
            .OrderByDescending(s => s.Started).ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new ActiveTaskUsage(s.Id!, Title(s.Id!), s.TurnId, s.Total, s.TurnTokens, s.Started, s.Updated)
                { OutputRate = s.Rate.Sample }).ToArray();
        return new(date, total, tasks, now, errors > 0 ? "部分本机记录暂不可读，显示已读取用量" :
            "本机 Codex 记录 · 含缓存输入和输出 · 每 2 秒检查，历史目录每分钟校对", errors == 0);
    }

    private void Discover(string root, bool recursive, DateTime todayStart, ref int errors)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                { RecurseSubdirectories = recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                _discoveredFiles++;
                var key = Path.GetFileName(path); // Archive moves keep their cursor and counters.
                if (_files.TryGetValue(key, out var state)) { state.Path = path; state.Missing = false; }
                else if (File.GetLastWriteTimeUtc(path) >= todayStart) _files[key] = new() { Path = path };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors++; }
    }

    private string Title(string id) => _titles.TryGetValue(id, out var title) ? title : "任务 " + id[..Math.Min(8, id.Length)];
    private void UpdateTitles()
    {
        try
        {
            var path = Path.Combine(home, "session_index.jsonl");
            var info = new FileInfo(path);
            if (!info.Exists || info.LastWriteTimeUtc == _indexStamp || info.Length > 8 * 1024 * 1024) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var id = Text(root, "id"); var title = Text(root, "thread_name");
                    if (id is not null && !string.IsNullOrWhiteSpace(title)) _titles[id] = title.Length > 160 ? title[..160] : title;
                }
                catch (JsonException) { }
            }
            _indexStamp = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private void Feed(Rollout state, ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            var end = bytes.IndexOf((byte)'\n');
            var part = end >= 0 ? bytes[..end] : bytes;
            if (!state.SkipLine)
            {
                if (state.Pending.Length + part.Length > 2 * 1024 * 1024) { state.SkipLine = true; state.Pending.SetLength(0); }
                else
                {
                    state.Pending.Write(part);
                    if (state.Pending.Length > 512)
                    {
                        var prefix = Encoding.UTF8.GetString(state.Pending.GetBuffer(), 0, 512);
                        if (prefix.Contains("\"response_item\"", StringComparison.Ordinal) || prefix.Contains("\"item_completed\"", StringComparison.Ordinal))
                        { state.SkipLine = true; state.Pending.SetLength(0); }
                    }
                }
            }
            if (end < 0) return;
            if (!state.SkipLine) Parse(state, Encoding.UTF8.GetString(state.Pending.GetBuffer(), 0, (int)state.Pending.Length));
            state.Pending.SetLength(0); state.SkipLine = false;
            bytes = bytes[(end + 1)..];
        }
    }
    private void Parse(Rollout state, string line)
    {
        if (!line.Contains("session_meta", StringComparison.Ordinal) && !line.Contains("token_count", StringComparison.Ordinal)
            && !line.Contains("task_started", StringComparison.Ordinal) && !line.Contains("task_complete", StringComparison.Ordinal)
            && !line.Contains("turn_aborted", StringComparison.Ordinal) && !line.Contains("turn_context", StringComparison.Ordinal)
            && !line.Contains("user_message", StringComparison.Ordinal)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            if (Text(root, "type") == "session_meta")
            {
                state.Id = Text(payload, "id");
                state.Subagent = payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object;
                return;
            }
            if (!DateTimeOffset.TryParse(Text(root, "timestamp"), out var stamp)) return;
            if (Text(root, "type") == "turn_context") { state.Rate.Anchor(stamp); return; }
            if (Text(root, "type") != "event_msg") return;
            var type = Text(payload, "type");
            if (type == "task_started")
            {
                state.Running = true; state.TurnId = Text(payload, "turn_id"); state.Started = stamp;
                state.LastEvent = stamp; state.TurnTokens = 0;
                state.Rate.Anchor(stamp, true);
            }
            else if (type == "user_message") state.Rate.Anchor(stamp, true);
            else if (type is "task_complete" or "task_completed" or "turn_aborted")
            {
                var turn = Text(payload, "turn_id");
                if (turn is null || state.TurnId is null || turn == state.TurnId) { state.Running = false; state.LastEvent = stamp; }
            }
            else if (type == "token_count" && payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("total_token_usage", out var usage) && Number(usage, "total_tokens") is long total)
            {
                // The initial cumulative total can include inherited fork history.
                // Use only last_token_usage for that first observation, then counter deltas.
                var last = info.TryGetProperty("last_token_usage", out var lastUsage) ? Number(lastUsage, "total_tokens") : null;
                var previousTotal = state.Total;
                long? delta = state.Total is long previous && total >= previous ? total - previous : last;
                var input = Number(usage, "input_tokens"); var cached = Number(usage, "cached_input_tokens"); var output = Number(usage, "output_tokens");
                long? Component(long? current, long? previous, string key) => previousTotal.HasValue && total >= previousTotal && current.HasValue && previous.HasValue && current >= previous
                    ? current - previous : Number(lastUsage, key);
                var countersReset = previousTotal.HasValue && total < previousTotal ||
                    output.HasValue && state.Output.HasValue && output < state.Output;
                if (countersReset || delta > 0)
                    state.Rate.Observe(stamp, Component(output, state.Output, "output_tokens"), countersReset);
                if (delta > 0 && state.Id is not null)
                {
                    var identity = state.Id + "|" + stamp.ToUniversalTime().ToString("O") + "|" + total;
                    var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    var pairedDelta = previousTotal.HasValue && total >= previousTotal && input.HasValue && cached.HasValue &&
                        state.Input.HasValue && state.Cached.HasValue && input >= state.Input && cached >= state.Cached;
                    var observedInput = pairedDelta ? input - state.Input : Number(lastUsage, "input_tokens");
                    var observedCache = pairedDelta ? cached - state.Cached : Number(lastUsage, "cached_input_tokens");
                    state.Observed = new(id, stamp, observedInput, observedCache, Component(output, state.Output, "output_tokens"));
                    TokenObserved?.Invoke(state.Observed);
                }
                else if (delta == 0 && state.Observed is { } observed)
                {
                    var filled = observed with { InputTokens = observed.InputTokens ?? Number(lastUsage, "input_tokens"),
                        CachedInputTokens = observed.CachedInputTokens ?? Number(lastUsage, "cached_input_tokens"), OutputTokens = observed.OutputTokens ?? Number(lastUsage, "output_tokens") };
                    if (filled != observed) { state.Observed = filled; TokenObserved?.Invoke(filled); }
                }
                state.Input = input; state.Cached = cached; state.Output = output;
                state.Total = total; state.LastEvent = stamp; state.Updated = stamp;
                if (delta is long increment && (increment > 0 || previousTotal is null && increment == 0))
                {
                    var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp, _zone).DateTime);
                    state.Daily[date] = checked(state.Daily.GetValueOrDefault(date) + increment);
                    if (state.Running) state.TurnTokens = checked((state.TurnTokens ?? 0) + increment);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or InvalidOperationException) { }
    }
    private static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static long? Number(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value) && value >= 0 ? value : null;
}
