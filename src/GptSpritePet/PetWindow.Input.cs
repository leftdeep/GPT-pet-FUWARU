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
    internal void RegisterPetInteraction() => _behavior.NotifyInteraction();

    private void TrackWindowInteraction(Window window)
    {
        window.PreviewMouseDown += (_, _) => RegisterPetInteraction();
        window.PreviewMouseWheel += (_, _) => RegisterPetInteraction();
        window.PreviewKeyDown += (_, _) => RegisterPetInteraction();
    }

    private void UpdateUsageHover(Point screen, double now, bool overOurWindow)
    {
        if (!IsVisible || !_settings.UsageCardVisible || _settings.ClickThrough || _dragging)
        { SetUsageHover(false); return; }
        var characterHit = overOurWindow && _sprite.IsOpaqueHit(_sprite.PointFromScreen(screen));
        var cardPoint = _usageBadge.PointFromScreen(screen);
        var cardHit = overOurWindow && _usageHoverVisible && new Rect(0, 0, _usageBadge.ActualWidth, _usageBadge.ActualHeight).Contains(cardPoint);
        if (characterHit || cardHit) { _lastUsageHover = now; SetUsageHover(true); }
        else if (now - _lastUsageHover >= 0.35) SetUsageHover(false);
    }

    private void SetUsageHover(bool visible)
    {
        if (_usageHoverVisible == visible) return;
        _usageHoverVisible = visible;
        _usageHeadTop = null;
        _usageBadge.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (visible)
        {
            PositionHeadBubbles();
            _usageBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }
        else { _lastUsageHover = double.NegativeInfinity; _usageBadge.BeginAnimation(OpacityProperty, null); }
    }

    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        _pendingPreviewKey = null;
        RegisterPetInteraction();
        StopPreview();
        _pointerDown = true; _dragging = false;
        NativeDesktop.GetCursorPos(out _pointerStart); _dragStart = NativeDesktop.WindowRect(_handle);
        _sprite.CaptureMouse(); e.Handled = true;
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        NativeDesktop.GetCursorPos(out var pointer);
        var dx = pointer.X - _pointerStart.X; var dy = pointer.Y - _pointerStart.Y;
        if (!_dragging && (Math.Abs(dx) >= SystemParameters.MinimumHorizontalDragDistance * DpiScale
            || Math.Abs(dy) >= SystemParameters.MinimumVerticalDragDistance * DpiScale))
        {
            _dragging = true; _clickTimer.Stop(); SetUsageHover(false); _behavior.BeginDrag();
            if (_peekEdge != ScreenEdge.None)
            {
                LeaveEdgePeek(new Point(_pointerStart.X, _pointerStart.Y), new Point(pointer.X, pointer.Y));
                _dragStart = NativeDesktop.WindowRect(_handle); _pointerStart = pointer; dx = dy = 0;
            }
        }
        if (_dragging) MoveClamped(_dragStart.X + dx, _dragStart.Y + dy, NativeDesktop.WorkAreaAt(pointer.X, pointer.Y));
        e.Handled = true;
    }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerDown) return;
        var wasDrag = _dragging;
        _pointerDown = false; _sprite.ReleaseMouseCapture();
        if (wasDrag) FinishDrag();
        else { _clickTimer.Stop(); _clickTimer.Start(); }
        e.Handled = true;
    }
    private void FinishDrag()
    {
        if (!_dragging) return;
        var area = CharacterWorkArea(); var before = CharacterScreenBounds();
        NativeDesktop.GetCursorPos(out var pointer);
        var dx = pointer.X - _pointerStart.X; var dy = pointer.Y - _pointerStart.Y;
        var preferred = Math.Abs(dx) >= Math.Abs(dy) ? dx < 0 ? ScreenEdge.Left : ScreenEdge.Right
            : dy < 0 ? ScreenEdge.Top : ScreenEdge.Bottom;
        var edge = EdgePeekPlacement.Detect(before, area, 16 * DpiScale, preferred);
        _dragging = false; _behavior.EndDrag();
        if (edge == ScreenEdge.None || !EnterEdgePeek(edge, area)) ClampCurrent();
        ScheduleSave();
    }
    private void ClickReaction()
    {
        RegisterPetInteraction();
        if (!PetApplication.SmokeTest) _growth.Interact(DateTimeOffset.Now);
        var actions = _sprite.AvailableActions.Where(a => a.Group == AnimationPreviewGroup.ClickInteraction).ToArray();
        if (_peekEdge == ScreenEdge.None && !_settings.Paused && actions.Length > 0) PreviewAnimation(actions[_random.Next(actions.Length)].Key);
        else if (_peekEdge == ScreenEdge.None && !_settings.Paused) _behavior.React(PetState.Idle, 2.5);
    }
}
