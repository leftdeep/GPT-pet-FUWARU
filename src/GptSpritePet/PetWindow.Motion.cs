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
    private void ApplyView()
    {
        _usageHeadTop = null;
        var showUsage = _settings.UsageCardVisible;
        // Reserve transparent space so revealing the card never moves the character or its feet.
        var spriteTop = showUsage ? 200 * _settings.UsageBubbleScale : 82;
        if (!showUsage || _settings.ClickThrough) SetUsageHover(false);
        if (!_skinInitialized || _loadedSkin != _settings.SkinDirectory)
        {
            _skinLoadWarning = null;
            _sprite.LoadSkin(_settings.SkinDirectory);
            if (_sprite.LoadWarning != null && _settings.SkinDirectory != null)
            {
                _skinLoadWarning = _sprite.LoadWarning + "\n已恢复内置角色。";
                _settings = _settings with { SkinDirectory = null };
                _sprite.LoadSkin(null);
            }
            _loadedSkin = _settings.SkinDirectory; _skinInitialized = true;
            _skinManifestStamp = File.GetLastWriteTimeUtc(SkinManifest);
        }
        // Size describes the standing character. The transparent viewport also reserves authored effect space.
        if (_peekEdge != ScreenEdge.None && !_sprite.SetEdgePeek(_peekEdge))
        { _peekEdge = ScreenEdge.None; _settings = _settings with { EdgePeek = ScreenEdge.None }; }
        // Match every pose by crown-to-chin head height, near the existing left pose's scale.
        _sprite.PeekSize = _settings.Size * .4;
        var spriteSize = _peekEdge == ScreenEdge.None ? _sprite.ViewportSizeAt(_settings.Size) : _sprite.PeekViewportSize;
        var cardWidth = UsageBubbleBaseWidth * _settings.UsageBubbleScale;
        Width = Math.Max(spriteSize + 40, cardWidth + 40); Height = spriteSize + spriteTop + 22;
        _sprite.Width = spriteSize; _sprite.Height = spriteSize;
        Canvas.SetLeft(_sprite, (Width - spriteSize) / 2); Canvas.SetTop(_sprite, spriteTop);
        if (_peekEdge != ScreenEdge.None)
        {
            Width = _peekEdge is ScreenEdge.Top or ScreenEdge.Bottom
                ? Math.Max(spriteSize + cardWidth + 40, cardWidth * 2 + 40) : spriteSize + cardWidth + 40;
            // Wrapped quota headings and the thick bars need additional room at screen corners.
            Height = spriteSize + 224 * _settings.UsageBubbleScale + 40;
            Canvas.SetLeft(_sprite, _peekEdge == ScreenEdge.Left ? 12 : _peekEdge == ScreenEdge.Right ? Width - spriteSize - 12 : (Width - spriteSize) / 2);
            Canvas.SetTop(_sprite, _peekEdge == ScreenEdge.Top ? 12 : _peekEdge == ScreenEdge.Bottom ? Height - spriteSize - 12 : (Height - spriteSize) / 2);
        }
        _usageBadge.Width = UsageBubbleBaseWidth;
        _usageBadge.LayoutTransform = new ScaleTransform(_settings.UsageBubbleScale, _settings.UsageBubbleScale);
        Canvas.SetLeft(_usageBadge, (Width - cardWidth) / 2);
        Topmost = _settings.Topmost;
        _topmostGuard?.Synchronize();
        _behavior.AvailableStates = _sprite.AvailableActions.Where(a => a.State.HasValue).Select(a => a.State!.Value).ToHashSet();
        // Desktop movement uses the idle clip until a dedicated walking clip is installed.
        _behavior.AvailableStates = _behavior.AvailableStates.Append(PetState.Walking).ToHashSet();
        _behavior.Paused = _settings.Paused; _behavior.AutoWalk = _settings.AutoWalk; _behavior.Frequency = _settings.Frequency;
        _behavior.WalkingSpeed = _sprite.WalkingSpeedAt(_settings.Size); _walkingPhysicalX = null;
        _behavior.WalkingTiming = _sprite.WalkingTiming;
        _behavior.MovementActionKeys = _sprite.AvailableActions.Where(a => a.Group == AnimationPreviewGroup.Walking).Select(a => a.Key).ToArray();
        _sprite.AutomaticMovementKey = _behavior.SelectedMovementKey;
        if (_sprite.PreviewKey is null) _sprite.WalkingPlayback = _behavior.WalkingPlayback;
        // Existing smoke scenarios use immediate state selection; dedicated rest
        // checks below exercise the real clip durations and queued interactions.
        _behavior.LieDownSeconds = PetApplication.SmokeTest ? 0 : _sprite.ClipSeconds(PetState.LyingDown);
        _behavior.WakeUpSeconds = PetApplication.SmokeTest ? 0 : _sprite.ClipSeconds(PetState.Waking);
        if (_handle != IntPtr.Zero) NativeDesktop.ApplyStyles(_handle, _settings.ClickThrough);
        PositionHeadBubbles();
    }

    private void PositionHeadBubbles()
    {
        var headTop = Canvas.GetTop(_sprite) + _sprite.VisibleTop;
        // Hold the reading anchor while open; breathing and individual sprite frames
        // can move the character's visible crown without moving the quota card.
        if (_usageHoverVisible && _peekEdge == ScreenEdge.None) _usageHeadTop ??= headTop;
        var rect = _handle == IntPtr.Zero ? default : NativeDesktop.WindowRect(_handle);
        var area = _handle == IntPtr.Zero ? default : CharacterWorkArea(rect.X, rect.Y);
        var dpi = DpiScale;
        foreach (var bubble in new[] { _usageBadge })
        {
            var scale = _settings.UsageBubbleScale;
            var width = bubble.ActualWidth * scale; var height = bubble.ActualHeight * scale;
            if (width <= 0 || height <= 0) continue;
            var anchor = _usageHoverVisible ? _usageHeadTop ?? headTop : headTop;
            var left = (Width - width) / 2;
            var top = Math.Max(12, anchor - 12 - height);
            if (_peekEdge != ScreenEdge.None)
            {
                PositionPeekBubble(bubble, width, height);
                left = Canvas.GetLeft(bubble); top = Canvas.GetTop(bubble);
            }
            if (_handle != IntPtr.Zero)
            {
                var minX = (area.X - rect.X) / dpi + 6;
                var maxX = (area.Right - rect.X) / dpi - width - 6;
                var minY = (area.Y - rect.Y) / dpi + 6;
                var maxY = (area.Bottom - rect.Y) / dpi - height - 6;
                if (_peekEdge != ScreenEdge.None)
                {
                    minX = Math.Max(6, minX); maxX = Math.Min(Width - width - 6, maxX);
                    minY = Math.Max(6, minY); maxY = Math.Min(Height - height - 6, maxY);
                }
                left = Math.Clamp(left, minX, Math.Max(minX, maxX));
                top = Math.Clamp(top, minY, Math.Max(minY, maxY));
            }
            // Assign the final position once, aligned to physical screen pixels.
            Canvas.SetLeft(bubble, Math.Round(left * dpi) / dpi);
            Canvas.SetTop(bubble, Math.Round(top * dpi) / dpi);
        }
        if (_handle != IntPtr.Zero) PositionResetAlert();
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds; var delta = Math.Max(0, now - _lastTick); _lastTick = now;
        if (_nativeMinimized) return;
        AdvanceFrame(delta);
        if (now - _lastMotionSave >= 10) { _lastMotionSave = now; SaveNow(); }
    }

    private void AdvanceFrame(double delta)
    {
        if (_peekEdge != ScreenEdge.None) return;
        if (!_pointerDown) _behavior.AdvanceInactivity(delta);
        delta = Math.Clamp(delta, 0, .25);
        if (_sprite.PreviewKey is not null)
        {
            _sprite.PrepareWalkingAdvance(delta);
            var previewMovement = (_walkingPreview?.Advance(delta) ?? 0) * _sprite.Direction * _sprite.WalkingSpeedAt(_settings.Size);
            _sprite.Advance(delta); PositionHeadBubbles(); MoveWalking(previewMovement, false);
            if (_walkingPreview is { Playing: false }) StopPreview();
            return;
        }
        if (!_behavior.Paused) _sprite.PrepareWalkingAdvance(delta);
        var movement = _behavior.Advance(delta, countInactivity: false);
        // A queued click can start its preview when waking reaches the standing pose.
        if (_sprite.PreviewKey is not null) { PositionHeadBubbles(); return; }
        _sprite.SetBehaviorState(_behavior.State, _behavior.WalkingPlayback, _behavior.Direction); _sprite.Advance(delta);
        _sprite.AutomaticMovementKey = _behavior.SelectedMovementKey;
        PositionHeadBubbles();
        MoveWalking(movement, true);
    }

    private void MoveWalking(double movement, bool automatic)
    {
        if (movement != 0 && !_pointerDown)
        {
            var rect = NativeDesktop.WindowRect(_handle); var area = CharacterWorkArea(rect.X, rect.Y);
            // Retain subpixel travel across native integer window moves; rounding each tick loses speed.
            if (!_walkingPhysicalX.HasValue || Math.Abs(_walkingPhysicalX.Value - rect.X) > 1) _walkingPhysicalX = rect.X;
            var proposed = rect with { X = _walkingPhysicalX.Value + movement * DpiScale };
            var target = DesktopBounds.ClampVisible(proposed, LocalCharacterBounds(), area) with { Y = rect.Y };
            if (Math.Abs(target.X - proposed.X) > 0.01 && _sprite.State == PetState.Walking)
            {
                if (automatic)
                {
                    _behavior.Turn();
                    _sprite.SetBehaviorState(_behavior.State, _behavior.WalkingPlayback, _behavior.Direction);
                }
                else _sprite.Direction = -_sprite.Direction;
                // The mirrored outline can differ slightly. Keep it within the same work area after turning.
                target = DesktopBounds.ClampVisible(target, LocalCharacterBounds(), area) with { Y = rect.Y };
            }
            _walkingPhysicalX = target.X;
            NativeDesktop.Move(_handle, target.X, target.Y);
            PositionHeadBubbles();
        }
        else _walkingPhysicalX = null;
    }

    private double DpiScale => Math.Max(96, NativeDesktop.GetDpiForWindow(_handle)) / 96d;
    private void UpdateTimer()
    {
        var visible = IsVisible && (_handle == IntPtr.Zero || NativeDesktop.IsWindowVisible(_handle) && !NativeDesktop.IsIconic(_handle));
        _tick.Stop(); _lastTick = _clock.Elapsed.TotalSeconds;
        if (visible && _peekEdge == ScreenEdge.None && (!_settings.Paused || _sprite.PreviewKey is not null)) _tick.Start();
        _hoverTimer.Stop();
        if (visible && !PetApplication.SmokeTest && _settings.UsageCardVisible && !_settings.ClickThrough) _hoverTimer.Start();
        if (!visible) { _clickTimer.Stop(); SetUsageHover(false); }
    }

    internal void ResetPosition()
    {
        if (_handle == IntPtr.Zero) return;
        LeaveEdgePeek();
        var area = NativeDesktop.GetCursorPos(out var pointer) ? NativeDesktop.WorkAreaAt(pointer.X, pointer.Y) : NativeDesktop.WorkArea(_handle);
        var visible = LocalCharacterBounds();
        MoveClamped(area.Right - visible.Right - 36 * DpiScale, area.Bottom - visible.Bottom, area);
        _behavior.Wake(); ScheduleSave();
    }
    private void ClampCurrent()
    {
        if (_handle == IntPtr.Zero || _exiting || NativeDesktop.IsIconic(_handle)) return;
        if (_peekEdge != ScreenEdge.None)
        {
            var visible = CharacterScreenBounds();
            AttachPeek(CharacterWorkArea(), visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
            ScheduleSave(); return;
        }
        var rect = NativeDesktop.WindowRect(_handle); MoveClamped(rect.X, rect.Y, CharacterWorkArea(rect.X, rect.Y)); ScheduleSave();
    }
    private void MoveClamped(double x, double y, DesktopRect area)
    {
        var rect = NativeDesktop.WindowRect(_handle); var target = DesktopBounds.ClampVisible(rect with { X = x, Y = y }, LocalCharacterBounds(), area);
        NativeDesktop.Move(_handle, target.X, target.Y);
        _walkingPhysicalX = null; PositionHeadBubbles();
    }
    private DesktopRect LocalCharacterBounds()
    {
        if (_sprite.ActualWidth <= 0 || _sprite.ActualHeight <= 0) return NativeDesktop.WindowRect(_handle) with { X = 0, Y = 0 };
        var bounds = _sprite.VisibleBounds; var dpi = DpiScale;
        return new((Canvas.GetLeft(_sprite) + bounds.X) * dpi, (Canvas.GetTop(_sprite) + bounds.Y) * dpi,
            bounds.Width * dpi, bounds.Height * dpi);
    }
    private DesktopRect CharacterScreenBounds()
    {
        var window = NativeDesktop.WindowRect(_handle); var local = LocalCharacterBounds();
        return local with { X = window.X + local.X, Y = window.Y + local.Y };
    }
    private DesktopRect CharacterWorkArea() { var window = NativeDesktop.WindowRect(_handle); return CharacterWorkArea(window.X, window.Y); }
    private DesktopRect CharacterWorkArea(double x, double y)
    {
        var local = LocalCharacterBounds();
        return NativeDesktop.WorkAreaAt((int)Math.Round(x + local.X + local.Width / 2), (int)Math.Round(y + local.Y + local.Height / 2));
    }
}
