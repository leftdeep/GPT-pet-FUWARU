using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunInteractionChecks(Dictionary<string, bool> checks)
    {
        checks["pet_speech_bubble_removed"] = _canvas.Children.Count == 2 &&
            _canvas.Children.Contains(_sprite) && _canvas.Children.Contains(_usageBadge);
        for (var count = 1; count <= 2; count++)
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(down, count);
            PointerDown(_sprite, down);
            PointerUp(_sprite, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }
        checks["pet_double_click_keeps_controls_closed"] = _controlWindow is null && _clickTimer.IsEnabled;
        _clickTimer.Stop();

        string? entry = null;
        var writes = 0;
        var startup = new WindowsStartup(ProjectPaths.Root, () => entry, value => { entry = value; writes++; });
        checks["startup_default_disabled"] = !startup.Enabled;
        checks["startup_uses_stable_quoted_hidden_launcher"] = startup.Command.Contains("-WindowStyle Hidden -File \"") &&
            startup.Command.EndsWith("scripts\\launch-dragon-girl.ps1\"") && !startup.Command.Contains("lib\\");
        checks["startup_registration_is_project_specific"] = startup.ValueName !=
            new WindowsStartup(Path.Combine(ProjectPaths.Root, "other-project"), () => null, _ => { }).ValueName;
        GptSpritePet.Core.PetSettings? applied = null;
        var page = new SettingsPage(() => Settings, SkinName, _store.DirectoryPath, value => applied = value, startup: startup);
        var box = ControlDescendants(page).OfType<CheckBox>().Single(x => ((string)x.Content).StartsWith("开机自启动"));
        var apply = ControlDescendants(page).OfType<Button>().Single(x => (string)x.Content == "应用设置");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["settings_unrelated_apply_does_not_register_startup"] = writes == 0;
        box.IsChecked = true; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["settings_startup_enable_applied"] = startup.Enabled && entry == startup.Command && writes == 1 && applied is not null;
        var reopened = new SettingsPage(() => Settings, SkinName, _store.DirectoryPath, _ => { }, startup: startup);
        checks["settings_startup_reads_windows_registration"] = ControlDescendants(reopened).OfType<CheckBox>()
            .Single(x => ((string)x.Content).StartsWith("开机自启动")).IsChecked == true;
        box.IsChecked = false; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["settings_startup_disable_removes_own_entry"] = entry is null && writes == 2;
        entry = "outdated command"; startup.SetEnabled(true);
        checks["startup_repairs_own_stale_command"] = entry == startup.Command;
        applied = null;
        var failing = new WindowsStartup(ProjectPaths.Root, () => null, _ => throw new UnauthorizedAccessException("test denied"));
        var denied = new SettingsPage(() => Settings, SkinName, _store.DirectoryPath, value => applied = value, startup: failing);
        ControlDescendants(denied).OfType<CheckBox>().Single(x => ((string)x.Content).StartsWith("开机自启动")).IsChecked = true;
        ControlDescendants(denied).OfType<Button>().Single(x => (string)x.Content == "应用设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["startup_failure_does_not_claim_saved"] = applied is null && ControlDescendants(denied).OfType<TextBlock>()
            .Any(x => x.Text.StartsWith("自启动设置未保存"));
        checks["settings_removed_speech_notification_options"] = !ControlDescendants(page).OfType<CheckBox>()
            .Any(x => (string)x.Content is "低额度时提醒一次" or "升级时轻量提醒");
    }
}
