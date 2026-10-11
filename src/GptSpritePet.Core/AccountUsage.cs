using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GptSpritePet.Core;

public enum UsageConnectionState { Disabled, Connecting, Ready, Partial, Offline, SignedOut, MissingClient, Unsupported, Error }
public sealed record UsageAccount(string Identity, string Label, string? Plan, string Type);
public sealed record DailyTokenUsage(DateOnly Date, long Tokens);
public sealed record TokenActivity(long? LifetimeTokens, long? PeakDailyTokens, DailyTokenUsage[]? Daily)
{
    public DateOnly? LatestDate => Daily is { Length: > 0 } ? Daily.Max(x => x.Date) : null;
    public long? OnDate(DateOnly date) => Daily?.FirstOrDefault(x => x.Date == date)?.Tokens;
    public long? RecordedSevenDays(DateOnly date)
    {
        var records = Daily?.Where(x => x.Date >= date.AddDays(-6) && x.Date <= date).ToArray();
        try { return records is { Length: > 0 } ? records.Sum(x => x.Tokens) : null; }
        catch (OverflowException) { return null; }
    }
}
public sealed record UsageQuota(string BucketId, string Label, double? UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt)
{
    public double? RemainingPercent => UsedPercent.HasValue ? Math.Clamp(100 - UsedPercent.Value, 0, 100) : null;
    public bool IsCurrent(DateTimeOffset now) => ResetsAt.HasValue && ResetsAt.Value > now;
    public string WindowLabel => WindowMinutes switch
    {
        300 => "5 小时", 10080 => "每周", >= 1440 when WindowMinutes % 1440 == 0 => $"{WindowMinutes / 1440} 天",
        >= 60 when WindowMinutes % 60 == 0 => $"{WindowMinutes / 60} 小时", _ => $"{WindowMinutes} 分钟"
    };
}
public sealed record AccountUsageSnapshot
{
    public UsageConnectionState State { get; init; } = UsageConnectionState.Connecting;
    public string Message { get; init; } = "正在连接账号…";
    public UsageAccount? Account { get; init; }
    public TokenActivity? Tokens { get; init; }
    public UsageQuota[] Quotas { get; init; } = [];
    public DateTimeOffset? TokensUpdatedAt { get; init; }
    public DateTimeOffset? LimitsUpdatedAt { get; init; }
    public bool Busy { get; init; }
    public bool LimitsAvailable { get; init; }
    public LocalTokenUsage? LocalUsage { get; init; }
    public string? SelectedTaskId { get; init; }
    public ActiveTaskUsage? CurrentTask => LocalUsage?.Tasks.FirstOrDefault(t => t.Id == SelectedTaskId) ?? LocalUsage?.Tasks.FirstOrDefault();
    public UsageQuota? QuotaForWindow(int minutes)
    {
        var matches = Quotas.Where(q => q.WindowMinutes == minutes).ToArray();
        return matches.FirstOrDefault(q => q.BucketId == "codex") ?? (matches.Length == 1 ? matches[0] : null);
    }
    public bool HasCurrentLimits(DateTimeOffset now, int refreshSeconds) =>
        State is UsageConnectionState.Ready or UsageConnectionState.Partial && LimitsAvailable && LimitsUpdatedAt.HasValue &&
        now - LimitsUpdatedAt.Value < TimeSpan.FromSeconds(refreshSeconds * 2 + 30);
}

