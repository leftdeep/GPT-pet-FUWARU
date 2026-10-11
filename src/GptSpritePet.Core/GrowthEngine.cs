using System.Text.Json;

namespace GptSpritePet.Core;

public sealed record ObservedTokenUsage(string Id, DateTimeOffset At, long? InputTokens, long? CachedInputTokens, long? OutputTokens);
public static class GrowthRules
{
    public static double Need(int level) => 60 + 15d * (level - 1);
    public static double TotalTo(int level) => 7.5d * (level - 1) * (level + 6d);
    public static int Level(double xp) => Math.Max(1, (int)Math.Floor((-5 + Math.Sqrt(49 + 8 * Math.Max(0, xp) / 15)) / 2));
    public static double TimeXp(double seconds) => Math.Min(seconds, 7200) / 180 + Math.Clamp(seconds - 7200, 0, 7200) / 360;
    public static int TokenXp(double tokens) => tokens >= 200000 ? 20 : tokens >= 80000 ? 15 : tokens >= 20000 ? 10 : tokens >= 5000 ? 5 : 0;
}
public sealed class GrowthDay
{
    public double Seconds { get; set; }
    public double WeightedTokens { get; set; }
    public double TimeXp { get; set; }
    public int PresenceXp { get; set; }
    public int TokenXp { get; set; }
    public int InteractionXp { get; set; }
    public double Xp => TimeXp + PresenceXp + TokenXp + InteractionXp;
}
public sealed class GrowthRun
{
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
}
public sealed class GrowthTokenCredit
{
    public DateTimeOffset At { get; set; }
    public double Output { get; set; }
    public double Input { get; set; }
}
public sealed class GrowthSave
{
    public int SchemaVersion { get; set; } = 1;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public double TotalXp { get; set; }
    public Dictionary<string, GrowthDay> Days { get; set; } = new();
    public List<GrowthRun> Runs { get; set; } = new();
    public Dictionary<string, GrowthTokenCredit> Tokens { get; set; } = new();
    // Retain legacy save data only; commemorations are no longer generated or displayed.
    public HashSet<string> Badges { get; set; } = new();
    public DateTimeOffset? LastInteraction { get; set; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyFields { get; set; }
}
public sealed record GrowthSnapshot(int Level, double TotalXp, double LevelXp, double NeedXp, GrowthDay Today,
    int Days, double Hours, double FocusSeconds, string? Warning);

/// <summary>Local companionship ledger. Call Tick with monotonic elapsed time; no offline catch-up.</summary>
public sealed class GrowthEngine
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeZoneInfo _zone;
    private GrowthSave _save;
    private GrowthRun? _run;
    private DateTimeOffset? _lastWall;
    private double _focus;
    private double _saveElapsed;
    private string? _prunedDate;
    private bool _readOnly;
    public string? Warning { get; private set; }
    public event Action? Changed;

