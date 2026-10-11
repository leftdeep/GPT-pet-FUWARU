using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GptSpritePet.Core;

namespace GptSpritePet;

internal static class ResetWatchUi
{
    internal static TextBlock Text(string value, double size = 13, string color = "#36303F")
    { var text = PetTheme.Text(value, size); text.Foreground = PetTheme.Brush(color); return text; }
    internal static Button Button(string text, Action action, bool primary = false)
    {
        var button = PetTheme.Button(text, (_, _) => action());
        button.FontSize = 12; button.Margin = new Thickness(0);
        button.Background = PetTheme.Brush(primary ? "#8063AC" : "#FFFFFF");
        button.Foreground = PetTheme.Brush(primary ? "#FFFFFF" : "#8063AC");
        button.BorderThickness = new Thickness(0); return button;
    }
    internal static void OpenSource(ResetNotice notice)
    {
        // Do not launch arbitrary URIs supplied by a third-party feed/cache.
        var url = ResetFeedParser.TweetUrl(notice.Url);
        if (url is null) return;
        OpenBrowser(url);
    }
    internal static void OpenTiboHome() => OpenBrowser("https://x.com/thsottiaux");
    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
    internal static string Elapsed(ResetNotice? reset, DateTimeOffset now)
    {
        if (reset is null) return "尚无可靠记录";
        if (!reset.ExactTime) return reset.At.ToString("yyyy-MM-dd") + "（日期记录）";
        var duration = now - reset.At;
        if (duration < TimeSpan.Zero) return "公告时间待校验";
        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays} 天 {duration.Hours} 小时"
            : duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟" : $"{Math.Max(0, (int)duration.TotalMinutes)} 分钟";
    }
    internal static string Stamp(DateTimeOffset? at) => at?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未检查";
}

internal sealed class ResetAlertWindow : Window
{
    private readonly TextBlock _title = ResetWatchUi.Text("发现新的重置预告", 18);
    private readonly TextBlock _kind = ResetWatchUi.Text("重置预告", 12, "#A76522");
    private readonly TextBlock _copy = ResetWatchUi.Text("", 13, "#766D83");
    private readonly TextBlock _source = ResetWatchUi.Text("", 11, "#766D83");
    private readonly TextBlock _time = ResetWatchUi.Text("", 11, "#766D83");
    private readonly Button _acknowledge, _post;
    private readonly Polygon _tail;
    private ResetNotice? _notice;
    private IntPtr _handle;
    private double _placedX = double.NaN, _placedY = double.NaN;
    private bool? _onLeft;
    internal event Action? PositionRequested;
    internal ResetNotice? Notice => _notice;

