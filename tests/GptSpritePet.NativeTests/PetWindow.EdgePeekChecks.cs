using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunEdgePeekChecks(Dictionary<string, bool> checks)
    {
        RunPeekPresentationChecks(checks);
        var saved = _settings; var rect = NativeDesktop.WindowRect(_handle);
        try
        {
            foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
            {
                LeaveEdgePeek();
                ApplySettings(saved with { Size = 240, Paused = false, UsageCardVisible = true, ClickThrough = false });
                _tick.Stop(); _behavior.Wake(); _sprite.State = PetState.Idle; _sprite.ResetAnimation(); UpdateLayout();
                var area = CharacterWorkArea(); var local = LocalCharacterBounds(); var window = NativeDesktop.WindowRect(_handle);
                var centerX = area.X + area.Width / 2; var centerY = area.Y + area.Height / 2;
                var projected = EdgePeekPlacement.Attach(window, local, area, edge, centerX, centerY);
                _dragging = true; _behavior.BeginDrag();
                NativeDesktop.GetCursorPos(out _pointerStart);
                MoveClamped(projected.X, projected.Y, area);
                var layouts = 0; var guarded = true;
                void ObserveLayout(object? sender, EventArgs args)
                { layouts++; guarded &= _sprite.PlacementPresentationHeld; }
                _sprite.LayoutUpdated += ObserveLayout;
                try { FinishDrag(); }
                finally { _sprite.LayoutUpdated -= ObserveLayout; }
                checks["peek_layout_keeps_previous_pose_" + edge] = layouts > 0 && guarded && !_sprite.PlacementPresentationHeld;
                UpdateLayout();
                checks["peek_drag_release_" + edge] = _peekEdge == edge && _sprite.EdgePeek == edge && _settings.EdgePeek == edge;
                checks["peek_freezes_timer_" + edge] = !_tick.IsEnabled && !_settings.Paused;
                var before = CharacterScreenBounds(); var animation = _sprite.AnimationTime; var inactive = _behavior.InactiveSeconds;
                AdvanceFrame(700); _sprite.Advance(700);
                checks["peek_no_automatic_actions_" + edge] = animation == _sprite.AnimationTime && inactive == _behavior.InactiveSeconds && before == CharacterScreenBounds();
                checks["peek_attached_" + edge] = edge switch
                {
                    ScreenEdge.Left => Math.Abs(before.X - area.X) <= 1,
                    ScreenEdge.Right => Math.Abs(before.Right - area.Right) <= 1,
                    ScreenEdge.Top => Math.Abs(before.Y - area.Y) <= 1,
                    _ => Math.Abs(before.Bottom - area.Bottom) <= 1
                };
                checks["peek_configured_head_size_" + edge] = Math.Abs(_sprite.PeekHeadHeight - 96) < .01;
                checks["peek_character_viewport_margin_" + edge] = RenderHasMargin(RenderSprite(_sprite));
                var opaque = FindOpaquePoint(_sprite);
                checks["peek_alpha_hit_" + edge] = opaque.HasValue && !_sprite.IsOpaqueHit(new Point(0, 0));
                SetUsageHover(false); UpdateUsageHover(_sprite.PointToScreen(opaque ?? new Point(180, 180)), _clock.Elapsed.TotalSeconds, true); UpdateLayout();
                checks["peek_hover_card_" + edge] = _usageBadge.IsVisible && PeekCardFits(area);
                SetUsageHover(false); UpdateLayout();
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                PumpResetUi(); // Reuse the native layered-window presentation wait.
                var hit = _sprite.PointToScreen(opaque ?? new Point(180, 180));
                checks["peek_native_pointer_hit_" + edge] = NativeDesktop.WindowFromPoint(new NativeDesktop.Point
                    { X = (int)hit.X, Y = (int)hit.Y }) == _handle;
                SetUsageHover(true); UpdateLayout();
                checks["peek_context_menu_" + edge] = CreateMenuCommands().Any(c => c.Label == "账号用量…" && c.Execute is not null)
                    && CreateContextMenu().Items.Count > 0;
                SaveBitmap(RenderElement(_canvas), Path.Combine(_store.DirectoryPath, "edge-peek-" + edge + ".png"));
                SaveBitmap(RenderSprite(_sprite), Path.Combine(_store.DirectoryPath, "edge-peek-character-" + edge + ".png"));
                ClickReaction();
                checks["peek_click_keeps_pose_" + edge] = _peekEdge == edge && _sprite.PreviewKey is null && !_tick.IsEnabled;
                        foreach (var size in new[] { 160, 360 })
                {
                    ApplySettings(_settings with { Size = size, UsageBubbleScale = 2 }); UpdateLayout();
                    checks["peek_head_resizes_" + edge + "_" + size] = Math.Abs(_sprite.PeekHeadHeight - size * .4) < .01;
                    var resized = CharacterScreenBounds();
                    checks["peek_body_resizes_proportionally_" + edge + "_" + size] =
                        Math.Abs(resized.Width / before.Width - size / 240d) < .01 &&
                        Math.Abs(resized.Height / before.Height - size / 240d) < .01;
                    AttachPeek(area, area.X + 1, area.Y + 1); UpdateLayout(); SetUsageHover(true); PositionHeadBubbles();
                    checks["peek_corner_card_" + edge + "_" + size] = PeekCardFits(area);
                    AttachPeek(area, area.Right - 1, area.Bottom - 1); PositionHeadBubbles();
                    checks["peek_opposite_corner_card_" + edge + "_" + size] = PeekCardFits(area);
                }
                var paused = _settings.Paused;
                LeaveEdgePeek(); UpdateLayout();
                checks["peek_drag_out_restores_" + edge] = _peekEdge == ScreenEdge.None && _sprite.EdgePeek == ScreenEdge.None && _settings.Paused == paused && _tick.IsEnabled;
                SetUsageHover(false); _tick.Stop();
            }
            var area2 = CharacterWorkArea();
            EnterEdgePeek(ScreenEdge.Left, area2); TogglePause();
            checks["peek_pause_setting_preserved"] = _settings.Paused && !_tick.IsEnabled;
            LeaveEdgePeek(); checks["peek_leaving_respects_pause"] = _settings.Paused && !_tick.IsEnabled;
            TogglePause(); EnterEdgePeek(ScreenEdge.Top, area2); ResetPosition();
            checks["peek_reset_restores_normal"] = _peekEdge == ScreenEdge.None && _settings.EdgePeek == ScreenEdge.None;
            EnterEdgePeek(ScreenEdge.Right, area2);
            var action = _sprite.AvailableActions.FirstOrDefault();
            if (action is not null) PreviewAnimation(action.Key);
            checks["peek_explicit_preview_leaves_dock"] = _peekEdge == ScreenEdge.None && _sprite.PreviewKey is not null;
            StopPreview();
        }
        finally
        {
            _dragging = false; LeaveEdgePeek(); SetUsageHover(false);
            ApplySettings(saved); _tick.Stop(); MoveClamped(rect.X, rect.Y, CharacterWorkArea(rect.X, rect.Y));
            UpdateLayout(); PumpResetUi();
        }
    }

    private void RunPeekPresentationChecks(Dictionary<string, bool> checks)
    {
        using var sprite = new SpriteView(); sprite.LoadSkin(_settings.SkinDirectory);
        const double viewport = 480;
        sprite.Width = sprite.Height = viewport;
        sprite.Measure(new Size(viewport, viewport)); sprite.Arrange(new Rect(0, 0, viewport, viewport));
        foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            sprite.SetEdgePeek(ScreenEdge.None); sprite.ResetAnimation();
            var normal = BitmapPixels(RenderSprite(sprite));
            using (sprite.HoldPlacementPresentation())
            {
                sprite.SetEdgePeek(edge); sprite.PeekSize = 96;
                checks["peek_no_early_pose_pixels_" + edge] = normal.SequenceEqual(BitmapPixels(RenderSprite(sprite)));
                using (sprite.HoldPlacementPresentation()) { }
                checks["peek_nested_hold_keeps_old_pixels_" + edge] = sprite.PlacementPresentationHeld
                    && normal.SequenceEqual(BitmapPixels(RenderSprite(sprite)));
            }
            checks["peek_commit_displays_new_pixels_" + edge] = !sprite.PlacementPresentationHeld
                && !normal.SequenceEqual(BitmapPixels(RenderSprite(sprite))) && Math.Abs(sprite.PeekHeadHeight - 96) < .01;
            checks["peek_committed_alpha_hit_" + edge] = AlphaMatchesRender(sprite, RenderSprite(sprite));
            var docked = BitmapPixels(RenderSprite(sprite));
            using (sprite.HoldPlacementPresentation())
            {
                sprite.SetEdgePeek(ScreenEdge.None);
                checks["peek_leaving_holds_pose_" + edge] = docked.SequenceEqual(BitmapPixels(RenderSprite(sprite)));
            }
            checks["peek_leaving_releases_pose_" + edge] = normal.SequenceEqual(BitmapPixels(RenderSprite(sprite)));
        }
    }

    private bool PeekCardFits(DesktopRect area)
    {
        // Canvas coordinates must be arranged before testing their physical screen positions.
        UpdateLayout();
        var first = _usageBadge.PointToScreen(new Point(0, 0));
        var last = _usageBadge.PointToScreen(new Point(_usageBadge.ActualWidth, _usageBadge.ActualHeight));
        var fits = first.X >= area.X - 1 && first.Y >= area.Y - 1 && last.X <= area.Right + 1 && last.Y <= area.Bottom + 1
            && Canvas.GetLeft(_usageBadge) >= -1 && Canvas.GetTop(_usageBadge) >= -1
            && Canvas.GetLeft(_usageBadge) + _usageBadge.ActualWidth * _settings.UsageBubbleScale <= Width + 1
            && Canvas.GetTop(_usageBadge) + _usageBadge.ActualHeight * _settings.UsageBubbleScale <= Height + 1;
        if (!fits)
            File.AppendAllText(Path.Combine(_store.DirectoryPath, "peek-layout-failures.jsonl"), System.Text.Json.JsonSerializer.Serialize(new
            {
                edge = _peekEdge.ToString(), size = _settings.Size, area, first, last,
                left = Canvas.GetLeft(_usageBadge), top = Canvas.GetTop(_usageBadge), Width, Height,
                cardWidth = _usageBadge.ActualWidth * _settings.UsageBubbleScale,
                cardHeight = _usageBadge.ActualHeight * _settings.UsageBubbleScale, window = NativeDesktop.WindowRect(_handle)
            }) + Environment.NewLine);
        return fits;
    }
}