    public GrowthEngine(string directory, TimeZoneInfo? zone = null)
    {
        _path = Path.Combine(directory, "growth.json"); _save = Load();
        try { _zone = zone ?? TimeZoneInfo.FindSystemTimeZoneById(_save.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { _zone = zone ?? TimeZoneInfo.Local; }
        _save.TimeZoneId = _zone.Id;
    }
    private GrowthSave Load()
    {
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException();
                var save = JsonSerializer.Deserialize<GrowthSave>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                if (save.SchemaVersion != 1) { _readOnly = true; Warning = "成长存档版本较新，已暂停写入，请更新程序。"; return new(); }
                if (!double.IsFinite(save.TotalXp) || save.TotalXp is < 0 or > 1000000000 || save.Days is null || save.Tokens is null || save.Runs is null ||
                    save.Badges is null || save.Days.Any(p =>
                    !DateOnly.TryParseExact(p.Key, "yyyy-MM-dd", out _) || p.Value is null || !double.IsFinite(p.Value.Seconds) || p.Value.Seconds < 0 ||
                    !double.IsFinite(p.Value.WeightedTokens) || p.Value.WeightedTokens < 0 || !double.IsFinite(p.Value.TimeXp) || p.Value.TimeXp is < 0 or > 60 ||
                    p.Value.PresenceXp is not (0 or 10) || p.Value.TokenXp is not (0 or 5 or 10 or 15 or 20) || p.Value.InteractionXp is not (0 or 5 or 10)) ||
                    save.Runs.Any(r => r is null || r.End < r.Start) || save.Tokens.Any(p => p.Value is null || !double.IsFinite(p.Value.Input) ||
                    !double.IsFinite(p.Value.Output) || p.Value.Input < 0 || p.Value.Output < 0))
                    throw new InvalidDataException();
                if (path.EndsWith(".bak", StringComparison.Ordinal)) Warning = "成长存档已从备份恢复。";
                return save;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                Warning = "成长存档无法读取，已保留原文件并尝试备份。";
                try { File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return new();
    }
    private string DateKey(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, _zone).ToString("yyyy-MM-dd");
    private GrowthDay Day(DateTimeOffset now)
    {
        var key = DateKey(now); if (!_save.Days.TryGetValue(key, out var day)) _save.Days[key] = day = new(); return day;
    }
    public GrowthSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            var level = GrowthRules.Level(_save.TotalXp);
            var today = _save.Days.GetValueOrDefault(DateKey(now)) ?? new();
            // Clone the mutable ledger so callers never observe partial background updates.
            var copy = new GrowthDay { Seconds = today.Seconds, WeightedTokens = today.WeightedTokens, TimeXp = today.TimeXp,
                PresenceXp = today.PresenceXp, TokenXp = today.TokenXp, InteractionXp = today.InteractionXp };
            return new(level, _save.TotalXp, _save.TotalXp - GrowthRules.TotalTo(level), GrowthRules.Need(level), copy,
                _save.Days.Values.Count(d => d.PresenceXp > 0), _save.Days.Values.Sum(d => d.Seconds) / 3600,
                _focus, Warning);
        }
    }
    public void StartRun(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_readOnly || _run is not null) return;
            _run = new() { Start = now, End = now }; _save.Runs.Add(_run); _lastWall = now;
            Prune(now);
            SaveCore();
        }
    }
    public void EndRun(DateTimeOffset now)
    {
        lock (_gate) { if (_run is not null && now >= _run.End) _run.End = now; _run = null; _lastWall = null; SaveCore(); }
    }
    public void Tick(DateTimeOffset now, double elapsedSeconds, bool unlocked, bool inputActive, bool taskProgress)
    {
        if (!double.IsFinite(elapsedSeconds)) return;
        lock (_gate)
        {
            if (_readOnly || _run is null) return;
            var previous = _lastWall; _lastWall = now;
            if (now >= _run.End) _run.End = now;
            if (_prunedDate != DateKey(now)) Prune(now);
            // A delayed dispatcher, suspend, or clock jump is never converted into catch-up XP.
            var wall = previous.HasValue ? (now - previous.Value).TotalSeconds : 0;
            if (elapsedSeconds > 0 && elapsedSeconds <= 5 && wall > 0 && Math.Abs(wall - elapsedSeconds) <= 2 && unlocked &&
                (inputActive || taskProgress || _focus > 0))
            {
                var remaining = elapsedSeconds;
                var start = now.AddSeconds(-elapsedSeconds);
                while (remaining > .000001)
                {
                    var local = TimeZoneInfo.ConvertTime(start, _zone);
                    var midnight = local.Date.AddDays(1);
                    var boundary = new DateTimeOffset(midnight, _zone.GetUtcOffset(midnight));
                    var part = Math.Min(remaining, Math.Max(.000001, (boundary - start).TotalSeconds));
                    var day = Day(start); var old = day.Xp; day.Seconds += part;
                    day.TimeXp = GrowthRules.TimeXp(day.Seconds); if (day.Seconds >= 180) day.PresenceXp = 10;
                    Award(day.Xp - old); start = start.AddSeconds(part); remaining -= part;
                }
                if (_focus > 0) _focus = Math.Max(0, _focus - elapsedSeconds);
            }
            _saveElapsed += Math.Max(0, Math.Min(5, elapsedSeconds));
            if (_saveElapsed >= 5) { SaveCore(); _saveElapsed = 0; }
        }
        Changed?.Invoke();
    }
    public bool Interact(DateTimeOffset now)
    {
        bool earned;
        lock (_gate)
        {
            if (_readOnly || _run is null) return false;
            var day = Day(now);
            earned = day.InteractionXp < 10 && (!_save.LastInteraction.HasValue || now - _save.LastInteraction.Value >= TimeSpan.FromMinutes(30));
            if (earned) { day.InteractionXp += 5; _save.LastInteraction = now; Award(5); SaveCore(); }
        }
        if (earned) Changed?.Invoke(); return earned;
    }
    public void ObserveToken(ObservedTokenUsage usage, DateTimeOffset readAt)
    {
        var changed = false;
        lock (_gate)
        {
            if (_readOnly) return;
            if (_run is not null && readAt >= _run.End) _run.End = readAt;
            if (!_save.Runs.Any(r => usage.At >= r.Start && usage.At <= r.End)) return;
            var output = Math.Max(0, usage.OutputTokens ?? 0);
            var input = usage.InputTokens.HasValue && usage.CachedInputTokens.HasValue ? .1 * Math.Max(0d, (double)usage.InputTokens.Value - usage.CachedInputTokens.Value) : 0;
            if (!_save.Tokens.TryGetValue(usage.Id, out var credit)) credit = new() { At = usage.At };
            var delta = Math.Max(0, output - credit.Output) + Math.Max(0, input - credit.Input);
            if (delta <= 0) return;
            credit.Output = Math.Max(credit.Output, output); credit.Input = Math.Max(credit.Input, input); _save.Tokens[usage.Id] = credit;
            var day = Day(usage.At); day.WeightedTokens += delta;
            var xp = GrowthRules.TokenXp(day.WeightedTokens); Award(xp - day.TokenXp); day.TokenXp = xp; changed = true;
            SaveCore(); // Event credits and XP commit in the same atomic document.
        }
        if (changed) Changed?.Invoke();
    }
    private void Award(double xp)
    {
        _save.TotalXp += Math.Max(0, xp);
    }
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-30); _prunedDate = DateKey(now);
        _save.Runs.RemoveAll(r => r.End < cutoff);
        // Trim the eligibility window together with dedup keys, including a process running for over a month.
        foreach (var run in _save.Runs) if (run.Start < cutoff) run.Start = cutoff;
        foreach (var key in _save.Tokens.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToArray()) _save.Tokens.Remove(key);
    }
    public void Focus(int minutes)
    {
        lock (_gate) _focus = minutes is 25 or 50 ? minutes * 60 : 0; Changed?.Invoke();
    }
    public void Save() { lock (_gate) SaveCore(); }
    private void SaveCore()
    {
        if (_readOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_save, SettingsStore.JsonOptions);
            using (var stream = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(_path)) File.Replace(_path + ".tmp", _path, _path + ".bak", true);
            else File.Move(_path + ".tmp", _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "成长存档暂时无法保存，请检查目录权限与磁盘空间。"; }
    }
}
