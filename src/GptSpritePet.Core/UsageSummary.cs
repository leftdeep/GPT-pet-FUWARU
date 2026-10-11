using System.Globalization;

namespace GptSpritePet.Core;

public enum UsageMetricLevel { Unknown, Normal, Warning, Critical }
public sealed record UsageMetric(string Label, string Value, string Hint, bool Current,
    double? RemainingPercent = null, DateTimeOffset? ResetsAt = null)
{
    public UsageMetricLevel Level => !Current ? UsageMetricLevel.Unknown : RemainingPercent < 10 ? UsageMetricLevel.Critical :
        RemainingPercent < 20 ? UsageMetricLevel.Warning : UsageMetricLevel.Normal;
}
public sealed record UsageSummary(UsageMetric FiveHours, UsageMetric SevenDays, UsageMetric Today, UsageMetric Task, UsageMetric OutputRate)
{
    public static UsageSummary Create(AccountUsageSnapshot snapshot, DateTimeOffset now, int refreshSeconds)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var local = snapshot.LocalUsage?.Date == today ? snapshot.LocalUsage : null;
        var daily = local?.TodayTokens ?? snapshot.Tokens?.OnDate(today);
        var localHint = local?.TodayTokens is not null ? "本机 Codex 今日记录（系统本地日期）。输入包含缓存；输出已包含推理，不重复相加。" :
            "官方当天记录；可能延迟，未返回时不记为零。";
        var task = snapshot.CurrentTask;
        return new(Quota(snapshot, now, refreshSeconds, 300, "5小时余额"), Quota(snapshot, now, refreshSeconds, 10080, "7日余额"),
            new("当日Token", Count(daily), localHint + Exact(daily), local?.Available ?? snapshot.State == UsageConnectionState.Ready),
            new("任务Token", task is null ? "无任务" : Count(task.TotalTokens), task is null ? "尚未检测到本机正在执行的 Codex 任务。" :
                task.Title + "\n当前对话任务累计" + Exact(task.TotalTokens) + "\n本次执行：" + Count(task.TurnTokens) + Exact(task.TurnTokens),
                task is not null && local?.Available == true), Rate(task, local?.Available == true, now));
    }
    private static UsageMetric Rate(ActiveTaskUsage? task, bool available, DateTimeOffset now)
    {
        const string label = "输出速率（估算）";
        const string explanation = "新增输出 Token ÷ 对应日志事件间隔；不计输入和缓存，输出已包含推理。\n每 2 秒检查；可能包含思考、工具调用和等待，不等同于逐 Token 生成速度。";
        if (task is null) return new(label, "无任务", explanation, false);
        if (!available) return new(label, "—", task.Title + "\n本机记录暂不可读。\n" + explanation, false);
        var sample = task.OutputRate;
        if (sample?.IsAvailable(now) != true) return new(label, "等待样本", task.Title + "\n首次读取建立基线；等待带有效时长的新用量事件。\n" + explanation, false);
        var fresh = sample.IsFresh(now);
        var rate = sample.TokensPerSecond!.Value.ToString("0.#", CultureInfo.InvariantCulture) + " tok/s";
        return new(label, (fresh ? "" : "上次 ") + rate, task.Title + "\n" + explanation +
            $"\n样本：{sample.OutputTokens:N0} 输出 Token / {sample.DurationMilliseconds / 1000:0.###} 秒" +
            "\n最近样本：" + sample.SampledAt.ToLocalTime().ToString("HH:mm:ss") +
            (fresh ? "" : "\n超过 8 秒未见新样本，保留上次读数；3 分钟后清除。"), fresh);
    }
    private static UsageMetric Quota(AccountUsageSnapshot snapshot, DateTimeOffset now, int refreshSeconds, int minutes, string label)
    {
        var quota = snapshot.QuotaForWindow(minutes);
        var plan = snapshot.Account?.Plan;
        var pro = string.Equals(plan, "pro", StringComparison.OrdinalIgnoreCase) ||
            plan?.StartsWith("pro_", StringComparison.OrdinalIgnoreCase) == true || plan?.StartsWith("pro-", StringComparison.OrdinalIgnoreCase) == true;
        if (minutes == 300 && pro && snapshot.HasCurrentLimits(now, refreshSeconds) && !snapshot.Quotas.Any(q => q.WindowMinutes == 300))
            return new(label, "不限", "Pro 当前无 5 小时额度限制；7 日余额独立显示。", true);
        var current = snapshot.HasCurrentLimits(now, refreshSeconds) && quota?.IsCurrent(now) == true;
        var value = quota?.RemainingPercent is double remaining ? $"{remaining:0.#}%" + (current ? "" : "（旧）") : "—";
        var hint = quota is null ? "官方未返回此窗口，或多个额度桶无法确定对应窗口。" : quota.Label + " · " + quota.WindowLabel +
            "\n余额 = 100% − 官方已用百分比。" + (current ? "" : "\n上次数据，等待更新。") +
            (quota.ResetsAt is null ? "" : "\n重置：" + quota.ResetsAt.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        return new(label, value, hint, current, quota?.RemainingPercent, quota?.ResetsAt);
    }
    public static string Count(long? value) => value is null ? "—" : value >= 100_000_000 ? $"{value / 100_000_000d:0.##} 亿" :
        value >= 10_000 ? $"{value / 10_000d:0.##} 万" : value.Value.ToString("N0", CultureInfo.InvariantCulture);
    private static string Exact(long? value) => value is null ? "" : "\n" + value.Value.ToString("N0", CultureInfo.InvariantCulture) + " tokens";
}
