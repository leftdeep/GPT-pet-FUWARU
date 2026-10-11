using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunStructureChecks(Dictionary<string, bool> checks)
    {
        RunInteractionChecks(checks);
        System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
        {
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
        var commands = CreateMenuCommands(); var wpf = CreateContextMenu();
        checks["shared_menu_wpf_top_level"] = wpf.Items.OfType<MenuItem>().Select(i => (string)i.Header)
            .SequenceEqual(commands.Where(c => c.Label is not null).Select(c => c.Label));
        var trayMenu = (Forms.ContextMenuStrip)typeof(TrayController).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_tray)!;
        typeof(Forms.ContextMenuStrip).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(trayMenu, [new CancelEventArgs()]);
        bool Same(PetMenuItem command, Forms.ToolStripItem rendered)
        {
            if (command.Label is null) return rendered is Forms.ToolStripSeparator;
            if (rendered is not Forms.ToolStripMenuItem item || item.Text != command.Label || item.Enabled != command.Enabled
                || item.Checked != (command.Checked ?? false)) return false;
            return command.Children is not { } children || children.Length == item.DropDownItems.Count &&
                children.Select((child, index) => Same(child, item.DropDownItems[index])).All(ok => ok);
        }
        checks["shared_menu_tray_commands_states_children"] = trayMenu.Items.Count == commands.Length + 2 &&
            commands.Select((command, index) => Same(command, trayMenu.Items[index + 2])).All(ok => ok);
        checks["version_from_assembly_metadata"] = Title == "芙哇噜 " + ProjectPaths.Version && ProjectPaths.Version != "未知";
        var labels = commands.Select(c => c.Label).ToArray();
        checks["menu_removed_redundant_controls"] = !labels.Any(label => new[] { "Token 气泡大小", "自动走动", "立即检查重置公告", "重置公告监测" }.Contains(label));
        var previewIndex = Array.IndexOf(labels, "动作预览");
        checks["menu_control_tabs_after_preview_in_order"] = labels.Skip(previewIndex + 1).Take(6)
            .SequenceEqual(new string?[] { null, "设置…", "账号用量…", "重置历史…", "成长详情…", null });
        GptSpritePet.Core.PetSettings? applied = null;
        var settingsWindow = new SettingsPage(() => Settings with { Paused = true }, _sprite.SkinName, _store.DirectoryPath, value => applied = value);
        var sizeControl = (ComboBox)typeof(SettingsPage).GetField("_bubbleSize", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settingsWindow)!;
        sizeControl.SelectedIndex = 3;
        var buttons = Descendants(settingsWindow).OfType<Button>().ToArray();
        checks["settings_pause_control_removed"] = !Descendants(settingsWindow).OfType<CheckBox>().Any(box => (string)box.Content == "暂停动作");
        buttons.First(button => (string)button.Content == "应用设置").RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        checks["settings_bubble_size_saves_preserving_pause"] = applied is { UsageBubbleScale: 1.75, Paused: true };
        RunControlWindowChecks(checks);
        var production = Assembly.LoadFile(System.IO.Path.Combine(ProjectPaths.Root, "src", "GptSpritePet", "bin", "Release", "net10.0-windows", "GptSpritePet.dll"));
        checks["production_excludes_smoke_checks"] = production.GetType("GptSpritePet.PetWindow")!
            .GetMethod("RunSmokeTest", BindingFlags.Instance | BindingFlags.NonPublic) is null;
        var original = Settings; ApplySettings(original with { AutoWalk = false, Paused = true });
        var before = NativeDesktop.WindowRect(_handle); SaveNow();
        var saved = _store.Load();
        checks["physical_position_saved_for_next_launch"] = saved.X == before.X && saved.Y == before.Y;
        ApplySettings(original); _tick.Stop();
        var statusFile = System.IO.Path.Combine(_store.DirectoryPath, "runtime-status.json");
        WriteRuntimeStatus();
        using var status = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(statusFile));
        checks["runtime_status_atomic_complete_json"] = status.RootElement.GetProperty("version").GetString() == ProjectPaths.Version &&
            !System.IO.File.Exists(statusFile + ".tmp");
    }
}