    internal ResetAlertWindow(Action<string> acknowledge)
    {
        Title = "Codex 重置公告"; Width = 360; SizeToContent = SizeToContent.Height;
        AllowsTransparency = true; Background = Brushes.Transparent; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true; PetTheme.Apply(this);
        var root = new Grid();
        _tail = new Polygon { Fill = Brushes.White, Stroke = PetTheme.Brush("#ECE5F4"), StrokeThickness = 1,
            Points = new PointCollection { new(0, 0), new(11, 11), new(0, 22) }, Width = 12, Height = 24,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60, 0, 0) };
        root.Children.Add(_tail);
        var content = new StackPanel();
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var warning = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), Background = PetTheme.Brush("#FFF3DE"), Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock { Text = "!", FontSize = 18, FontWeight = FontWeights.Medium, Foreground = PetTheme.Brush("#A76522"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        heading.Children.Add(warning); _kind.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_kind, 1); heading.Children.Add(_kind);
        _time.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_time, 2); heading.Children.Add(_time); content.Children.Add(heading);
        _title.FontWeight = FontWeights.Medium; content.Children.Add(_title);
        _copy.Margin = new Thickness(0, 9, 0, 14); content.Children.Add(_copy);
        _source.Margin = new Thickness(0, 0, 0, 15); content.Children.Add(_source);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _post = ResetWatchUi.Button("查看原帖", () => { if (_notice is not null) ResetWatchUi.OpenSource(_notice); });
        _post.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(_post);
        _acknowledge = ResetWatchUi.Button("我知道了", () => { if (_notice is not null) acknowledge(_notice.Key); }, true);
        _acknowledge.Name = "ResetAcknowledge";
        Grid.SetColumn(_acknowledge, 1); actions.Children.Add(_acknowledge); content.Children.Add(actions);
        root.Children.Add(new Border { Background = Brushes.White, BorderBrush = PetTheme.Brush("#ECE5F4"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22), Padding = new Thickness(19), Margin = new Thickness(11, 8, 11, 12), Effect = PetTheme.Shadow(), Child = content });
        Content = root;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle; NativeDesktop.ApplyStyles(_handle, false);
            HwndSource.FromHwnd(_handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            { if (message == 0x0021) { handled = true; return new IntPtr(3); } return IntPtr.Zero; });
        };
        SizeChanged += (_, _) => PositionRequested?.Invoke();
    }
    internal void Update(ResetNotice notice, int pendingCount)
    {
        _notice = notice; _kind.Text = notice.Label;
        _title.Text = notice.Kind switch { ResetNoticeKind.Completed => "公开公告称重置已完成",
            ResetNoticeKind.Extra => "发现重置卡或额外额度公告", _ => "发现新的重置预告" };
        _copy.Text = notice.Summary; _time.Text = notice.At.ToLocalTime().ToString("MM-dd HH:mm");
        _source.Text = "Tibo · 数据：" + notice.Source + (pendingCount > 1 ? $" · {pendingCount} 条未读" : "");
        _post.IsEnabled = notice.Url is not null;
    }
    internal void Place(DesktopRect character, DesktopRect area)
    {
        if (_handle == IntPtr.Zero) return;
        var dpi = Math.Max(96, NativeDesktop.GetDpiForWindow(_handle)) / 96d;
        var width = ActualWidth * dpi; var height = ActualHeight * dpi;
        var left = character.X - width - 12 * dpi;
        var onLeft = left >= area.X + 6 * dpi;
        if (!onLeft) left = character.Right + 12 * dpi;
        left = Math.Clamp(left, area.X + 4 * dpi, Math.Max(area.X + 4 * dpi, area.Right - width - 4 * dpi));
        var top = Math.Clamp(character.Y - 24 * dpi, area.Y + 4 * dpi, Math.Max(area.Y + 4 * dpi, area.Bottom - height - 4 * dpi));
        if (_onLeft != onLeft)
        {
            _onLeft = onLeft; _tail.HorizontalAlignment = onLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _tail.Points = onLeft ? new PointCollection { new(0, 0), new(11, 11), new(0, 22) }
                : new PointCollection { new(11, 0), new(0, 11), new(11, 22) };
        }
        if (double.IsNaN(_placedX) || Math.Abs(_placedX - left) >= 1 || Math.Abs(_placedY - top) >= 1)
        { _placedX = left; _placedY = top; NativeDesktop.Move(_handle, left, top); }
    }
}

