using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GptSpritePet.Core;
using static GptSpritePet.PetTheme;

namespace GptSpritePet;

internal sealed class UsagePage : UserControl, IDisposable
{
    private readonly AccountUsageService _service;
    private readonly Func<PetSettings> _settings;
    private readonly TextBlock _account = Text("正在连接…", 13), _status = Text("", 12), _dates = Text("", 11);
    private readonly UniformGrid _metrics = new() { Columns = 2 };
    private readonly ComboBox _taskSelector = new() { Height = 32, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _taskInfo = Text("尚未检测到进行中任务", 12);
    private readonly TextBlock _outputRate = Text("等待样本", 21, true);
    private string[] _taskIds = [];
    private bool _updatingTasks;
    private sealed record TaskChoice(string? Id, string Title) { public override string ToString() => Title; }
    private readonly StackPanel _quotas = new(), _days = new();
    private readonly Button _refresh, _login, _cancel;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private AccountUsageSnapshot _snapshot = new();
    internal bool CountdownRunning => _countdown.IsEnabled;
    public UsagePage(AccountUsageService service, Func<PetSettings> settings)
    {
        _service = service; _settings = settings;
        FontSize = 13;
        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        _refresh = Button("刷新", (_, _) => _service.RequestRefresh()); buttons.Children.Add(_refresh);
        _login = Button("登录 ChatGPT", async (_, _) => await LoginAsync()); buttons.Children.Add(_login);
        _cancel = Button("取消登录", async (sender, _) => { await _service.CancelLoginAsync(); ((Button)sender).Visibility = Visibility.Collapsed; });
        _cancel.Visibility = Visibility.Collapsed; buttons.Children.Add(_cancel);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        var panel = new StackPanel(); scroll.Content = panel;
        panel.Children.Add(Text("余额与用量", 23, true)); panel.Children.Add(_account);
        _account.Margin = new Thickness(0, 6, 0, 6); panel.Children.Add(_status);
        var tokens = Card(panel, "当前用量"); tokens.Children.Add(_metrics); tokens.Children.Add(_dates);
        _dates.Margin = new Thickness(0, 8, 0, 0);
        var tasks = Card(panel, "正在进行的任务"); tasks.Children.Add(_taskSelector);
        tasks.Children.Add(_taskInfo);
        var rate = Card(panel, "输出速率（估算）"); rate.Children.Add(_outputRate);
        rate.Children.Add(Text("只计算所选任务的输出，含推理；每 2 秒读取日志。8 秒无新样本后标记为上次读数，3 分钟后清除。", 11));
        _taskSelector.SelectionChanged += (_, _) =>
        {
            if (!_updatingTasks && _taskSelector.SelectedItem is TaskChoice choice) _service.SelectTask(choice.Id);
        };
        Card(panel, "剩余额度").Children.Add(_quotas);
        Card(panel, "近七日记录").Children.Add(_days);
        panel.Children.Add(Text("额度来自官方账号接口。今日与任务 Token 优先读取本机 Codex 记录，每 2 秒检查；含缓存输入及输出，不等同于扣费 Token。每日官方统计可能延迟。", 11));
        panel.Children.Add(Text("登录使用本机 Codex 的账号连接，不需要填写密码或 API Key。", 11));
        _countdown.Tick += (_, _) => { RenderQuotas(); RenderMetrics(); };
        Loaded += (_, _) => { Update(_service.Snapshot); _countdown.Start(); };
        Unloaded += (_, _) => _countdown.Stop();
        Update(service.Snapshot);
    }
    public void Dispose() => _countdown.Stop();
    public void Update(AccountUsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        _account.Text = snapshot.Account is null ? "尚未连接账号" : snapshot.Account.Label +
            (snapshot.Account.Plan is null ? "" : " · " + snapshot.Account.Plan.ToUpperInvariant());
        _status.Text = snapshot.Busy ? "正在更新…" : snapshot.Message + $" · 每 {_settings().UsageRefreshSeconds} 秒刷新";
        _status.Foreground = Brush(snapshot.State is UsageConnectionState.Ready ? "#8063AC" : "#796541");
        _refresh.IsEnabled = _settings().UsageEnabled && !snapshot.Busy;
        _login.Visibility = snapshot.Account is null || snapshot.State == UsageConnectionState.Unsupported ? Visibility.Visible : Visibility.Collapsed;
        _login.IsEnabled = _settings().UsageEnabled && snapshot.State != UsageConnectionState.MissingClient && !snapshot.Busy;
        var today = DateOnly.FromDateTime(DateTime.Now);
        RenderMetrics(); UpdateTasks();
        _dates.Text = "最新统计日：" + (snapshot.Tokens?.LatestDate?.ToString("yyyy-MM-dd") ?? "暂无") +
            "\n官方 Token 更新：" + Stamp(snapshot.TokensUpdatedAt) + "\n额度更新：" + Stamp(snapshot.LimitsUpdatedAt) +
            "\n本机记录检查：" + Stamp(snapshot.LocalUsage?.UpdatedAt);
        RenderQuotas(); RenderDays(today);
    }
    private void RenderMetrics()
    {
        _metrics.Children.Clear();
        var summary = UsageSummary.Create(_snapshot, DateTimeOffset.Now, _settings().UsageRefreshSeconds);
        _outputRate.Text = summary.OutputRate.Value;
        _outputRate.ToolTip = summary.OutputRate.Hint;
        _outputRate.Foreground = Brush(summary.OutputRate.Current ? "#8063AC" : "#94899F");
        foreach (var metric in new[] { summary.FiveHours, summary.SevenDays, summary.Today, summary.Task })
        {
            var box = new StackPanel { Margin = new Thickness(12, 10, 12, 10), ToolTip = metric.Hint };
            box.Children.Add(Text(metric.Label, 11));
            var value = Text(metric.Value, metric.Value.Length > 9 ? 16 : 23, true);
            if (!metric.Current) value.Foreground = Brush("#94899F");
            box.Children.Add(value); _metrics.Children.Add(new Border { Child = box, CornerRadius = new CornerRadius(10), Background = Brush("#F5EFFB"), Margin = new Thickness(3) });
        }
    }
    private void UpdateTasks()
    {
        var tasks = _snapshot.LocalUsage?.Tasks ?? [];
        var ids = tasks.Select(t => t.Id + "|" + t.Title).ToArray();
        _updatingTasks = true;
        try
        {
            if (_taskSelector.Items.Count == 0 || !_taskIds.SequenceEqual(ids))
            {
                _taskIds = ids; _taskSelector.Items.Clear();
                _taskSelector.Items.Add(new TaskChoice(null, "自动 · 最近开始的进行中任务"));
                foreach (var task in tasks) _taskSelector.Items.Add(new TaskChoice(task.Id, task.Title));
            }
            _taskSelector.SelectedIndex = _snapshot.SelectedTaskId is null ? 0 :
                Math.Max(0, Array.FindIndex(tasks, t => t.Id == _snapshot.SelectedTaskId) + 1);
            _taskSelector.IsEnabled = tasks.Length > 0;
            _taskInfo.Text = _snapshot.CurrentTask is { } current ? current.Title +
                "\n本次执行：" + UsageSummary.Count(current.TurnTokens) + " tokens" +
                "\n对话累计：" + UsageSummary.Count(current.TotalTokens) + " tokens" +
                "\n最近用量事件：" + Stamp(current.UpdatedAt) + (tasks.Length > 1 ? $"\n同时进行 {tasks.Length} 个任务，可在上方切换。" : "") :
                "暂无进行中任务；完成或中断后自动移出列表。";
            _taskInfo.ToolTip = _snapshot.LocalUsage?.Message;
        }
        finally { _updatingTasks = false; }
    }
    private void RenderQuotas()
    {
        _quotas.Children.Clear(); var now = DateTimeOffset.Now;
        if (_snapshot.Quotas.Length == 0) { _quotas.Children.Add(Text("暂无额度数据", 13)); return; }
        foreach (var quota in _snapshot.Quotas)
        {
            var current = _snapshot.HasCurrentLimits(now, _settings().UsageRefreshSeconds) && quota.IsCurrent(now);
            var remaining = quota.RemainingPercent;
            var color = !current ? "#94899F" : remaining < 10 ? "#C45757" : remaining < 20 ? "#B98A28" : "#8063AC";
            var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = Text(quota.Label + " · " + quota.WindowLabel, 13);
            var value = Text(remaining.HasValue ? $"剩余 {remaining:0.#}%" : "暂无百分比", 13, true); value.Foreground = Brush(color);
            row.Children.Add(label); row.Children.Add(value); Grid.SetColumn(value, 1); _quotas.Children.Add(row);
            _quotas.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = remaining ?? 0, Height = 6,
                Foreground = Brush(color), Background = Brush("#F0EAF6"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 7, 0, 5) });
            var countdown = quota.ResetsAt is null ? "暂无重置时间" : !quota.IsCurrent(now) ? "等待官方更新重置后的额度" :
                "距重置 " + Duration(quota.ResetsAt.Value - now) + " · " + quota.ResetsAt.Value.ToLocalTime().ToString("MM-dd HH:mm");
            _quotas.Children.Add(Text((current ? "" : "上次数据 · ") + countdown, 11));
            _quotas.Children.Add(new Border { Height = 10 });
        }
    }
    private void RenderDays(DateOnly today)
    {
        _days.Children.Clear();
        var recent = _snapshot.Tokens?.Daily?.Where(x => x.Date >= today.AddDays(-6) && x.Date <= today).ToArray();
        var max = recent is { Length: > 0 } ? Math.Max(1, recent.Max(x => x.Tokens)) : 1;
        for (var offset = -6; offset <= 0; offset++)
        {
            var date = today.AddDays(offset); var value = _snapshot.Tokens?.OnDate(date);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(52) }); row.ColumnDefinitions.Add(new());
            row.ColumnDefinitions.Add(new() { Width = new GridLength(83) });
            row.Children.Add(Text(date.ToString("MM-dd"), 11));
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = value.HasValue ? (double)value.Value / max : 0,
                Height = 5, VerticalAlignment = VerticalAlignment.Center, Background = Brush("#F0EAF6"), Foreground = Brush("#A98CCB"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 9, 0) };
            Grid.SetColumn(bar, 1); row.Children.Add(bar);
            var amount = Text(value.HasValue ? Count(value) : "未返回", 11); amount.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(amount, 2); row.Children.Add(amount); _days.Children.Add(row);
        }
    }
    private async Task LoginAsync()
    {
        _login.IsEnabled = false;
        try
        {
            var uri = await _service.StartLoginAsync();
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            _status.Text = "请在官方网页完成登录，完成后将自动更新。"; _cancel.Visibility = Visibility.Visible;
        }
        catch (Exception) { _status.Text = "暂时无法打开登录，请检查本机 Codex 或稍后重试。"; }
        finally { _login.IsEnabled = true; }
    }
    internal static string Count(long? value) => value is null ? "尚未更新" : value >= 100_000_000 ? $"{value / 100_000_000d:0.##} 亿" :
        value >= 10_000 ? $"{value / 10_000d:0.##} 万" : value.Value.ToString("N0", CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset? stamp) => stamp?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "暂无";
    private static string Duration(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays}天 {span.Hours}小时" :
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}小时 {span.Minutes}分" : $"{Math.Max(0, span.Minutes)}分 {Math.Max(0, span.Seconds)}秒";
}
