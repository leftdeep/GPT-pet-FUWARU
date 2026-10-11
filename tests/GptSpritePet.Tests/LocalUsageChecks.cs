using System.Text;
using System.Text.Json;
using GptSpritePet.Core;

internal static class LocalUsageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var zone = TimeZoneInfo.CreateCustomTimeZone("usage-test", TimeSpan.FromHours(8), "usage-test", "usage-test");
        var midnight = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.FromHours(8));
        var home = Path.Combine(Path.GetTempPath(), "GptSpritePet-usage-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var archive = Path.Combine(home, "archived_sessions"); Directory.CreateDirectory(archive);
        string Row(DateTimeOffset stamp, string type, object payload) => JsonSerializer.Serialize(new { timestamp = stamp.ToString("O"), type, payload }) + "\n";
        string Meta(string id, bool subagent = false) => Row(midnight.AddMinutes(-10), "session_meta", new { id, source = subagent ? (object)new { subagent = "test" } : "vscode" });
        string Event(DateTimeOffset stamp, string type, string turn = "one") => Row(stamp, "event_msg", new { type, turn_id = turn });
        string Tokens(DateTimeOffset stamp, long total, long last) => Row(stamp, "event_msg", new { type = "token_count", info = new
        { total_token_usage = new { total_tokens = total, input_tokens = total - 10, cached_input_tokens = total - 20, output_tokens = 10, reasoning_output_tokens = 5 },
          last_token_usage = new { total_tokens = last } } });
        void Write(string path, string text, bool append = false)
        {
            if (append) File.AppendAllText(path, text, new UTF8Encoding(false)); else File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, midnight.AddMinutes(10).UtcDateTime);
        }
        try
        {
            var reader = new LocalTokenUsageReader(home, zone);
            var missing = reader.Read(midnight);
            check(missing.TodayTokens is null && missing.Tasks.Length == 0, "missing local records never become zero tokens or a running task");
            var one = Path.Combine(sessions, "one.jsonl");
            Write(one, Meta("one") + Event(midnight.AddMinutes(-5), "task_started") + Tokens(midnight.AddMinutes(-1), 1000, 100)
                + Tokens(midnight.AddMinutes(1), 1300, 300) + Tokens(midnight.AddMinutes(1), 1300, 300));
            var snapshot = reader.Read(midnight.AddMinutes(2));
            check(snapshot.TodayTokens == 300, "local midnight attributes counter delta to new day and deduplicates repeated usage events");
            check(snapshot.Tasks.Single().TotalTokens == 1300 && snapshot.Tasks.Single().TurnTokens == 400,
                "thread cumulative and current execution remain distinct; cached and reasoning tokens are not added twice");
            check(reader.Read(midnight.AddMinutes(2)).TodayTokens == 300, "refreshing unchanged logs cannot inflate today's usage");
            Write(one, Event(midnight.AddMinutes(3), "task_complete"), true);
            check(reader.Read(midnight.AddMinutes(3)).Tasks.Length == 0, "completed tasks disappear from current task display");
            Write(one, Event(midnight.AddMinutes(4), "task_started", "two") + Tokens(midnight.AddMinutes(5), 1500, 200), true);
            check(reader.Read(midnight.AddMinutes(5)).Tasks.Single().TurnTokens == 200, "a new execution starts its own token counter");
            Write(one, Event(midnight.AddMinutes(6), "turn_aborted", "old-turn"), true);
            check(reader.Read(midnight.AddMinutes(6)).Tasks.Length == 1, "late completion of a different turn cannot stop the active turn");
            var two = Path.Combine(sessions, "two.jsonl");
            Write(two, Meta("two") + Event(midnight.AddMinutes(6), "task_started") + Tokens(midnight.AddMinutes(7), 90000, 50));
            snapshot = reader.Read(midnight.AddMinutes(7));
            check(snapshot.TodayTokens == 550 && snapshot.Tasks.First().Id == "two", "fork history is not charged to today and newest active task is selected first");
            Write(Path.Combine(home, "session_index.jsonl"), JsonSerializer.Serialize(new { id = "two", thread_name = "测试任务" }) + "\n");
            check(reader.Read(midnight.AddMinutes(7)).Tasks.First().Title == "测试任务", "task titles come from the index without reading user prompts");
            var child = Path.Combine(sessions, "child.jsonl");
            Write(child, Meta("child", true) + Event(midnight.AddMinutes(8), "task_started") + Tokens(midnight.AddMinutes(8), 25, 25));
            snapshot = reader.Read(midnight.AddMinutes(8));
            check(snapshot.Tasks.Length == 2 && snapshot.TodayTokens == 575, "subagent activity counts toward today without replacing the main current task");
            File.Move(two, Path.Combine(archive, "two.jsonl"));
            check(reader.Read(midnight.AddMinutes(8)).TodayTokens == 575, "archiving a session does not double-count its tokens");
            var partial = Tokens(midnight.AddMinutes(9), 1600, 100);
            Write(one, partial[..^8], true);
            check(reader.Read(midnight.AddMinutes(9)).TodayTokens == 575, "partially written JSON is held until a complete newline arrives");
            Write(one, partial[^8..], true);
            check(reader.Read(midnight.AddMinutes(9)).TodayTokens == 675, "completing an appended JSON line charges its tokens exactly once");
            Write(one, Event(midnight.AddMinutes(10), "turn_aborted", "two"), true);
            check(reader.Read(midnight.AddMinutes(10)).Tasks.Length == 1, "interrupted current execution is removed from active tasks");
            Write(one, Meta("one") + Event(midnight.AddMinutes(11), "task_started") + Tokens(midnight.AddMinutes(11), 5, 5));
            check(reader.Read(midnight.AddMinutes(11)).TodayTokens == 80, "truncated or replaced logs reset their cursor without retaining stale totals");
            check(reader.Read(midnight.AddDays(2)).Tasks.Length == 0, "abandoned old running markers do not remain current forever");
            var selected = new AccountUsageSnapshot { LocalUsage = snapshot, SelectedTaskId = "one" };
            check(selected.CurrentTask?.Id == "one" && (selected with { SelectedTaskId = "missing" }).CurrentTask?.Id == "two",
                "task selector chooses the requested active task and safely falls back after completion");
            var quotas = new AccountUsageSnapshot { State = UsageConnectionState.Ready, LimitsAvailable = true, LimitsUpdatedAt = now,
                Quotas = [new("other", "Other", 99, 300, now.AddHours(1)), new("codex", "Codex", 20, 300, now.AddHours(1)),
                          new("codex", "Codex", 32, 10080, now.AddDays(6))] };
            var summary = UsageSummary.Create(quotas, now, 60);
            check(summary.FiveHours.Value == "80%" && summary.SevenDays.Value == "68%", "5h and 7-day values use their exact Codex windows instead of the smallest unrelated percentage");
            check(UsageSummary.Create(quotas with { Quotas = [quotas.Quotas[2]] }, now, 60).FiveHours.Value == "—", "missing 5h window is unavailable rather than copied from the weekly window");
            check(UsageSummary.Create(quotas with { LimitsAvailable = false }, now, 60).FiveHours.Value.Contains("旧"), "failed refresh labels cached quota values as old data");
            check(UsageSummary.Create(quotas with { LimitsUpdatedAt = now.AddMinutes(-10) }, now, 60).SevenDays.Value.Contains("旧"), "stale quota values cannot appear fresh");
            var mixed = UsageSummary.Create(quotas with { Quotas = [new("codex", "Codex", 100, 300, now.AddHours(1)), quotas.Quotas[2]] }, now, 60);
            check(mixed.FiveHours.Level == UsageMetricLevel.Critical && mixed.SevenDays.Level == UsageMetricLevel.Normal &&
                mixed.FiveHours.ResetsAt == now.AddHours(1) && mixed.SevenDays.ResetsAt == now.AddDays(6), "depleted 5h quota has independent severity and reset time from healthy weekly quota");
            var weeklyLow = UsageSummary.Create(quotas with { Quotas = [quotas.Quotas[1], new("codex", "Codex", 85, 10080, now.AddDays(6))] }, now, 60);
            check(weeklyLow.FiveHours.Level == UsageMetricLevel.Normal && weeklyLow.SevenDays.Level == UsageMetricLevel.Warning,
                "weekly warning does not recolor healthy 5h quota");
            check(UsageSummary.Create(quotas with { LimitsAvailable = false }, now, 60).FiveHours.Level == UsageMetricLevel.Unknown,
                "failed refresh uses neutral stale severity");
            var pro = quotas with { Account = new("test", "test", "pro", "chatgpt"), Quotas = [quotas.Quotas[2]] };
            check(UsageSummary.Create(pro, now, 60).FiveHours is { Value: "不限", Level: UsageMetricLevel.Normal, ResetsAt: null },
                "fresh Pro without a five-hour window displays no five-hour limit");
            check(UsageSummary.Create(pro with { LimitsAvailable = false }, now, 60).FiveHours.Value == "—" &&
                UsageSummary.Create(pro with { Quotas = quotas.Quotas }, now, 60).FiveHours.Value == "80%",
                "Pro read failures remain unknown and actual returned quota takes precedence");
            var day = DateOnly.FromDateTime(now.LocalDateTime);
            var local = new LocalTokenUsage(day, 450, [new("id", "Current", "turn", 1000, 300, now, now)], now, "test", true);
            var combined = quotas with { LocalUsage = local, Tokens = new(null, null, [new(day, 100)]) };
            summary = UsageSummary.Create(combined, now, 60);
            check(summary.Today.Value == "450" && summary.Task.Value == "1,000", "live local today and thread usage populate the four-metric summary");
            check(UsageSummary.Create(combined with { LocalUsage = local with { Date = day.AddDays(-1) } }, now, 60).Today.Value == "100",
                "yesterday's local snapshot cannot be displayed as today's activity");
            check(UsageSummary.Create(quotas, now, 60).Today.Value == "—" && UsageSummary.Create(quotas, now, 60).Task.Value == "无任务",
                "unavailable today and no active task have different display states");
        }
        finally
        {
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(home).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cleanup outside test root.");
            Directory.Delete(home, true);
        }
    }
}