internal sealed class ResetHistoryPage : UserControl, IDisposable
{
    private readonly ResetWatchService _service;
    private readonly TextBlock _elapsed = ResetWatchUi.Text("", 26, "#8063AC");
    private readonly TextBlock _status = ResetWatchUi.Text("", 12, "#766D83");
    private readonly TextBlock _lastCheck = ResetWatchUi.Text("", 12);
    private readonly TextBlock _nextCheck = ResetWatchUi.Text("", 12);
    private readonly TextBlock _lastReset = ResetWatchUi.Text("", 12);
    private readonly StackPanel _events = new();
    private readonly Button _refresh;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private ResetWatchSnapshot _snapshot = new();
    internal bool CountdownRunning => _timer.IsEnabled;
    internal ResetHistoryPage(ResetWatchService service, Action check)
    {
        _service = service;
        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;
        var summary = new StackPanel();
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new());
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var timing = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        timing.Children.Add(ResetWatchUi.Text("距上次公开重置公告", 12, "#766D83"));
        _elapsed.Name = "ResetElapsed"; _elapsed.FontWeight = FontWeights.SemiBold;
        _elapsed.Margin = new Thickness(0, 6, 0, 0); timing.Children.Add(_elapsed);
        toolbar.Children.Add(timing);
        _refresh = ResetWatchUi.Button("立即检查", check, true);
        _refresh.FontSize = 14; _refresh.FontWeight = FontWeights.SemiBold;
        _refresh.Name = "ResetCheckNow";
        _refresh.Width = 116; _refresh.Height = 44;
        _refresh.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_refresh, 1); toolbar.Children.Add(_refresh); summary.Children.Add(toolbar);

        var statusRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        statusRow.ColumnDefinitions.Add(new());
        statusRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _status.Name = "ResetCheckStatus";
        _status.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(new Border { Child = _status, Background = PetTheme.Brush("#F7F3FC"),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 12, 0) });
        var homepage = ResetWatchUi.Button("打开 Tibo 主页", ResetWatchUi.OpenTiboHome);
        homepage.FontSize = 11; homepage.Width = 116; homepage.Height = 32;
        homepage.VerticalAlignment = VerticalAlignment.Center;
        homepage.ToolTip = "在默认浏览器打开 https://x.com/thsottiaux";
        Grid.SetColumn(homepage, 1); statusRow.Children.Add(homepage); summary.Children.Add(statusRow);
        summary.Children.Add(new Border { Height = 1, Background = PetTheme.Brush("#F0EAF6"), Margin = new Thickness(0, 14, 0, 12) });
        var dates = new Grid();
        foreach (var (label, value) in new[] { ("最近成功检查", _lastCheck), ("下次检查", _nextCheck), ("上次公告", _lastReset) })
        {
            var column = dates.ColumnDefinitions.Count;
            dates.ColumnDefinitions.Add(new());
            var item = new StackPanel { Margin = new Thickness(0, 0, column < 2 ? 12 : 0, 0) };
            item.Children.Add(ResetWatchUi.Text(label, 11, "#94899F"));
            value.Margin = new Thickness(0, 5, 0, 0); value.FontWeight = FontWeights.Medium;
            value.TextWrapping = TextWrapping.NoWrap; value.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Children.Add(value); Grid.SetColumn(item, column); dates.Children.Add(item);
        }
        summary.Children.Add(dates);
        var summaryCard = new Border { Child = summary, Background = Brushes.White,
            Name = "ResetSummaryCard",
            CornerRadius = new CornerRadius(15), BorderBrush = PetTheme.Brush("#ECE5F4"),
            BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(summaryCard, Dock.Top); root.Children.Add(summaryCard);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) }; root.Children.Add(scroll);
        var panel = new StackPanel(); scroll.Content = panel;
        panel.Children.Add(_events);
        var attribution = ResetWatchUi.Text("数据：codex-reset.com / codexreset.today（备用）\n这是公共公告记录，不代表本账号额度已重置。", 11, "#766D83");
        attribution.Margin = new Thickness(0, 16, 0, 0); panel.Children.Add(attribution);
        _timer.Tick += (_, _) => _elapsed.Text = ResetWatchUi.Elapsed(_snapshot.LastReset, DateTimeOffset.Now);
        Loaded += (_, _) => { Update(_service.Snapshot); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        Update(service.Snapshot);
    }
    public void Dispose() => _timer.Stop();
    internal void Update(ResetWatchSnapshot snapshot)
    {
        _snapshot = snapshot; _elapsed.Text = ResetWatchUi.Elapsed(snapshot.LastReset, DateTimeOffset.Now);
        _status.Text = snapshot.Message;
        if (snapshot.LastSuccessfulCheckAt is { } checkedAt && DateTimeOffset.Now - checkedAt > TimeSpan.FromHours(2))
            _status.Text += " · 检查结果较旧";
        _lastCheck.Text = ResetWatchUi.Stamp(snapshot.LastSuccessfulCheckAt);
        _nextCheck.Text = !snapshot.Enabled ? "已暂停" : snapshot.NextCheckAt is null ? "尚未安排" : ResetWatchUi.Stamp(snapshot.NextCheckAt);
        _lastReset.Text = snapshot.LastReset is { } last ? last.ExactTime ? ResetWatchUi.Stamp(last.At) : last.At.ToString("yyyy-MM-dd") : "暂无记录";
        _lastCheck.ToolTip = snapshot.LastSuccessfulCheckAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        _nextCheck.ToolTip = snapshot.Enabled ? snapshot.NextCheckAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "公告监测已暂停";
        _lastReset.ToolTip = snapshot.LastReset is { } previous ? previous.ExactTime ? previous.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : previous.At.ToString("yyyy-MM-dd") : "暂无可靠的公开重置公告";
        _refresh.Content = snapshot.Busy ? "正在检查…" : "立即检查";
        _refresh.IsEnabled = snapshot.Enabled && !snapshot.Busy;
        _events.Children.Clear();
        if (snapshot.Events.Length == 0) _events.Children.Add(ResetWatchUi.Text("首次检查后会显示最近的公开公告。", 13, "#766D83"));
        foreach (var notice in snapshot.Events)
        {
            var row = new StackPanel();
            row.Children.Add(ResetWatchUi.Text(notice.Label, 13, notice.Kind == ResetNoticeKind.Preview ? "#A76522" : "#8063AC"));
            var copy = ResetWatchUi.Text(notice.Summary, 12); copy.Margin = new Thickness(0, 6, 0, 7); row.Children.Add(copy);
            row.Children.Add(ResetWatchUi.Text((notice.ExactTime ? notice.At.ToLocalTime().ToString("MM-dd HH:mm") : notice.At.ToString("yyyy-MM-dd")) + " · " + notice.Source, 11, "#766D83"));
            if (notice.Url is not null) { var link = ResetWatchUi.Button("查看原帖", () => ResetWatchUi.OpenSource(notice)); link.HorizontalAlignment = HorizontalAlignment.Left; link.Margin = new Thickness(-12, 4, 0, 0); row.Children.Add(link); }
            _events.Children.Add(new Border { Child = row, Background = Brushes.White, CornerRadius = new CornerRadius(14),
                BorderBrush = PetTheme.Brush("#ECE5F4"), BorderThickness = new Thickness(1), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) });
        }
    }
}
