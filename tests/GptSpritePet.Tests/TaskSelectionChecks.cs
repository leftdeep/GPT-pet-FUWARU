using System.Text.Json;
using GptSpritePet.Core;

internal static class TaskSelectionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var first = new ActiveTaskUsage("first", "第一个任务", "turn1", 120000, 1000, now.AddMinutes(-1), now);
        var second = first with { Id = "second", Title = "第二个任务", TotalTokens = 240000, TurnTokens = 2000 };
        var snapshot = new AccountUsageSnapshot { LocalUsage = new(DateOnly.FromDateTime(now.LocalDateTime), 500000,
            [second, first], now, "测试", true) };
        check(snapshot.CurrentTask?.Id == "second", "automatic task selection uses the first active task");
        check((snapshot with { SelectedTaskId = "first" }).CurrentTask?.Id == "first", "manual task selection still works without rate tracking");
        check((snapshot with { SelectedTaskId = "finished" }).CurrentTask?.Id == "second", "a completed selected task falls back to an active task");
        var summary = UsageSummary.Create(snapshot with { SelectedTaskId = "first" }, now, 60);
        check(summary.Today.Value == "50 万" && summary.Task.Value == "12 万", "daily and selected task token totals remain independent");
        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<AccountUsageSnapshot>(json)!;
        check(restored.CurrentTask?.TotalTokens == 240000 && restored.CurrentTask.OutputRate is null,
            "task snapshots preserve totals when no output timing exists");
        var legacyJson = json.Replace(",\"OutputRate\":null", "", StringComparison.Ordinal);
        check(JsonSerializer.Deserialize<AccountUsageSnapshot>(legacyJson)?.CurrentTask?.TotalTokens == 240000,
            "older task snapshots without rate fields remain compatible");
        check(UsageSummary.Create(snapshot with { LocalUsage = null }, now, 60).Task.Value == "无任务", "missing active tasks display an explicit empty state");
    }
}
