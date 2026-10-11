using System.Text.Json;
using GptSpritePet.Core;

internal static class GrowthChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "GptSpritePet-growth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zone = TimeZoneInfo.CreateCustomTimeZone("growth-test", TimeSpan.FromHours(8), "growth-test", "growth-test");
        var start = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(8));
        string DirectoryFor(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        GrowthEngine Fresh(string name) => new(DirectoryFor(name), zone);
        GrowthEngine Seed(string name, double seconds, double total = 0)
        {
            var path = DirectoryFor(name); var day = new GrowthDay { Seconds = seconds, TimeXp = GrowthRules.TimeXp(seconds), PresenceXp = seconds >= 180 ? 10 : 0 };
            File.WriteAllText(Path.Combine(path, "growth.json"), JsonSerializer.Serialize(new GrowthSave { TotalXp = Math.Max(total, day.Xp),
                Days = new() { ["2026-10-04"] = day } })); return new(path, zone);
        }
        bool Near(double a, double b) => Math.Abs(a - b) < .00001;
        try
        {
            check(GrowthRules.Level(0) == 1 && GrowthRules.Level(59.999) == 1 && GrowthRules.Level(60) == 2, "growth first level threshold is exactly 60 XP");
            check(GrowthRules.TotalTo(30) == 7830 && GrowthRules.TotalTo(50) == 20580, "growth cumulative curve matches level 30 and 50 targets");
            check(Enumerable.Range(1, 100).All(l => GrowthRules.Level(GrowthRules.TotalTo(l)) == l && GrowthRules.Need(l) == GrowthRules.TotalTo(l + 1) - GrowthRules.TotalTo(l)), "growth curve supports future levels and exact carry-over");
            check(GrowthRules.TimeXp(1800) == 10 && GrowthRules.TimeXp(7200) == 40 && GrowthRules.TimeXp(14400) == 60 && GrowthRules.TimeXp(99999) == 60, "companionship tiers have a 60 XP daily cap");
            check(new[] { 4999d, 5000, 20000, 80000, 200000, 999999 }.Select(GrowthRules.TokenXp).SequenceEqual(new[] { 0, 5, 10, 15, 20, 20 }), "weighted token tiers grant only their capped cumulative amount");
            var engine = Fresh("clock"); engine.StartRun(start);
            engine.Tick(start.AddSeconds(1), 1, true, true, false);
            check(Near(engine.Snapshot(start).Today.Seconds, 1) && Near(engine.Snapshot(start).TotalXp, 1d / 180), "growth awards fractional time continuously");
            var s = engine.Snapshot(start); s.Today.Seconds = 1000;
            check(engine.Snapshot(start).Today.Seconds == 1, "growth snapshots cannot mutate the saved ledger");
            engine.Tick(start.AddSeconds(2), 1, true, false, false);
            engine.Tick(start.AddSeconds(3), 1, false, true, true);
            check(engine.Snapshot(start).Today.Seconds == 1, "idle and locked desktops earn no time XP");
            engine.Tick(start.AddSeconds(4), 1, true, false, true);
            check(engine.Snapshot(start).Today.Seconds == 2, "fresh task progress supports companionship without recent input");
            engine.Tick(start.AddHours(1), 3600, true, true, true);
            engine.Tick(start.AddMinutes(30), 1, true, true, true);
            check(engine.Snapshot(start).Today.Seconds == 2, "suspend gaps and clock rollback cannot award catch-up XP");
            engine.Tick(start.AddHours(1), double.NaN, true, true, true);
            check(double.IsFinite(engine.Snapshot(start).TotalXp), "invalid monotonic duration cannot poison growth persistence");
            engine.EndRun(start.AddHours(1));
            engine.Tick(start.AddHours(2), 1, true, true, true);
            check(engine.Snapshot(start).Today.Seconds == 2, "a stopped pet cannot gain offline time");
            engine = new(Path.Combine(root, "clock"), zone); engine.StartRun(start.AddHours(2)); engine.Tick(start.AddHours(2).AddSeconds(1), 1, true, true, false);
            check(engine.Snapshot(start).Today.Seconds == 3 && Near(engine.Snapshot(start).Today.TimeXp, 3d / 180), "restart preserves partial seconds and fractional XP");
            var presence = Seed("presence", 179); presence.StartRun(start); presence.Tick(start.AddSeconds(1), 1, true, true, false);
            check(presence.Snapshot(start).Today.PresenceXp == 10 && presence.Snapshot(start).Days == 1, "three-minute daily presence is awarded automatically");
            presence.Tick(start.AddSeconds(2), 1, true, true, false);
            check(presence.Snapshot(start).Today.PresenceXp == 10, "daily presence cannot be claimed twice");
            var tier = Seed("time-tier", 7199); tier.StartRun(start); tier.Tick(start.AddSeconds(2), 2, true, true, false);
            check(Near(tier.Snapshot(start).Today.TimeXp, 40 + 1d / 360), "time rate changes exactly at two hours");
            var capped = Seed("cap", 14399); capped.StartRun(start); capped.Tick(start.AddSeconds(2), 2, true, true, false);
            check(capped.Snapshot(start).Today.TimeXp == 60 && capped.Snapshot(start).Today.Seconds == 14401, "time XP caps while lifetime companionship keeps accumulating");
            check(capped.Interact(start) && !capped.Interact(start.AddMinutes(29)) && capped.Interact(start.AddMinutes(30)) && !capped.Interact(start.AddMinutes(60)), "interactions have a persistent cooldown and two-award daily cap");
            capped.ObserveToken(new("full", start.AddSeconds(1), 0, 0, 200000), start.AddSeconds(3));
            check(capped.Snapshot(start).Today.Xp == 100, "all four income sources sum to the 100 XP daily maximum");
            var token = Fresh("tokens"); token.StartRun(start);
            token.ObserveToken(new("history", start.AddSeconds(-1), 1000000, 0, 1000000), start);
            check(token.Snapshot(start).Today.WeightedTokens == 0, "startup historical usage is never imported as growth XP");
            var usage = new ObservedTokenUsage("one", start.AddSeconds(1), 10000, 9000, 4900);
            token.ObserveToken(usage, start.AddSeconds(2));
            check(token.Snapshot(start).Today.WeightedTokens == 5000 && token.Snapshot(start).Today.TokenXp == 5, "cached input is excluded and uncached input receives ten percent weight");
            token.ObserveToken(usage, start.AddSeconds(3));
            check(token.Snapshot(start).Today.WeightedTokens == 5000 && token.Snapshot(start).TotalXp == 5, "replayed token events cannot reward twice");
            token.ObserveToken(new("missing", start.AddSeconds(2), 150000, null, 100), start.AddSeconds(3));
            check(token.Snapshot(start).Today.WeightedTokens == 5100, "missing cache fields never imply zero cached input; known output still counts");
            token.ObserveToken(new("missing", start.AddSeconds(2), 150000, 1000, 100), start.AddSeconds(3));
            check(token.Snapshot(start).Today.WeightedTokens == 20000 && token.Snapshot(start).Today.TokenXp == 10, "late field completion credits only the missing weighted component and next tier difference");
            token.EndRun(start.AddMinutes(1));
            token = new(Path.Combine(root, "tokens"), zone); token.StartRun(start.AddMinutes(10));
            token.ObserveToken(usage, start.AddMinutes(10));
            token.ObserveToken(new("offline", start.AddMinutes(5), 0, 0, 900000), start.AddMinutes(10));
            check(token.Snapshot(start).Today.WeightedTokens == 20000, "persistent token dedup survives restart and excludes between-run usage");
            token.ObserveToken(new("delayed", start.AddSeconds(30), 0, 0, 60000), start.AddMinutes(10));
            check(token.Snapshot(start).Today.TokenXp == 15, "eligible delayed usage can be credited after restart");
            token.Tick(start.AddDays(31), 1, true, true, false);
            var oldXp = token.Snapshot(start).TotalXp;
            token.ObserveToken(usage, start.AddDays(31));
            check(token.Snapshot(start).TotalXp == oldXp, "pruning dedup keys also closes old runtime eligibility to prevent re-awards");
            var cross = Fresh("midnight"); var midnight = start.Date.AddDays(1); var before = new DateTimeOffset(midnight, start.Offset).AddSeconds(-1);
            cross.StartRun(before); cross.Tick(before.AddSeconds(2), 2, true, true, false);
            check(cross.Snapshot(before).Today.Seconds == 1 && cross.Snapshot(before.AddSeconds(2)).Today.Seconds == 1, "a tick crossing local midnight is split into separate daily ledgers");
            cross.ObserveToken(new("yesterday", before.AddMilliseconds(100), 0, 0, 5000), before.AddDays(1));
            check(cross.Snapshot(before).Today.TokenXp == 5 && cross.Snapshot(before.AddDays(1)).Today.TokenXp == 0, "delayed tokens retain their original date and daily cap");
            var focus = Fresh("focus"); focus.StartRun(start); focus.Focus(25); focus.Tick(start.AddSeconds(1), 1, true, false, false);
            check(focus.Snapshot(start).Today.Seconds == 1 && focus.Snapshot(start).FocusSeconds == 1499, "focus supports reading without bonus experience");
            focus.Tick(start.AddSeconds(2), 1, false, false, false);
            check(focus.Snapshot(start).FocusSeconds == 1499 && focus.Snapshot(start).Today.Seconds == 1, "lock pauses both focus timer and companionship");
            focus.Focus(0); focus.Tick(start.AddSeconds(3), 1, true, false, false);
            check(focus.Snapshot(start).Today.Seconds == 1, "ending focus restores normal idle detection");
            var levelingPath = DirectoryFor("leveling");
            File.WriteAllText(Path.Combine(levelingPath, "growth.json"), "{\"TotalXp\":329,\"Unlocked\":[\"heart\"],\"Vouchers\":9,\"Decoration\":\"badge\"}");
            var leveling = new GrowthEngine(levelingPath, zone); leveling.StartRun(start); leveling.Interact(start);
            var leveled = leveling.Snapshot(start);
            check(leveled.Level == 5 && leveled.LevelXp == 4, "level-up keeps XP carry-over without toy rewards");
            using var legacy = JsonDocument.Parse(File.ReadAllText(Path.Combine(levelingPath, "growth.json")));
            check(legacy.RootElement.GetProperty("Unlocked").GetArrayLength() == 1 && legacy.RootElement.GetProperty("Unlocked")[0].GetString() == "heart" &&
                legacy.RootElement.GetProperty("Vouchers").GetInt32() == 9, "legacy reward data is preserved but levels do not unlock toys or issue vouchers");
            var badgesPath = DirectoryFor("badges"); var days = Enumerable.Range(0, 6).ToDictionary(i => start.AddDays(-i - 1).ToString("yyyy-MM-dd"), _ => new GrowthDay { Seconds = 180, PresenceXp = 10, TimeXp = 1 });
            days["2026-10-04"] = new GrowthDay { Seconds = 179, TimeXp = 179d / 180 };
            File.WriteAllText(Path.Combine(badgesPath, "growth.json"), JsonSerializer.Serialize(new GrowthSave { Days = days, TotalXp = days.Values.Sum(d => d.Xp) }));
            var badges = new GrowthEngine(badgesPath, zone); badges.StartRun(start); badges.Tick(start.AddSeconds(1), 1, true, true, false);
            badges.Save();
            var badgeSave = JsonSerializer.Deserialize<GrowthSave>(File.ReadAllText(Path.Combine(badgesPath, "growth.json")))!;
            check(badges.Snapshot(start).Days == 7 && badgeSave.Badges.Count == 0, "companionship days remain counted without generating removed commemorations");
            var recoveredPath = DirectoryFor("recovery"); var recover = new GrowthEngine(recoveredPath, zone); recover.StartRun(start); recover.Interact(start); recover.Save();
            File.WriteAllText(Path.Combine(recoveredPath, "growth.json"), "broken-json"); recover = new(recoveredPath, zone);
            check(recover.Snapshot(start).TotalXp == 5 && recover.Warning?.Contains("备份") == true && Directory.GetFiles(recoveredPath, "*.invalid-*").Length == 1, "corrupt primary growth save restores the atomic backup and preserves evidence");
            var futurePath = DirectoryFor("future"); var futureJson = "{\"SchemaVersion\":99}"; File.WriteAllText(Path.Combine(futurePath, "growth.json"), futureJson);
            var future = new GrowthEngine(futurePath, zone); future.StartRun(start); future.Interact(start); future.Save();
            check(File.ReadAllText(Path.Combine(futurePath, "growth.json")) == futureJson && future.Warning is not null, "unknown future save versions remain read-only");
            var concurrent = Fresh("concurrent"); concurrent.StartRun(start);
            Parallel.For(0, 50, i => concurrent.ObserveToken(new("duplicate", start, 1000, 0, 5000), start.AddSeconds(1)));
            check(concurrent.Snapshot(start).Today.WeightedTokens == 5100 && concurrent.Snapshot(start).TotalXp == 5, "concurrent usage callbacks atomically deduplicate a shared event");
            ReaderChecks(DirectoryFor("reader"), start, zone, check);
        }
        finally
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temp, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
    private static void ReaderChecks(string home, DateTimeOffset now, TimeZoneInfo zone, Action<bool, string> check)
    {
        var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions); var path = Path.Combine(sessions, "thread.jsonl");
        string Row(object payload, string type = "event_msg", DateTimeOffset? at = null) => JsonSerializer.Serialize(new { timestamp = (at ?? now).ToString("O"), type, payload }) + "\n";
        object Counters(long total, long input, long? cached, long output) => new { total_tokens = total, input_tokens = input, cached_input_tokens = cached, output_tokens = output, reasoning_output_tokens = output / 2 };
        string Token(object total, object last, DateTimeOffset at) => Row(new { type = "token_count", info = new { total_token_usage = total, last_token_usage = last } }, at: at);
        File.WriteAllText(path, Row(new { id = "thread", source = "vscode" }, "session_meta") + Token(Counters(10000, 9000, 8000, 1000), Counters(100, 80, 70, 20), now));
        var reader = new LocalTokenUsageReader(home, zone); var events = new List<ObservedTokenUsage>(); reader.TokenObserved += events.Add; reader.Read(now);
        check(events.Count == 1 && events[0].InputTokens == 80 && events[0].CachedInputTokens == 70 && events[0].OutputTokens == 20, "first token observation uses last usage rather than inherited fork totals");
        File.AppendAllText(path, Token(Counters(10500, 9400, 8300, 1100), Counters(500, 400, 300, 100), now.AddSeconds(1))); reader.Read(now.AddSeconds(1));
        check(events.Count == 2 && events[1].InputTokens == 400 && events[1].CachedInputTokens == 300 && events[1].OutputTokens == 100, "incremental token observer computes component deltas without double-counting reasoning");
        File.AppendAllText(path, Token(Counters(10500, 9400, 8300, 1100), Counters(500, 400, 300, 100), now.AddSeconds(2))); reader.Read(now.AddSeconds(2));
        check(events.Count == 2, "duplicate cumulative counters emit no new growth event");
        File.AppendAllText(path, Token(Counters(100, 80, null, 20), Counters(100, 80, null, 20), now.AddSeconds(3))); reader.Read(now.AddSeconds(3)); var missing = events[^1];
        File.AppendAllText(path, Token(Counters(100, 80, 70, 20), Counters(100, 80, 70, 20), now.AddSeconds(4))); reader.Read(now.AddSeconds(4));
        check(events.Count == 4 && missing.Id == events[^1].Id && missing.CachedInputTokens is null && events[^1].CachedInputTokens == 70 && events[^1].At == missing.At, "counter reset and late cache metadata preserve the same event identity");
        check(events.All(e => e.Id.Length == 64 && !e.Id.Contains("thread")), "growth dedup identifiers contain hashes instead of task titles or dialogue");
        File.AppendAllText(path, Token(Counters(150, 100, null, 50), Counters(50, 20, null, 30), now.AddSeconds(5)));
        reader.Read(now.AddSeconds(5));
        File.AppendAllText(path, Token(Counters(1000, 900, 800, 100), Counters(100, 80, 70, 20), now.AddSeconds(6)));
        reader.Read(now.AddSeconds(6));
        check(events[^1].InputTokens == 80 && events[^1].CachedInputTokens == 70 && events[^1].OutputTokens == 50,
            "missing aggregate cache baseline uses paired last-request input and cache instead of inflating uncached input");
    }
}
