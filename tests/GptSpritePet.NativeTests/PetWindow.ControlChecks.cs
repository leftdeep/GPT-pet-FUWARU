using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private PetControlWindow CreateControlPreview(PetControlTab tab, UserControl page) =>
        new(selected => selected == tab ? page : CreateControlPage(selected), _ => { }, tab)
        { ShowActivated = false, ShowInTaskbar = false };

    private void RunControlWindowChecks(Dictionary<string, bool> checks)
    {
        var original = _settings;
        OpenSettings(); var window = _controlWindow!; window.UpdateLayout(); PumpResetUi();
        var handle = new WindowInteropHelper(window).Handle;
        checks["control_window_creates_only_requested_page"] = window.CreatedPageCount == 1;
        var page = window.Page<SettingsPage>(PetControlTab.Settings)!;
        checks["control_settings_body_uses_default_cursor"] = page.Cursor != Cursors.Hand
            && ((FrameworkElement)page.Content).Cursor != Cursors.Hand
            && ControlDescendants(page).OfType<TextBlock>().All(text => text.Cursor != Cursors.Hand);
        checks["control_settings_clickable_controls_use_hand"] = ControlDescendants(page).OfType<Button>().All(button => button.Cursor == Cursors.Hand)
            && ControlDescendants(page).OfType<CheckBox>().All(box => box.Cursor == Cursors.Hand)
            && ControlDescendants(page).OfType<ComboBox>().All(combo => combo.Cursor == Cursors.Hand);
        checks["control_only_tab_headers_use_hand"] = ControlDescendants(window).OfType<TabItem>().All(tab => tab.Cursor != Cursors.Hand
            && tab.Template.FindName("Face", tab) is FrameworkElement header && header.Cursor == Cursors.Hand);
        var size = (ComboBox)typeof(SettingsPage).GetField("_bubbleSize", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        size.SelectedIndex = 3;
        foreach (var pair in new[] { ("账号用量…", PetControlTab.Usage), ("重置历史…", PetControlTab.ResetHistory),
                     ("成长详情…", PetControlTab.Growth), ("设置…", PetControlTab.Settings) })
        {
            CreateMenuCommands().Single(command => command.Label == pair.Item1).Execute!();
            window.UpdateLayout(); PumpResetUi();
            checks["control_window_routes_" + pair.Item2] = ReferenceEquals(window, _controlWindow) &&
                new WindowInteropHelper(_controlWindow!).Handle == handle && window.SelectedTab == pair.Item2;
            checks["control_window_single_native_window_" + pair.Item2] = Application.Current.Windows.OfType<PetControlWindow>().Count() == 1;
            checks["control_page_default_cursor_" + pair.Item2] = window.Page<UserControl>(pair.Item2)?.Cursor != Cursors.Hand;
        }
        var usage = window.Page<UsagePage>(PetControlTab.Usage)!;
        var growth = window.Page<GrowthPage>(PetControlTab.Growth)!;
        var history = window.Page<ResetHistoryPage>(PetControlTab.ResetHistory)!;
        checks["control_window_retains_settings_draft"] = ReferenceEquals(page, window.Page<SettingsPage>(PetControlTab.Settings)) && size.SelectedIndex == 3;
        checks["control_window_inactive_pages_stop_countdowns"] = !usage.CountdownRunning && !growth.CountdownRunning && !history.CountdownRunning;
        checks["control_usage_tab_enables_account_connection"] = _settings.UsageEnabled;
        window.SelectTab(PetControlTab.Usage); window.UpdateLayout(); PumpResetUi();
        checks["control_usage_countdown_resumes"] = usage.CountdownRunning && !growth.CountdownRunning && !history.CountdownRunning;
        var snapshot = _usageService.Snapshot with { Message = "标签页实时更新验证", State = UsageConnectionState.SignedOut };
        UpdateUsage(snapshot);
        checks["control_usage_receives_live_updates"] = ControlDescendants(usage).OfType<TextBlock>().Any(t => t.Text.Contains(snapshot.Message));
        window.SelectTab(PetControlTab.Growth); window.UpdateLayout(); PumpResetUi();
        checks["control_growth_countdown_resumes"] = growth.CountdownRunning && !usage.CountdownRunning && !history.CountdownRunning;
        window.SelectTab(PetControlTab.ResetHistory); window.UpdateLayout(); PumpResetUi();
        checks["control_history_countdown_resumes"] = history.CountdownRunning && !usage.CountdownRunning && !growth.CountdownRunning;
        window.SelectTab(PetControlTab.Settings); window.UpdateLayout(); PumpResetUi();
        ApplySettings(_settings with { Paused = true }); _tick.Stop();
        ControlDescendants(page).OfType<Button>().Single(b => (string)b.Content == "应用设置")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["control_apply_preserves_latest_pause_and_window"] = _settings.Paused && _settings.UsageBubbleScale == 1.75 && ReferenceEquals(window, _controlWindow) && window.IsVisible;
        window.WindowState = WindowState.Minimized; OpenGrowth();
        checks["control_menu_restores_minimized_window"] = window.WindowState == WindowState.Normal && window.SelectedTab == PetControlTab.Growth;
        window.Close();
        checks["control_close_releases_window_and_timers"] = _controlWindow is null && !usage.CountdownRunning && !growth.CountdownRunning && !history.CountdownRunning;
        OpenResetHistory();
        checks["control_reopen_opens_requested_tab_lazily"] = _controlWindow is { SelectedTab: PetControlTab.ResetHistory, CreatedPageCount: 1 } && !ReferenceEquals(window, _controlWindow);
        _controlWindow!.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_controlWindow), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        checks["control_escape_closes_window"] = _controlWindow is null;
        ApplySettings(original); _tick.Stop();
    }

    private static IEnumerable<DependencyObject> ControlDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { yield return child; foreach (var nested in ControlDescendants(child)) yield return nested; }
    }
}