public static class AccountUsageParser
{
    public static UsageAccount? Account(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var type = Text(a, "type") ?? "unknown";
        var email = Text(a, "email");
        var id = Text(a, "accountId") ?? Text(a, "id") ?? email;
        // An unidentified account must not be allowed to inherit another account's cache.
        var identity = id is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + id)));
        var label = email is null ? "已登录账号" : MaskEmail(email);
        return new(identity, label, Text(a, "planType"), type);
    }
    public static string MaskEmail(string email)
    {
        var split = email.IndexOf('@');
        return split > 0 ? email[..Math.Min(2, split)] + "***" + email[split..] : "已登录账号";
    }
    public static TokenActivity Tokens(JsonElement result)
    {
        var summary = result.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        DailyTokenUsage[]? daily = null;
        if (result.TryGetProperty("dailyUsageBuckets", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            var list = new List<DailyTokenUsage>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!DateOnly.TryParseExact(Text(row, "startDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) || Number(row, "tokens") is not long count)
                    throw new InvalidDataException("Invalid daily token activity.");
                if (list.Any(x => x.Date == date)) throw new InvalidDataException("Duplicate daily token activity.");
                list.Add(new(date, count));
            }
            daily = list.OrderBy(x => x.Date).ToArray();
        }
        return new(Number(summary, "lifetimeTokens"), Number(summary, "peakDailyTokens"), daily);
    }
    public static UsageQuota[] Quotas(JsonElement result)
    {
        var list = new List<UsageQuota>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
        {
            foreach (var entry in map.EnumerateObject()) AddBucket(entry.Name, entry.Value, list);
        }
        else if (result.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            AddBucket(Text(single, "limitId") ?? "codex", single, list);
        return list.ToArray();
    }
    private static void AddBucket(string id, JsonElement bucket, List<UsageQuota> list)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return;
        var label = Text(bucket, "limitName") ?? (id == "codex" ? "Codex" : id);
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("windowDurationMins", out var mins) || mins.ValueKind != JsonValueKind.Number || !mins.TryGetInt32(out var duration) || duration <= 0) continue;
            double? used = w.TryGetProperty("usedPercent", out var percent) && percent.ValueKind == JsonValueKind.Number && percent.TryGetDouble(out var value) &&
                double.IsFinite(value) && value >= 0 ? value : null;
            DateTimeOffset? reset = null;
            if (w.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds))
            {
                try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
            }
            list.Add(new(id, label, used, duration, reset));
        }
    }
    private static long? Number(JsonElement a, string key) => a.ValueKind == JsonValueKind.Object &&
        a.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var value) && value >= 0 ? value : null;
    private static string? Text(JsonElement a, string key) => a.ValueKind == JsonValueKind.Object &&
        a.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
}

public sealed class UsageAlertTracker
{
    private sealed record Seen(DateTimeOffset Reset, int Level);
    private readonly Dictionary<string, Seen> _seen = new();
    private readonly string? _file;
    public UsageAlertTracker(string? file = null)
    {
        _file = file;
        try
        {
            if (file is not null && File.Exists(file) && new FileInfo(file).Length < 64 * 1024)
                _seen = JsonSerializer.Deserialize<Dictionary<string, Seen>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public string? Evaluate(AccountUsageSnapshot snapshot, DateTimeOffset now, int refreshSeconds)
    {
        if (snapshot.Account is null || snapshot.Busy || !snapshot.HasCurrentLimits(now, refreshSeconds)) return null;
        string? warning = null;
        foreach (var quota in snapshot.Quotas)
        {
            if (!quota.IsCurrent(now) || quota.RemainingPercent is not double remaining) continue;
            var level = remaining < 10 ? 2 : remaining < 20 ? 1 : 0;
            if (level == 0) continue;
            var key = snapshot.Account.Identity + "|" + quota.BucketId + "|" + quota.WindowMinutes;
            if (_seen.TryGetValue(key, out var old) && old.Reset == quota.ResetsAt && old.Level >= level) continue;
            _seen[key] = new(quota.ResetsAt!.Value, level);
            warning = $"{quota.Label} {quota.WindowLabel}额度剩余 {remaining:0.#}%，留意重置时间。";
        }
        if (warning is not null && _file is not null)
        {
            foreach (var key in _seen.Where(x => x.Value.Reset <= now).Select(x => x.Key).ToArray()) _seen.Remove(key);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_seen)); File.Move(_file + ".tmp", _file, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return warning;
    }
}
