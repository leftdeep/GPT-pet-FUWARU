using System.Net;
using System.Text;
using System.Text.Json;
using GptSpritePet.Core;

internal static class ResetWatchChecks
{
    private sealed class Source : IResetFeedSource
    {
        public int Calls;
        public ResetFeedBatch Batch = new([], "codex-reset.com");
        public Exception? Error;
        public TaskCompletionSource? Wait;
        public async Task<ResetFeedBatch> FetchAsync(CancellationToken cancellation)
        {
            Calls++;
            if (Wait is not null) await Wait.Task.WaitAsync(cancellation);
            if (Error is not null) throw Error;
            return Batch;
        }
    }
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request)); }
    private static ResetNotice Notice(string id, DateTimeOffset at, ResetNoticeKind kind, string source = "codex-reset.com") =>
        new(id, at, kind, "测试公告", "https://x.com/thsottiaux/status/" + id, source);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static async Task Run(Action<bool, string> check)
    {
        check(ResetFeedParser.Classify("Global reset landing tomorrow at 10am") == ResetNoticeKind.Preview, "reset: explicit future announcement is a preview");
        check(ResetFeedParser.Classify("Over the next 28 days we will ship an improvement or a full reset") == ResetNoticeKind.Preview, "reset: a watch window is an announcement, not a completed refill");
        check(ResetFeedParser.Classify("Reset all propagated. Enjoy.") == ResetNoticeKind.Completed, "reset: completed propagation claim is recognized");
        check(ResetFeedParser.Classify("Usage limits have been reset for all paid users") == ResetNoticeKind.Completed, "reset: completed usage claim is recognized");
        check(ResetFeedParser.Classify("We will give a banked reset tomorrow") == ResetNoticeKind.Extra, "reset: banked cards never become a global reset");
        check(ResetFeedParser.Classify("Loading a banked reset in everyone's paid accounts") == ResetNoticeKind.Extra,
            "reset: announced banked card is an extra-credit notice");
        check(ResetFeedParser.Classify("Will we give a banked reset tomorrow?") == ResetNoticeKind.Candidate
            && ResetFeedParser.Classify("No banked reset today") == ResetNoticeKind.Candidate
            && ResetFeedParser.Classify("We will not give a banked reset tomorrow") == ResetNoticeKind.Candidate
            && ResetFeedParser.Classify("不会赠送重置卡") == ResetNoticeKind.Candidate,
            "reset: questions and denied card gifts do not become urgent extra-credit alerts");
        check(ResetFeedParser.Classify("Will we reset tomorrow?") == ResetNoticeKind.Candidate
            && ResetFeedParser.Classify("We will not reset tomorrow") == ResetNoticeKind.Candidate, "reset: questions and denials do not trigger urgent alerts");
        check(ResetFeedParser.Classify("We have not reset usage yet") == ResetNoticeKind.Candidate
            && ResetFeedParser.Classify("Tomorrow's reset was cancelled") == ResetNoticeKind.Candidate, "reset: negated completion and cancelled announcements stay quiet");
        check(ResetFeedParser.Classify("All right, simplifying things") is null && ResetFeedParser.Classify("Blissful reset company") == ResetNoticeKind.Candidate, "reset: unrelated posts and jokes stay quiet");
        check(ResetFeedParser.TweetUrl("javascript:alert(1)") is null && ResetFeedParser.TweetUrl("https://x.com/other/status/123") is null
            && ResetFeedParser.TweetUrl("Tibo: https://twitter.com/thsottiaux/status/123") == "https://x.com/thsottiaux/status/123", "reset: only canonical Tibo source links are launchable");

        using var timeline = JsonDocument.Parse("""{"events":[{"id":"123","group":"reset","announcement_state":"announced","announced_at":"2026-10-02T21:18:48Z","summary":"Reset all propagated","url":"https://x.com/thsottiaux/status/123"}]}""");
        using var feed = JsonDocument.Parse("""{"stale":false,"profile":{"handle":"thsottiaux"},"tweets":[{"id":"123","at":"2026-10-02T21:18:48Z","text":"Reset tomorrow","url":"https://x.com/thsottiaux/status/123"},{"id":"999","at":"2026-10-03T01:00:00Z","text":"Reset tomorrow","url":"https://x.com/other/status/999"}]}""");
        var parsed = ResetFeedParser.ParsePrimary(timeline.RootElement, feed.RootElement);
        check(parsed.Events.Length == 1 && parsed.Events[0].Kind == ResetNoticeKind.Completed, "reset: feed cannot demote timeline completion or import another author");
        using var deniedTimeline = JsonDocument.Parse("""{"events":[{"group":"credits","announced_at":"2026-10-02T21:18:48Z","summary":"No banked reset today","url":"https://x.com/thsottiaux/status/124"}]}""");
        using var emptyFeed = JsonDocument.Parse("""{"profile":{"handle":"thsottiaux"},"tweets":[]}""");
        check(ResetFeedParser.ParsePrimary(deniedTimeline.RootElement, emptyFeed.RootElement).Events.Single().Kind == ResetNoticeKind.Candidate,
            "reset: credit grouping cannot promote a denied card gift to an alert");
        using var fallback = JsonDocument.Parse("""{"events":[{"published_at":"2026-10-03T01:00:00Z","title":"Reset tomorrow","summary":"We will reset tomorrow","source":"manual://screenshot"},{"date":"2026-10-03","title":"Reset all propagated","summary":"Reset all propagated","source":"https://x.com/thsottiaux/status/999"}]}""");
        var backup = ResetFeedParser.ParseFallback(fallback.RootElement);
        check(backup.Events.Any(e => e.Url is null && e.Kind == ResetNoticeKind.Candidate)
            && backup.Events.Any(e => e.Kind == ResetNoticeKind.Completed && !e.ExactTime), "reset: unlinked references are weak, date-only records retain their precision");
        var root = Path.Combine(Path.GetTempPath(), "pet-reset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var source = new Source();
        var oldReset = Notice("100", now.AddDays(-3), ResetNoticeKind.Completed);
        source.Batch = new([oldReset, Notice("101", now.AddHours(-2), ResetNoticeKind.Preview),
            Notice("109", now.AddHours(-1), ResetNoticeKind.Extra)], "codex-reset.com");
        try
        {
            using (var service = new ResetWatchService(root, source, () => now, false))
            {
                service.Configure(true); await service.CheckAsync();
                check(service.Snapshot.Pending.Length == 0 && service.Snapshot.LastReset?.At == oldReset.At, "reset: first check establishes history without alerting old notices");
                check(service.Snapshot.NextCheckAt == now.AddHours(1), "reset: normal polling is scheduled exactly one hour from the attempt");
                await service.CheckAsync(true);
                check(source.Calls == 1, "reset: repeated manual refresh honors one-minute minimum");
                now = now.AddHours(1);
                var upcoming = Notice("102", now.AddMinutes(-5), ResetNoticeKind.Preview);
                source.Batch = new([upcoming, oldReset], "codex-reset.com");
                await service.CheckAsync();
                check(service.Snapshot.Pending.Single().Id == "102" && service.Snapshot.LastReset?.At == oldReset.At,
                    "reset: a new preview queues persistent alert without changing the reset clock");
                now = now.AddMinutes(2); await service.CheckAsync(true);
                check(service.Snapshot.Pending.Length == 1, "reset: repeated announcement cannot add duplicate alerts");
                using var other = new ResetWatchService(root, new Source(), () => now, false);
                other.Configure(true); await other.CheckAsync(true);
                check(!other.Snapshot.IsOwner && other.Snapshot.Pending.Length == 0 && other.Snapshot.Events.Any(e => e.Id == "102"), "reset: concurrent pet instances share history and one monitor owner");
            }
            using (var resumed = new ResetWatchService(root, source, () => now, false))
            {
                resumed.Configure(true); now = now.AddMinutes(2); await resumed.CheckAsync();
                check(resumed.Snapshot.Pending.Any(e => e.Id == "102"), "reset: unread alert survives application restart");
                await resumed.AcknowledgeAsync("102:Preview");
                check(resumed.Snapshot.Pending.Length == 0, "reset: acknowledgment removes persistent alert");
                now = now.AddMinutes(2);
                source.Batch = new([Notice("102", now.AddMinutes(-1), ResetNoticeKind.Preview, "codexreset.today"), oldReset], "codexreset.today");
                await resumed.CheckAsync(true);
                check(resumed.Snapshot.Pending.Length == 0, "reset: acknowledged tweet is deduplicated across providers");
                now = now.AddMinutes(2);
                source.Batch = new([Notice("103", now.AddMinutes(-1), ResetNoticeKind.Extra), oldReset], "codex-reset.com");
                await resumed.CheckAsync(true);
                check(resumed.Snapshot.LastReset?.Id == "100" && resumed.Snapshot.Pending.Single().Key == "103:Extra",
                    "reset: extra credits queue an alert without changing the last-reset clock");
                await resumed.AcknowledgeAsync("103:Extra");
                now = now.AddMinutes(2);
                source.Batch = new([Notice("104", now.AddMinutes(-1), ResetNoticeKind.Preview)], "codex-reset.com", true);
                await resumed.CheckAsync(true);
                check(resumed.Snapshot.Pending.Length == 0, "reset: stale provider cannot produce an urgent alert");
                now = now.AddMinutes(2); source.Batch = source.Batch with { Stale = false }; await resumed.CheckAsync(true);
                check(resumed.Snapshot.Pending.Any(e => e.Id == "104"), "reset: stale observation does not swallow a later fresh alert");
                await resumed.AcknowledgeAsync("104:Preview");
                now = now.AddMinutes(2);
                var done = Notice("105", now.AddMinutes(-1), ResetNoticeKind.Completed);
                source.Batch = new([done], "codex-reset.com"); await resumed.CheckAsync(true);
                check(resumed.Snapshot.LastReset?.Id == "105" && resumed.Snapshot.Pending.Single().Kind == ResetNoticeKind.Completed, "reset: completion advances clock and gets one notification");
                source.Error = new HttpRequestException("offline"); now = now.AddMinutes(2); await resumed.CheckAsync(true);
                check(resumed.Snapshot.LastReset?.Id == "105" && resumed.Snapshot.Pending.Length == 1 && resumed.Snapshot.NextCheckAt == now.AddMinutes(5), "reset: offline failure keeps clock/unread state and backs off");
                now = now.AddMinutes(5); await resumed.CheckAsync();
                check(resumed.Snapshot.NextCheckAt == now.AddMinutes(15), "reset: consecutive failure backs off to fifteen minutes");
                source.Error = new ResetRateLimitException(TimeSpan.FromHours(3)); now = now.AddMinutes(15); await resumed.CheckAsync();
                check(resumed.Snapshot.NextCheckAt == now.AddHours(3), "reset: Retry-After overrides ordinary polling");
                source.Error = null; source.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously); now = now.AddHours(3);
                var first = resumed.CheckAsync(); var calls = source.Calls; await resumed.CheckAsync(true);
                check(resumed.Snapshot.Busy && source.Calls == calls, "reset: network requests never overlap");
                source.Wait.SetResult(); await first; source.Wait = null;
                resumed.Configure(false); var disabledCalls = source.Calls; await resumed.CheckAsync(true);
                check(source.Calls == disabledCalls && !resumed.Snapshot.Enabled, "reset: monitoring toggle suspends network checks");
            }
            var cardRoot = Path.Combine(root, "card-alerts"); Directory.CreateDirectory(cardRoot);
            var card = Notice("201", now.AddHours(-2), ResetNoticeKind.Extra);
            var cardSource = new Source { Batch = new([card, oldReset], "codex-reset.com") };
            // Prior versions recorded cards as seen without notifying. Preserve that actual upgrade case.
            File.WriteAllText(Path.Combine(cardRoot, "reset-watch.json"), JsonSerializer.Serialize(new ResetWatchCache
                { Initialized = true, LastReset = oldReset, Events = [card, oldReset], Seen = [card.Key, oldReset.Key] }));
            using (var upgraded = new ResetWatchService(cardRoot, cardSource, () => now, false))
            {
                upgraded.Configure(true); await upgraded.CheckAsync();
                check(upgraded.Snapshot.Pending.Single().Key == card.Key && upgraded.Snapshot.LastReset?.Id == oldReset.Id,
                    "reset: recent history-only card gets one notification after upgrading");
                now = now.AddMinutes(2); await upgraded.CheckAsync(true);
                check(upgraded.Snapshot.Pending.Length == 1, "reset: card refresh keeps a single persistent notification");
            }
            using (var resumedCard = new ResetWatchService(cardRoot, cardSource, () => now, false))
            {
                resumedCard.Configure(true); now = now.AddMinutes(2); await resumedCard.CheckAsync();
                check(resumedCard.Snapshot.Pending.Single().Key == card.Key, "reset: unread card notification survives restart");
                await resumedCard.AcknowledgeAsync(card.Key);
                now = now.AddMinutes(2); cardSource.Batch = new([card with { Source = "codexreset.today" }], "codexreset.today");
                await resumedCard.CheckAsync(true);
                check(resumedCard.Snapshot.Pending.Length == 0, "reset: acknowledged card stays deduplicated across providers");
                now = now.AddMinutes(2);
                var staleCard = Notice("202", now.AddMinutes(-1), ResetNoticeKind.Extra);
                cardSource.Batch = new([staleCard], "codex-reset.com", true); await resumedCard.CheckAsync(true);
                check(resumedCard.Snapshot.Pending.Length == 0, "reset: stale card feed cannot produce an alert");
                now = now.AddMinutes(2); cardSource.Batch = cardSource.Batch with { Stale = false }; await resumedCard.CheckAsync(true);
                check(resumedCard.Snapshot.Pending.Single().Key == staleCard.Key, "reset: stale card does not swallow its later fresh notification");
                await resumedCard.AcknowledgeAsync(staleCard.Key);
                now = now.AddMinutes(2); cardSource.Batch = new([Notice("203", now.AddHours(-49), ResetNoticeKind.Extra)], "codex-reset.com");
                await resumedCard.CheckAsync(true);
                check(resumedCard.Snapshot.Pending.Length == 0 && resumedCard.Snapshot.LastReset?.Id == oldReset.Id,
                    "reset: old cards remain quiet and never advance the public reset clock");
            }
            var cachePath = Path.Combine(root, "reset-watch.json");
            check(new FileInfo(cachePath).Length < 100 * 1024, "reset: bounded cache stays below one hundred KB");
            File.WriteAllText(cachePath, "{broken");
            using (var corrupt = new ResetWatchService(root, new Source(), () => now, false))
                check(!corrupt.Snapshot.Initialized && corrupt.Snapshot.LastReset is null, "reset: corrupt cache recovers without inventing a reset timestamp");
            var requests = new List<string>();
            using var transport = new Transport(request =>
            {
                var uri = request.RequestUri!.ToString(); requests.Add(uri);
                return new HttpResponseMessage(uri.Contains("codexreset.today") ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{\"events\":[]}", Encoding.UTF8, "application/json") };
            });
            using var client = new HttpClient(transport); using var provider = new PublicResetFeedSource(client);
            var fallbackBatch = await provider.FetchAsync(CancellationToken.None);
            check(fallbackBatch.Source == "codexreset.today" && requests.Count == 2, "reset: failed primary switches to public backup without a browser");
            using var limitTransport = new Transport(_ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromMinutes(40)); return response; });
            using var limitClient = new HttpClient(limitTransport); using var limited = new PublicResetFeedSource(limitClient);
            var obeysLimit = false;
            try { await limited.FetchAsync(CancellationToken.None); } catch (ResetRateLimitException limit) { obeysLimit = limit.RetryAfter == TimeSpan.FromMinutes(40); }
            check(obeysLimit, "reset: HTTP 429 preserves server Retry-After instead of bypassing its rate limit");
            var many = new Source { Batch = new(Enumerable.Range(1, 1000).Select(i => Notice(i.ToString(), now.AddMinutes(-i),
                i == 1000 ? ResetNoticeKind.Completed : ResetNoticeKind.Preview)).ToArray(), "codex-reset.com") };
            using var bounded = new ResetWatchService(Path.Combine(root, "bounded"), many, () => now, false);
            bounded.Configure(true); await bounded.CheckAsync();
            check(bounded.Snapshot.Events.Length == 30 && bounded.Snapshot.LastReset?.Id == "1000"
                && new FileInfo(Path.Combine(root, "bounded", "reset-watch.json")).Length < 100 * 1024,
                "reset: thousand-event feed stays bounded and preserves a reset outside the history window");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
}
