using System.Diagnostics;
using System.Text.Json;
using GptSpritePet.Core;

internal static class LocalScanChecks
{
    public static void Run(Action<bool, string> check)
    {
        var home = Path.Combine(Path.GetTempPath(), "pet-scan-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        string Day(DateTimeOffset day) => Path.Combine(home, "sessions", day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
        var history = Day(now.AddYears(-1)); var recent = Day(now);
        Directory.CreateDirectory(history); Directory.CreateDirectory(recent);
        string Log(string id, bool complete = false) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + "\n" +
            JsonSerializer.Serialize(new { type = "event_msg", timestamp = now.ToString("O"), payload = new { type = complete ? "task_complete" : "task_started", turn_id = "turn" } }) + "\n";
        try
        {
            for (var i = 0; i < 5000; i++)
            {
                var path = Path.Combine(history, $"old-{i}.jsonl"); File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, now.AddYears(-1).UtcDateTime);
            }
            File.WriteAllText(Path.Combine(recent, "active.jsonl"), Log("active"));
            var reader = new LocalTokenUsageReader(home, TimeZoneInfo.Utc);
            var timer = Stopwatch.StartNew(); reader.Read(now); var firstMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            for (var i = 1; i <= 15; i++) reader.Read(now.AddSeconds(i * 2));
            var pollMs = timer.Elapsed.TotalMilliseconds; var metrics = reader.ScanMetrics;
            check(metrics.FullScans == 1 && metrics.DiscoveredFiles < 5100,
                "15 incremental polls avoid revisiting 5,000 historical logs");
            check(metrics.FilePolls == 16, "unchanged historical files have no per-poll metadata work");
            File.WriteAllText(Path.Combine(recent, "new.jsonl"), Log("new"));
            check(reader.Read(now.AddSeconds(32)).Tasks.Any(t => t.Id == "new"), "a task in today's folder appears on the next 2-second poll");
            var unusual = Path.Combine(home, "sessions", "custom"); Directory.CreateDirectory(unusual);
            File.WriteAllText(Path.Combine(unusual, "unusual.jsonl"), Log("unusual"));
            check(reader.Read(now.AddSeconds(61)).Tasks.Any(t => t.Id == "unusual"), "periodic reconciliation finds tasks in nonstandard folders");
            var archive = Path.Combine(home, "archived_sessions"); Directory.CreateDirectory(archive);
            var archived = Path.Combine(archive, "active.jsonl"); File.Move(Path.Combine(recent, "active.jsonl"), archived);
            File.AppendAllText(archived, Log("active", true));
            reader.Read(now.AddSeconds(63));
            check(reader.Read(now.AddSeconds(65)).Tasks.All(t => t.Id != "active"), "archive moves preserve cursors and keep reading completion events");
            var scans = reader.ScanMetrics.FullScans; File.Delete(archived);
            reader.Read(now.AddSeconds(67)); reader.Read(now.AddSeconds(69)); reader.Read(now.AddSeconds(71));
            check(reader.ScanMetrics.FullScans <= scans + 1, "deleted logs cannot force a full history scan every poll");
            Console.WriteLine($"SCAN BENCHMARK: 5000 old files; initial {firstMs:0.##} ms; 15 cached polls {pollMs:0.##} ms; full scans {metrics.FullScans}; discovered entries {metrics.DiscoveredFiles}.");
        }
        finally
        {
            var boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(home).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid fixture cleanup path.");
            Directory.Delete(home, true);
        }
    }
}
