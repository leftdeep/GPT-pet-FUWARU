using System.Text;
using System.Text.Json;
using GptSpritePet.Core;

internal static class OutputRateChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var start = DateTimeOffset.Now.AddMinutes(-1);
        var home = Path.Combine(Path.GetTempPath(), "GptSpritePet-rate-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var one = Path.Combine(sessions, "one.jsonl");
        string Row(DateTimeOffset at, string type, object payload) => JsonSerializer.Serialize(new { timestamp = at.ToString("O"), type, payload }) + "\n";
        string Meta(string id) => Row(start, "session_meta", new { id, source = "vscode" });
        string Event(DateTimeOffset at, string type, string turn = "turn") => Row(at, "event_msg", new { type, turn_id = turn });
        string Tokens(DateTimeOffset at, long total, long? output, long? lastOutput = null) => Row(at, "event_msg", new
        {
            type = "token_count", info = new
            {
                total_token_usage = new { total_tokens = total, input_tokens = total - (output ?? 0), cached_input_tokens = 1000, output_tokens = output, reasoning_output_tokens = 99 },
                last_token_usage = new { total_tokens = 200, output_tokens = lastOutput }
            }
        });
        void Append(string text) => File.AppendAllText(one, text, new UTF8Encoding(false));
        var reader = new LocalTokenUsageReader(home);
        ActiveTaskUsage Read(int second) => reader.Read(start.AddSeconds(second)).Tasks.Single(t => t.Id == "one");
        bool Rate(ActiveTaskUsage task, double expected) => task.OutputRate?.TokensPerSecond is double actual && Math.Abs(actual - expected) < .0001;
        UsageMetric Metric(ActiveTaskUsage task, DateTimeOffset now, bool available = true) => UsageSummary.Create(new()
        {
            LocalUsage = new(DateOnly.FromDateTime(now.LocalDateTime), 100, [task], now, "test", available)
        }, now, 60).OutputRate;
        try
        {
            Append(Meta("one") + Event(start, "task_started") + Tokens(start.AddSeconds(1), 10000, 200, 200));
            check(Read(1).OutputRate is null, "rate startup replay establishes a baseline without showing historical speed");
            Append(Tokens(start.AddSeconds(5), 50000, 600));
            var task = Read(6);
            check(Rate(task, 100), "400 new output tokens over four event seconds gives 100 tok/s despite 40000 total-token growth");
            check(task.OutputRate is { OutputTokens: 400, DurationMilliseconds: 4000 }, "rate numerator and duration describe the same new usage interval");
            check(Metric(task, start.AddSeconds(6)).Value == "100 tok/s", "fresh selected-task rate is formatted in tok/s");
            check(Metric(task, start.AddSeconds(14)).Value == "上次 100 tok/s" && !Metric(task, start.AddSeconds(14)).Current,
                "eight-second-old rates are explicitly historical rather than active throughput");
            check(Metric(task, start.AddSeconds(185)).Value == "等待样本", "three-minute-old rates expire in presentation even without a newer snapshot");
            check(Metric(task, start.AddSeconds(6), false).Value == "—", "unreadable local logs cannot present a current rate");
            check(Metric(task with { OutputRate = null }, start.AddSeconds(6)).Value == "等待样本", "missing timing is unknown instead of zero");
            check(Read(7).OutputRate == task.OutputRate, "unchanged polls hold a reading without recalculating against poll time");
            Append(Tokens(start.AddSeconds(5), 50000, 600));
            check(Read(8).OutputRate == task.OutputRate, "repeated cumulative snapshots do not produce an extra speed sample");
            Append(Tokens(start.AddSeconds(7), 50500, 800) + Tokens(start.AddSeconds(13), 52000, 1100));
            task = Read(13);
            check(Rate(task, 62.5) && task.OutputRate?.DurationMilliseconds == 8000,
                "batched log writes use summed matched durations, not arithmetic mean of individual rates or polling interval");
            Append(Tokens(start.AddSeconds(13), 53000, 1200) + Tokens(start.AddSeconds(12), 54000, 1300));
            check(Read(14).OutputRate == task.OutputRate, "equal and backward timestamps cannot generate infinite or negative speed");
            Append(Tokens(start.AddSeconds(15), 55000, 1500));
            check(Rate(Read(15), 100), "valid timestamps recover after rejected timing without reusing unpaired output");
            Append(Event(start.AddSeconds(20), "task_started", "next"));
            check(Read(20).OutputRate is null, "a new turn clears the previous turn's displayed speed");
            Append(Tokens(start.AddSeconds(22), 56000, 1700));
            check(Rate(Read(22), 100), "first usage in a new turn measures from its start rather than the prior idle gap");
            Append(Tokens(start.AddSeconds(24), 10, 0, 0));
            check(Read(24).OutputRate is null, "counter regression invalidates the rate even when the reset's output is zero");
            Append(Tokens(start.AddSeconds(26), 410, 200));
            check(Rate(Read(26), 100), "counter regression establishes a fresh baseline for later observations");
            Append(Tokens(start.AddSeconds(28), 610, null));
            check(Read(28).OutputRate is null, "missing output counters clear a sample instead of falling back to total tokens");
            Append(Tokens(start.AddSeconds(30), 810, 400, 200));
            check(Rate(Read(30), 100), "last-response output fills missing cumulative baseline without including unmeasured intervals");
            Append(Tokens(start.AddSeconds(32), 1010, 400));
            check(Rate(Read(32), 0) && Metric(Read(32), start.AddSeconds(32)).Value == "0 tok/s",
                "an observed input-only usage interval is an actual zero-output sample");
            Append(Row(start.AddSeconds(34), "turn_context", new { model = "test" }) + Tokens(start.AddSeconds(35), 1110, 500));
            check(Rate(Read(35), 100), "turn_context supplies the timing anchor for a response");
            Append(Event(start.AddSeconds(100), "user_message") + Tokens(start.AddSeconds(102), 1310, 700));
            task = Read(102);
            check(Rate(task, 100), "a resumed user message prevents the previous idle period from entering the sample");
            var two = Path.Combine(sessions, "two.jsonl");
            File.WriteAllText(two, Meta("two") + Event(start.AddSeconds(101), "task_started") + Tokens(start.AddSeconds(102), 1000, 100, 100));
            reader.Read(start.AddSeconds(102));
            File.AppendAllText(two, Tokens(start.AddSeconds(104), 1400, 500));
            var local = reader.Read(start.AddSeconds(104));
            var snapshot = new AccountUsageSnapshot { LocalUsage = local, SelectedTaskId = "one" };
            check(UsageSummary.Create(snapshot, start.AddSeconds(104), 60).OutputRate.Value == "100 tok/s" &&
                UsageSummary.Create(snapshot with { SelectedTaskId = "two" }, start.AddSeconds(104), 60).OutputRate.Value == "200 tok/s",
                "simultaneous tasks retain independent rates and task selection changes the displayed rate");
            var partial = Tokens(start.AddSeconds(106), 1510, 900);
            Append(partial[..^5]);
            check(Rate(Read(106), 100), "partially written lines cannot create a partial rate sample");
            Append(partial[^5..]);
            check(Rate(Read(106), 50), "completed partial lines pair the output with the original event time exactly once");
            var archive = Path.Combine(home, "archived_sessions"); Directory.CreateDirectory(archive);
            File.Move(one, Path.Combine(archive, "one.jsonl"));
            check(Rate(Read(107), 50), "archive relocation preserves timing and does not replay the rate");
            check(Read(286).OutputRate is null, "retained rate state is released after three minutes without usage");
            check(new OutputRateSample(10, 0, start).TokensPerSecond is null && new OutputRateSample(10, double.NaN, start).TokensPerSecond is null,
                "invalid durations are rejected rather than formatted as non-finite speed");
            check(!new OutputRateSample(10, 1000, start.AddSeconds(10)).IsAvailable(start), "future event timestamps are unavailable");
            File.AppendAllText(Path.Combine(archive, "one.jsonl"), Event(start.AddSeconds(288), "task_complete", "next"));
            check(reader.Read(start.AddSeconds(288)).Tasks.All(t => t.Id != "one"), "completion removes the task and its speed from live selection");
            check(UsageSummary.Create(new(), start, 60).OutputRate.Value == "无任务", "no active task has a distinct rate state");
        }
        finally
        {
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(home).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(home, true);
        }
    }
}
