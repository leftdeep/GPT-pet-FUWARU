using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GptSpritePet.Core;
using Microsoft.Win32;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    internal PetMenuItem[] CreateMenuCommands()
    {
        RefreshAnimationAssets();
        var groups = Enum.GetValues<AnimationPreviewGroup>().Select(group =>
            new PetMenuItem(PreviewGroupLabel(group), Tag: group, Children: _sprite.AvailableActions
                .Where(action => action.Group == group).Select(action =>
                    new PetMenuItem(action.Label, () => PreviewAnimation(action.Key), Tag: action.Key)).ToArray()))
            .Where(group => group.Children!.Length > 0).ToArray();
        return [
            new("置顶显示", ToggleTopmost, _settings.Topmost, Icon: "\uE718"),
            new("暂停动作", TogglePause, _settings.Paused, Icon: "\uE769"),
            new("鼠标穿透", ToggleClickThrough, _settings.ClickThrough, Icon: "\uE962",
                Tip: "鼠标完全穿透人物；双击托盘图标可恢复互动。"),
            new("动作预览", Enabled: groups.Length > 0, Icon: "\uE714", Children: groups),
            PetMenuItem.Separator,
            new("设置…", OpenSettings, Icon: "\uE713"),
            new("账号用量…", OpenUsage, Icon: "\uE9D9"),
            new("重置历史…", OpenResetHistory, Icon: "\uE81C"),
            new("成长详情…", OpenGrowth, Icon: "\uE734"),
            PetMenuItem.Separator,
            new("重置位置", ResetPosition, Icon: "\uE81D"),
            new("隐藏到托盘", HidePet, Icon: "\uE8A7"),
            new("退出", ExitAfterMenuClose, Icon: "\uE7E8")
        ];
    }

    private ContextMenu CreateContextMenu()
    {
        var commands = CreateMenuCommands();
        var menu = new ContextMenu { FontFamily = new FontFamily("Microsoft YaHei UI"), MinWidth = 246 };
        menu.Opened += (_, _) => RegisterPetInteraction();
        menu.PreviewMouseDown += (_, _) => RegisterPetInteraction();
        menu.PreviewKeyDown += (_, _) => RegisterPetInteraction();
        _ = new MenuAutoDismiss(menu); PetTheme.Apply(menu);
        object Render(PetMenuItem command)
        {
            if (command.Label is null) return new Separator();
            var item = new MenuItem { Header = command.Label, IsCheckable = command.Checked.HasValue,
                IsChecked = command.Checked ?? false, IsEnabled = command.Enabled, Tag = command.Tag,
                Icon = command.Icon is null ? null : PetTheme.MenuIcon(command.Icon), ToolTip = command.Tip };
            if (command.Children is { } children) foreach (var child in children) item.Items.Add(Render(child));
            if (command.Execute is { } execute) item.Click += (_, e) => { RegisterPetInteraction(); menu.IsOpen = false; execute(); e.Handled = true; };
            return item;
        }
        foreach (var command in commands) menu.Items.Add(Render(command));
        return menu;
    }

    private void ExitAfterMenuClose()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); ExitPet(); }; timer.Start();
    }

    internal void ToggleTopmost() => ApplySettings(_settings with { Topmost = !_settings.Topmost });
    private void ResizeUsageBubble(double delta) => ApplySettings(_settings with { UsageBubbleScale = _settings.UsageBubbleScale + delta });
    internal void ToggleAutoWalk()
    {
        ApplySettings(_settings with { AutoWalk = !_settings.AutoWalk });
        if (_settings.AutoWalk && _peekEdge == ScreenEdge.None) _behavior.React(PetState.Walking, 5);
    }
    internal void TogglePause() => ApplySettings(_settings with { Paused = !_settings.Paused });
    internal void ToggleClickThrough()
    {
        ApplySettings(_settings with { ClickThrough = !_settings.ClickThrough });
    }
    private void PreviewAnimation(string key)
    {
        if (!_sprite.AvailableActions.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) return;
        LeaveEdgePeek();
        if (key.Equals(nameof(PetState.Sleeping), StringComparison.OrdinalIgnoreCase)
            && _sprite.HasAnimation(PetState.LyingDown) && _sprite.HasAnimation(PetState.Waking))
        {
            _pendingPreviewKey = null;
            if (!IsVisible) RestoreFromTray();
            StopPreview();
            _behavior.React(PetState.Sleeping);
            UpdateTimer();
            return;
        }
        RegisterPetInteraction();
        if (!_behavior.Paused && _behavior.State is (PetState.LyingDown or PetState.Sleeping or PetState.Waking))
        { _pendingPreviewKey = key; return; }
        _pendingPreviewKey = null;
        if (!IsVisible) RestoreFromTray();
        var previewCharacter = CharacterScreenBounds(); var previewArea = CharacterWorkArea();
        _previewTimer.Stop(); _walkingPreview = null;
        var previewDirection = _sprite.Direction;
        if (key.Equals(nameof(PetState.Walking), StringComparison.OrdinalIgnoreCase) && _sprite.WalkingTiming is { } timing)
        {
            _walkingPreview = new(timing); _walkingPreview.Begin();
            var leftSpace = previewCharacter.X - previewArea.X; var rightSpace = previewArea.Right - previewCharacter.Right;
            // Start inward when the selected direction would immediately run into the edge.
            if (previewDirection < 0 && leftSpace < 32 * DpiScale && rightSpace > leftSpace) previewDirection = 1;
            else if (previewDirection > 0 && rightSpace < 32 * DpiScale && leftSpace > rightSpace) previewDirection = -1;
        }
        _sprite.BeginPreview(key, _walkingPreview, previewDirection);
        _previewTimer.Interval = TimeSpan.FromSeconds(Math.Max(.1, _walkingPreview?.FinishSeconds ?? _sprite.PreviewSeconds(key)));
        _previewTimer.Start(); _tick.Interval = TimeSpan.FromMilliseconds(1000d / 24); UpdateTimer();
    }
    private void StopPreview()
    {
        _previewTimer.Stop();
        if (_sprite.PreviewKey is null) return;
        _sprite.EndPreview(_behavior.State, _behavior.WalkingPlayback, _behavior.Direction); _walkingPreview = null;
        UpdateTimer();
        _walkingPhysicalX = null; ScheduleSave();
    }
    internal void RefreshAnimationAssets()
    {
        if (File.GetLastWriteTimeUtc(SkinManifest) == _skinManifestStamp) return;
        _skinInitialized = false; ApplySettings(_settings);
    }
    private static string PreviewGroupLabel(AnimationPreviewGroup group) => group switch
    { AnimationPreviewGroup.Idle => "待机", AnimationPreviewGroup.Walking => "移动", _ => "点击互动" };

}
