using System;
using System.Windows;
using System.Windows.Controls;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private ScreenEdge _peekEdge;

    private bool EnterEdgePeek(ScreenEdge edge, DesktopRect area)
    {
        if (edge == ScreenEdge.None) return false;
        var before = CharacterScreenBounds();
        using var presentation = _sprite.HoldPlacementPresentation();
        if (!_sprite.SetEdgePeek(edge)) return false;
        _peekEdge = edge; _settings = _settings with { EdgePeek = edge };
        _clickTimer.Stop(); _pendingPreviewKey = null; StopPreview();
        SetUsageHover(false);
        ApplyView(); UpdateLayout();
        AttachPeek(area, before.X + before.Width / 2, before.Y + before.Height / 2);
        UpdateTimer(); ScheduleSave();
        return true;
    }

    private void AttachPeek(DesktopRect area, double centerX, double centerY)
    {
        if (_handle == IntPtr.Zero || _peekEdge == ScreenEdge.None) return;
        var target = EdgePeekPlacement.Attach(NativeDesktop.WindowRect(_handle), LocalCharacterBounds(), area, _peekEdge, centerX, centerY);
        NativeDesktop.Move(_handle, target.X, target.Y);
        _walkingPhysicalX = null; PositionHeadBubbles();
    }

    private void LeaveEdgePeek(Point? oldGrab = null, Point? newGrab = null)
    {
        if (_peekEdge == ScreenEdge.None) return;
        var before = CharacterScreenBounds(); var area = CharacterWorkArea();
        using var presentation = _sprite.HoldPlacementPresentation();
        _peekEdge = ScreenEdge.None; _settings = _settings with { EdgePeek = ScreenEdge.None };
        _sprite.SetEdgePeek(ScreenEdge.None); _behavior.Wake();
        ApplyView(); UpdateLayout();
        var visible = LocalCharacterBounds();
        var centerX = before.X + before.Width / 2; var centerY = before.Y + before.Height / 2;
        if (oldGrab is { } grab && newGrab is { } target)
        {
            var fractionX = Math.Clamp((grab.X - before.X) / before.Width, 0, 1);
            var fractionY = Math.Clamp((grab.Y - before.Y) / before.Height, 0, 1);
            centerX = target.X + visible.Width * (.5 - fractionX);
            centerY = target.Y + visible.Height * (.5 - fractionY);
        }
        MoveClamped(centerX - visible.X - visible.Width / 2, centerY - visible.Y - visible.Height / 2, area);
        UpdateTimer(); ScheduleSave();
    }

    private void PositionPeekBubble(FrameworkElement bubble, double width, double height)
    {
        var character = _sprite.VisibleBounds;
        character.Offset(Canvas.GetLeft(_sprite), Canvas.GetTop(_sprite));
        var x = character.X + (character.Width - width) / 2;
        var y = character.Y - height - 12;
        switch (_peekEdge)
        {
            case ScreenEdge.Left: x = character.Right + 12; y = character.Y + (character.Height - height) / 2; break;
            case ScreenEdge.Right: x = character.Left - width - 12; y = character.Y + (character.Height - height) / 2; break;
            case ScreenEdge.Top: y = character.Bottom + 12; break;
        }
        Canvas.SetLeft(bubble, x); Canvas.SetTop(bubble, y);
    }
}
