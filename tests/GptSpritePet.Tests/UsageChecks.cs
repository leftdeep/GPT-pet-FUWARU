using GptSpritePet.Core;
using System.Text.Json;

internal static class UsageChecks
{
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    internal static void Run(Action<bool, string> check)
    {
        var date = new DateOnly(2026, 10, 3);
        var usage = AccountUsageParser.Tokens(Json("""
            {"summary":{"lifetimeTokens":286286382,"peakDailyTokens":51342532},
             "dailyUsageBuckets":[{"startDate":"2026-10-02","tokens":51342532},{"startDate":"2026-10-01","tokens":0},
                                  {"startDate":"2026-09-25","tokens":12}],"threadUsage":null}
            """));
        check(usage.LifetimeTokens == 286286382 && usage.PeakDailyTokens == 51342532, "usage keeps exact server token integers");
        check(usage.OnDate(date) is null && usage.OnDate(date.AddDays(-2)) == 0, "missing daily activity differs from reported zero");
        check(usage.RecordedSevenDays(date) == 51342532, "seven-day recorded total excludes dates outside the period");
        check(usage.LatestDate == date.AddDays(-1), "latest bucket date exposes delayed daily statistics");
        check(AccountUsageParser.Tokens(Json("{\"summary\":null,\"dailyUsageBuckets\":null}")).LifetimeTokens is null,
            "unavailable token summary never becomes zero");
        check(AccountUsageParser.Tokens(Json("{\"summary\":{},\"dailyUsageBuckets\":[]}")).RecordedSevenDays(date) is null,
            "empty daily buckets do not invent a seven-day total");
        foreach (var invalid in new[] {
            "{\"dailyUsageBuckets\":[{\"startDate\":\"invalid\",\"tokens\":1}]}",
            "{\"dailyUsageBuckets\":[{\"startDate\":\"2026-10-03\",\"tokens\":-1}]}",
            "{\"dailyUsageBuckets\":[{\"startDate\":\"2026-10-03\",\"tokens\":1},{\"startDate\":\"2026-10-03\",\"tokens\":1}]}" })
        {
            var rejected = false; try { AccountUsageParser.Tokens(Json(invalid)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "malformed or duplicate daily records cannot inflate token statistics");
        }
        var account = AccountUsageParser.Account(Json("{\"account\":{\"type\":\"chatgpt\",\"email\":\"person@example.com\",\"planType\":\"plus\"}}"))!;
        check(account.Label == "pe***@example.com" && !account.Identity.Contains("person"), "cached identity masks email and hashes account key");
        check(AccountUsageParser.Account(Json("{\"account\":null}")) is null, "signed-out account is distinguishable from unavailable usage");
        var other = AccountUsageParser.Account(Json("{\"account\":{\"type\":\"chatgpt\",\"email\":\"other@example.com\"}}"))!;
        check(account.Identity != other.Identity, "different accounts have different cache identities");
        var quotas = AccountUsageParser.Quotas(Json("""
            {"rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":300}},"rateLimitsByLimitId":{
            "codex":{"primary":{"usedPercent":7,"windowDurationMins":300,"resetsAt":1791005362},
                     "secondary":{"usedPercent":null,"windowDurationMins":10080,"resetsAt":null}},
            "other":{"limitName":"Other model","primary":{"usedPercent":0,"windowDurationMins":60,"resetsAt":1791005362}}}}
            """));
        check(quotas.Length == 3 && quotas[0].RemainingPercent == 93, "multiple rate buckets take precedence over legacy single-bucket view");
        check(quotas[1].RemainingPercent is null && quotas[1].ResetsAt is null, "missing quota values remain unavailable");
        check(quotas[2].RemainingPercent == 100 && quotas[2].WindowLabel == "1 小时", "real zero usage and dynamic quota windows are retained");
        var legacy = AccountUsageParser.Quotas(Json("{\"rateLimits\":{\"primary\":{\"usedPercent\":105,\"windowDurationMins\":300}}}"));
        check(legacy.Single().RemainingPercent == 0, "remaining quota clamps to zero without producing negative capacity");
        var now = DateTimeOffset.Now;
        var alerts = new UsageAlertTracker();
        var snapshot = new AccountUsageSnapshot { Account = account, State = UsageConnectionState.Ready, LimitsAvailable = true,
            LimitsUpdatedAt = now, Quotas = [new("codex", "Codex", 85, 300, now.AddHours(1))] };
        check(alerts.Evaluate(snapshot, now, 60) is not null && alerts.Evaluate(snapshot, now, 60) is null, "low quota warns once per account and window");
        var critical = snapshot with { Quotas = [snapshot.Quotas[0] with { UsedPercent = 95 }] };
        check(alerts.Evaluate(critical, now, 60) is not null && alerts.Evaluate(critical, now, 60) is null, "critical quota gets one escalation without repeated bubbles");
        check(alerts.Evaluate(snapshot with { Account = other }, now, 60) is not null, "account changes do not share alert suppression");
        check(new UsageAlertTracker().Evaluate(snapshot with { State = UsageConnectionState.Offline }, now, 60) is null,
            "cached offline percentages do not produce fresh quota alerts");
        check(new UsageAlertTracker().Evaluate(snapshot with { LimitsAvailable = false, State = UsageConnectionState.Partial }, now, 60) is null,
            "failed quota refresh cannot trigger a warning using cached limits");
        check(new UsageAlertTracker().Evaluate(snapshot with { Quotas = [snapshot.Quotas[0] with { ResetsAt = now.AddSeconds(-1) }] }, now, 60) is null,
            "expired quota windows await server refresh rather than inventing a reset");
        check(new PetSettings { UsageRefreshSeconds = 1 }.Normalize().UsageRefreshSeconds == 30 &&
            new PetSettings { UsageRefreshSeconds = 999 }.Normalize().UsageRefreshSeconds == 300, "refresh interval remains within supported bounds");
        var alertFile = Path.Combine(Path.GetTempPath(), "GptSpritePet-alert-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var persistent = new UsageAlertTracker(alertFile);
            check(persistent.Evaluate(snapshot, now, 60) is not null && new UsageAlertTracker(alertFile).Evaluate(snapshot, now, 60) is null,
                "restarting the pet does not repeat a warning for the same account and quota window");
            check(new UsageAlertTracker(alertFile).Evaluate(snapshot with { Quotas = [snapshot.Quotas[0] with { ResetsAt = now.AddHours(2) }] }, now, 60) is not null,
                "a new quota reset permits its own low-usage reminder");
        }
        finally { if (File.Exists(alertFile)) File.Delete(alertFile); }
        Console.WriteLine("Account usage checks completed.");
    }
}
