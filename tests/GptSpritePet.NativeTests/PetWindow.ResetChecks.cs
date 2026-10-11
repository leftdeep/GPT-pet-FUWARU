using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunResetUiChecks(Dictionary<string, bool> checks)
    {
        var original = _settings;
        var now = DateTimeOffset.Now;
        var preview = new ResetNotice("10000000001", now.AddMinutes(-3), ResetNoticeKind.Preview,
            "Tibo 公告提及后续重置安排。具体时间与适用范围请查看原帖。", "https://x.com/thsottiaux/status/10000000001", "codex-reset.com");
        var completed = preview with { Id = "10000000000", At = now.AddDays(-2).AddHours(-6), Kind = ResetNoticeKind.Completed,
            Summary = "公开公告称额度重置已完成；本账号余额仍以用量页面为准。", Url = "https://x.com/thsottiaux/status/10000000000" };
        var cache = new ResetWatchCache { Initialized = true, LastAttemptAt = now, LastSuccessfulCheckAt = now,
            NextCheckAt = now.AddHours(1), LastReset = completed, Events = [preview, completed], Pending = [preview], Seen = [preview.Key, completed.Key] };
        var cacheDir = Path.Combine(_store.DirectoryPath, "reset-watch"); Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "reset-watch.json"), JsonSerializer.Serialize(cache));
        var snapshot = new ResetWatchSnapshot { Enabled = true, IsOwner = true, Initialized = true, LastAttemptAt = now,
            LastSuccessfulCheckAt = now, NextCheckAt = now.AddHours(1), LastReset = completed, Events = [preview, completed], Pending = [preview], Message = "示例记录 · 原生界面验证" };
        UpdateReset(snapshot);
        var alert = _resetAlert!; alert.UpdateLayout(); PositionResetAlert();
        PumpResetUi();
        checks["reset_alert_shown"] = alert.IsVisible && alert.Notice?.Key == preview.Key;
        alert.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        checks["reset_alert_survives_mouse_leave"] = alert.IsVisible;
        checks["reset_preview_does_not_change_clock"] = ResetWatchUi.Elapsed(snapshot.LastReset, now).StartsWith("2 天 6 小时");
        IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
        var button = Descendants(alert).OfType<Button>().Single(b => (b.Content as string) == "我知道了");
        var handle = new WindowInteropHelper(alert).Handle;
        foreach (var x in new[] { CharacterWorkArea().X + 5, CharacterWorkArea().Right - LocalCharacterBounds().Width - 5 })
        {
            MoveClamped(x - LocalCharacterBounds().X, NativeDesktop.WindowRect(_handle).Y, CharacterWorkArea());
            PositionResetAlert(); PumpResetUi();
            var rect = NativeDesktop.WindowRect(handle); var area = CharacterWorkArea();
            checks[x < area.X + 10 ? "reset_alert_fits_left_edge" : "reset_alert_fits_right_edge"] = rect.X >= area.X && rect.Right <= area.Right && rect.Y >= area.Y && rect.Bottom <= area.Bottom;
        }
        ApplySettings(original with { ClickThrough = true }); _tick.Stop(); PositionResetAlert(); PumpResetUi();
        var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        checks["reset_alert_clickable_when_pet_passes_through"] = NativeDesktop.WindowFromPoint(new NativeDesktop.Point { X = (int)point.X, Y = (int)point.Y }) == handle;
        SaveBitmap(RenderElement(alert), Path.Combine(_store.DirectoryPath, "reset-alert-preview.png"));
        var manualChecks = 0;
        var historyPage = new ResetHistoryPage(_resetService, () => manualChecks++);
        var history = CreateControlPreview(PetControlTab.ResetHistory, historyPage);
        history.Show(); historyPage.Update(snapshot); history.UpdateLayout();
        checks["reset_history_has_elapsed_and_source"] = Descendants(history).OfType<TextBlock>().Any(t => t.Text == "2 天 6 小时")
            && Descendants(history).OfType<TextBlock>().Any(t => t.Text.Contains("数据：codex-reset.com"));
        var refresh = Descendants(historyPage).OfType<Button>().Single(b => b.Name == "ResetCheckNow");
        var elapsed = Descendants(historyPage).OfType<TextBlock>().Single(t => t.Name == "ResetElapsed");
        var summary = Descendants(historyPage).OfType<Border>().Single(b => b.Name == "ResetSummaryCard");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(historyPage).TransformBounds(new Rect(element.RenderSize));
        foreach (var width in new[] { 520, 560, 720 })
        {
            history.Width = width; history.UpdateLayout();
            checks["reset_summary_no_overlap_" + width] = Bounds(elapsed).Right + 8 <= Bounds(refresh).Left && refresh.ActualWidth >= 110
                && Bounds(refresh).Right <= historyPage.ActualWidth - 22;
            var values = new[] { "最近成功检查", "下次检查", "上次公告" }.Select(label =>
                (TextBlock)((StackPanel)Descendants(historyPage).OfType<TextBlock>().Single(t => t.Text == label).Parent).Children[1]).ToArray();
            checks["reset_summary_dates_aligned_" + width] = values.All(value => Math.Abs(Bounds(value).Top - Bounds(values[0]).Top) < 1)
                && Bounds(values[0]).Right <= Bounds(values[1]).Left && Bounds(values[1]).Right <= Bounds(values[2]).Left
                && values.All(value => value.DesiredSize.Width <= value.ActualWidth + 1);
        }
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["reset_manual_check_still_invokes_action"] = manualChecks == 1;
        historyPage.Update(snapshot with { Busy = true }); history.UpdateLayout();
        checks["reset_busy_check_has_clear_disabled_state"] = !refresh.IsEnabled && (string)refresh.Content == "正在检查…";
        historyPage.Update(snapshot with { Enabled = false });
        checks["reset_paused_check_disabled"] = !refresh.IsEnabled && Descendants(historyPage).OfType<TextBlock>().Any(t => t.Text == "已暂停");
        history.Width = 520; history.Height = 560;
        historyPage.Update(snapshot with { Events = Enumerable.Range(0, 20).Select(i => preview with { Id = (10000001000L + i).ToString() }).ToArray() });
        history.UpdateLayout();
        var headerTop = Bounds(summary).Top;
        var buttonTop = Bounds(refresh).Top;
        var scroll = Descendants(historyPage).OfType<ScrollViewer>().First(viewer => viewer.Content is StackPanel);
        scroll.ScrollToEnd(); history.UpdateLayout();
        checks["reset_summary_and_check_remain_visible_while_scrolling"] = scroll.VerticalOffset > 0
            && Math.Abs(Bounds(summary).Top - headerTop) < 1 && Math.Abs(Bounds(refresh).Top - buttonTop) < 1
            && Bounds(summary).Bottom <= Bounds(scroll).Top;
        historyPage.Update(snapshot with { Message = "暂时无法获取最新公告，请稍后重试。当前显示最近成功检查的公开记录，账号额度仍以账号用量页面为准。" });
        history.UpdateLayout();
        checks["reset_long_status_wraps_within_summary"] = Descendants(historyPage).OfType<TextBlock>().Where(t => t.Name == "ResetCheckStatus")
            .All(t => Bounds(t).Right <= Bounds(summary).Right && Bounds(t).Bottom <= Bounds(summary).Bottom) && scroll.ViewportHeight > 60;
        history.Width = 560; history.Height = 800;
        historyPage.Update(snapshot with { Message = "已检查，等待新公告" }); history.UpdateLayout();
        var historyBitmap = RenderElement(history);
        SaveBitmap(historyBitmap, Path.Combine(_store.DirectoryPath, "reset-history-preview.png"));
        var cardBounds = summary.TransformToAncestor(history).TransformBounds(new Rect(summary.RenderSize));
        SaveBitmap(new System.Windows.Media.Imaging.CroppedBitmap(historyBitmap, new Int32Rect((int)cardBounds.X, (int)cardBounds.Y,
            (int)Math.Ceiling(cardBounds.Width), (int)Math.Ceiling(cardBounds.Height))), Path.Combine(_store.DirectoryPath, "reset-summary-preview.png"));
        history.Close();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpResetUi();
        checks["reset_acknowledge_closes_alert"] = _resetAlert is null;
        var saved = JsonSerializer.Deserialize<ResetWatchCache>(File.ReadAllText(Path.Combine(cacheDir, "reset-watch.json")));
        checks["reset_acknowledge_saved"] = saved?.Pending.Count == 0;
        var card = preview with { Id = "10000000002", Kind = ResetNoticeKind.Extra,
            Summary = "Tibo 公告涉及赠送重置卡或额外额度。领取和使用方式请查看原帖。",
            Url = "https://x.com/thsottiaux/status/10000000002" };
        cache.Pending = [card]; cache.Events = [card, completed]; cache.Seen = ["alert:" + card.Key, completed.Key];
        File.WriteAllText(Path.Combine(cacheDir, "reset-watch.json"), JsonSerializer.Serialize(cache));
        UpdateReset(snapshot with { Pending = [card], Events = [card, completed] }); PumpResetUi();
        var cardAlert = _resetAlert!;
        checks["reset_card_alert_shown_with_correct_title"] = cardAlert.IsVisible && cardAlert.Notice?.Key == card.Key
            && Descendants(cardAlert).OfType<TextBlock>().Any(text => text.Text == "发现重置卡或额外额度公告")
            && !Descendants(cardAlert).OfType<TextBlock>().Any(text => text.Text == "公开公告称重置已完成");
        cardAlert.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        checks["reset_card_alert_survives_mouse_leave"] = cardAlert.IsVisible;
        SaveBitmap(RenderElement(cardAlert), Path.Combine(_store.DirectoryPath, "reset-card-alert-preview.png"));
        Descendants(cardAlert).OfType<Button>().Single(b => (b.Content as string) == "我知道了")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpResetUi();
        var savedCard = JsonSerializer.Deserialize<ResetWatchCache>(File.ReadAllText(Path.Combine(cacheDir, "reset-watch.json")));
        checks["reset_card_acknowledge_closes_and_saves"] = _resetAlert is null && savedCard?.Pending.Count == 0;
        ApplySettings(original); _tick.Stop();
        var menu = CreateContextMenu();
        checks["reset_menu_entries_present"] = menu.Items.OfType<MenuItem>().Any(item => (string)item.Header == "重置历史…")
            && !menu.Items.OfType<MenuItem>().Any(item => new[] { "立即检查重置公告", "重置公告监测" }.Contains((string)item.Header));
    }
    private static void PumpResetUi()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
