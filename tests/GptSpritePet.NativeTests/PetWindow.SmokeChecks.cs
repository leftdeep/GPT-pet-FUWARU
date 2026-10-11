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
    private void RunSmokeTest()
    {
        var checks = new System.Collections.Generic.Dictionary<string, bool>();
        var original = _settings;
        RunMenuDismissChecks(checks);
        RunStructureChecks(checks);
        RunEdgePeekChecks(checks);
        RunTopmostChecks(checks);
        _tick.Stop();
        _behavior.Wake(); _sprite.State = PetState.Idle; _sprite.ResetAnimation();
        // The native layered-window hit test reads the last presented surface.
        // Present the reset pose before comparing its alpha with the native window.
        UpdateLayout();
        Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        PumpResetUi(); // Wait for the reset pose to reach the native layered surface.
        var foreground = NativeDesktop.GetForegroundWindow();
        var style = NativeDesktop.GetWindowLongPtr(_handle, NativeDesktop.GwlExStyle).ToInt64();
        checks["transparent_borderless"] = AllowsTransparency && WindowStyle == WindowStyle.None;
        checks["no_activate_tool_window"] = (style & NativeDesktop.WsExNoActivate) != 0 && (style & NativeDesktop.WsExToolWindow) != 0;
        checks["tray_created"] = _tray != null;
        var opaquePoint = FindOpaquePoint(_sprite);
        checks["opaque_hit"] = opaquePoint.HasValue;
        checks["blank_pass_through"] = !_sprite.IsOpaqueHit(new Point(0, 0));
        var solidPoint = _sprite.PointToScreen(opaquePoint ?? new Point(_sprite.ActualWidth / 2, _sprite.ActualHeight / 2));
        var blankPoint = PointToScreen(new Point(2, 2));
        var solidScreen = new NativeDesktop.Point { X = (int)solidPoint.X, Y = (int)solidPoint.Y };
        var blankScreen = new NativeDesktop.Point { X = (int)blankPoint.X, Y = (int)blankPoint.Y };
        checks["native_window_visible"] = NativeDesktop.IsWindowVisible(_handle);
        checks["native_opaque_hit"] = NativeDesktop.WindowFromPoint(solidScreen) == _handle;
        checks["native_blank_pass_through"] = NativeDesktop.WindowFromPoint(blankScreen) != _handle;
        ToggleClickThrough();
        checks["full_pass_through_style"] = (NativeDesktop.GetWindowLongPtr(_handle, NativeDesktop.GwlExStyle).ToInt64() & NativeDesktop.WsExTransparent) != 0;
        checks["native_full_pass_through"] = NativeDesktop.WindowFromPoint(solidScreen) != _handle;
        RestoreFromTray();
        checks["tray_restore_disables_pass_through"] = !_settings.ClickThrough && IsVisible;
        ApplySettings(_settings with { Paused = true }); checks["pause_stops_timer"] = !_tick.IsEnabled;
        ApplySettings(_settings with { Paused = false }); checks["resume_starts_timer"] = _tick.IsEnabled;
        HidePet(); checks["hide_stops_timer"] = !_tick.IsEnabled && !IsVisible;
        RestoreFromTray();
        ApplySettings(original); _tick.Stop();
        _behavior.BeginDrag(); checks["state_Dragging"] = _sprite.State == PetState.Dragging; _behavior.EndDrag();
        foreach (var action in _sprite.AvailableActions)
        {
            PreviewAnimation(action.Key);
            if (action.State == PetState.Sleeping && _sprite.HasAnimation(PetState.LyingDown) && _sprite.HasAnimation(PetState.Waking))
            {
                checks["preview_" + action.Key] = _sprite.PreviewKey is null && _behavior.State is (PetState.LyingDown or PetState.Sleeping);
                checks["preview_sleep_uses_behavior_timer"] = !_previewTimer.IsEnabled;
                _behavior.Wake();
                continue;
            }
            checks["preview_" + action.Key] = _sprite.PreviewKey == action.Key && _sprite.State == (action.State ?? PetState.Idle);
            checks["preview_full_duration_" + action.Key] = Math.Abs(_previewTimer.Interval.TotalSeconds - (_walkingPreview?.FinishSeconds ?? _sprite.PreviewSeconds(action.Key))) < .001;
            StopPreview();
        }
        if (_sprite.HasAnimation(PetState.Happy))
        {
            ApplySettings(original with { AutoWalk = false }); _tick.Stop(); _behavior.Wake();
            ClickReaction(); AdvanceFrame(.2); ClickReaction();
            checks["repeated_click_restarts_action"] = _sprite.CurrentFrameIndex == 0 && _sprite.PreviewKey is not null;
            // Clicks select any configured interaction, including custom keys.
            // Verify the actual selection rather than assuming it chose Happy.
            var clickedKey = _sprite.PreviewKey!;
            var clickedClip = SkinDefinition.Load(_settings.SkinDirectory!).NamedClips()[clickedKey];
            var frameSeconds = clickedClip.EffectiveFrameMilliseconds / 1000d;
            for (var frame = 1; frame < clickedClip.FrameCount; frame++) AdvanceFrame(frameSeconds);
            checks["click_plays_last_frame"] = _sprite.PreviewKey == clickedKey && _sprite.CurrentFrameIndex == clickedClip.FrameCount - 1;
            AdvanceFrame(frameSeconds + .001);
            StopPreview();
            checks["click_returns_to_idle"] = _behavior.State == PetState.Idle;
            ApplySettings(original); _tick.Stop();
        }
        RunSegmentedWalkingChecks(checks, original);
        ApplySettings(_settings with { Size = 160, Topmost = false }); UpdateLayout();
        checks["resize_and_topmost"] = Math.Abs(_sprite.ActualWidth - _sprite.ViewportSizeAt(160)) <= 1 && !Topmost;
        MoveClamped(-99999, 99999, NativeDesktop.WorkArea(_handle));
        var rect = NativeDesktop.WindowRect(_handle); var area = NativeDesktop.WorkArea(_handle);
        var visibleCharacter = CharacterScreenBounds();
        checks["workarea_clamp"] = visibleCharacter.X >= area.X - 1 && visibleCharacter.Y >= area.Y - 1
            && visibleCharacter.Right <= area.Right + 1 && visibleCharacter.Bottom <= area.Bottom + 1;
        RunCharacterBoundaryChecks(checks, original);
        ApplySettings(original); SaveNow(); checks["settings_roundtrip"] = _store.Load() == _settings;
        checks["operations_did_not_activate"] = NativeDesktop.GetForegroundWindow() == foreground;
        _tick.Stop();
        if (_sprite.HasMeasuredWalkingContacts)
        {
                foreach (var size in new[] { 160, 240, 360 })
            foreach (var direction in new[] { -1, 1 })
            {
            ApplySettings(original with { Size = size }); _tick.Stop(); UpdateLayout();
            var walkArea = NativeDesktop.WorkArea(_handle);
            MoveClamped(walkArea.X + walkArea.Width / 2 - Width * DpiScale / 2,
                walkArea.Y + walkArea.Height / 2, walkArea);
            _behavior.React(PetState.Walking, 3); _sprite.ResetAnimation(); _walkingPhysicalX = null;
            if (_behavior.Direction != direction) _behavior.Turn(); _sprite.Direction = direction;
            var startRect = NativeDesktop.WindowRect(_handle);
            var startSole = _sprite.PointToScreen(_sprite.WalkingSolePoint(true));
            var locked = true;
            var secondLocked = true;
            var supportLeg = _sprite.CurrentSupportLeg;
            var supportSole = startSole;
            var trace = new System.Collections.Generic.List<object>();
            var frameSeconds = _sprite.WalkingRenderMilliseconds / 1000d;
            var frames = (int)Math.Round(_sprite.FrameCount(PetState.Walking) * _sprite.FrameMillisecondsFor(PetState.Walking) / (double)_sprite.WalkingRenderMilliseconds);
            for (var frame = 1; frame <= frames; frame++)
            {
                AdvanceFrame(frameSeconds);
                var traceSole = _sprite.PointToScreen(_sprite.WalkingSolePoint(true));
                trace.Add(new { frame, poseIndex = _sprite.CurrentFrameIndex, rect = NativeDesktop.WindowRect(_handle),
                    soleX = traceSole.X, soleY = traceSole.Y, physicalX = _walkingPhysicalX });
                if (_sprite.HasFullCharacterWalk)
                {
                    if (_sprite.CurrentSupportLeg != supportLeg) { supportLeg = _sprite.CurrentSupportLeg; supportSole = traceSole; }
                    else
                    {
                        var stable = Math.Abs(traceSole.X - supportSole.X) < 1.5 && Math.Abs(traceSole.Y - supportSole.Y) < 0.5;
                        if (supportLeg == 0) locked &= stable; else secondLocked &= stable;
                    }
                }
                else if (frame < frames / 2) locked &= Math.Abs(traceSole.X - startSole.X) < 1.5 && Math.Abs(traceSole.Y - startSole.Y) < 0.5;
            }
            var expected = _sprite.WalkingSpeedAt(_settings.Size) * frames * frameSeconds * DpiScale * _behavior.Direction;
            checks[$"native_walk_stride_{size}_{direction}"] = Math.Abs(NativeDesktop.WindowRect(_handle).X - startRect.X - expected) <= 1;
            checks[$"native_support_foot_{size}_{direction}"] = locked;
            if (_sprite.HasFullCharacterWalk) checks[$"native_support_foot_second_{size}_{direction}"] = secondLocked;
            File.WriteAllText(Path.Combine(_store.DirectoryPath, $"walking-motion-trace-{size}-{direction}.json"), JsonSerializer.Serialize(new
                { startRect, startSole, expected, dpiScale = DpiScale, trace }, SettingsStore.JsonOptions));
            _behavior.Wake();
            }
            ApplySettings(original); _tick.Stop(); UpdateLayout();
        }
        CreateAnimationPreview(checks);
        RunAnimationTransitionChecks(checks);
        RunOnDemandAnimationChecks(checks);
        RunSleepingChecks(checks);
        _behavior.Wake(); _sprite.State = PetState.Idle;
        UpdateLayout();
        var preview = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        preview.Render(_canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
        using (var output = File.Create(Path.Combine(_store.DirectoryPath, "smoke-preview.png"))) encoder.Save(output);
        var settingsPreview = CreateControlPreview(PetControlTab.Settings,
            new SettingsPage(() => _settings, _sprite.SkinName, _store.DirectoryPath, ApplySettings, _sprite.UsesStandingHeight));
        settingsPreview.Show(); settingsPreview.UpdateLayout();
        checks["settings_window_loaded"] = settingsPreview.IsLoaded && settingsPreview.ActualWidth >= 440;
        var settingsBitmap = new RenderTargetBitmap((int)settingsPreview.ActualWidth, (int)settingsPreview.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        settingsBitmap.Render(settingsPreview);
        var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
        using (var output = File.Create(Path.Combine(_store.DirectoryPath, "settings-preview.png"))) settingsEncoder.Save(output);
        settingsPreview.Close();
        RunGrowthUiChecks(checks);
        RunResetUiChecks(checks);
        // Boundary checks deliberately leave transparent window padding offscreen.
        // Inspect the full usage card at the center, independently of those edge scenarios.
        var usageArea = CharacterWorkArea(); var usageWindowRect = NativeDesktop.WindowRect(_handle);
        MoveClamped(usageArea.X + (usageArea.Width - usageWindowRect.Width) / 2,
            usageArea.Y + (usageArea.Height - usageWindowRect.Height) / 2, usageArea);
        var usageSnapshot = _usageService.Snapshot;
        var usagePreviewFile = Path.Combine(_store.DirectoryPath, "usage-preview.json");
        if (File.Exists(usagePreviewFile) && new FileInfo(usagePreviewFile).Length < 2 * 1024 * 1024)
            usageSnapshot = JsonSerializer.Deserialize<AccountUsageSnapshot>(File.ReadAllText(usagePreviewFile)) ?? usageSnapshot;
        var metricStamp = DateTimeOffset.Now;
        var metricTask = usageSnapshot.CurrentTask ?? new ActiveTaskUsage("usage-preview", "当前任务显示测试", "preview", 120000, 40000,
            metricStamp.AddMinutes(-2), metricStamp);
        metricTask = metricTask with { OutputRate = new OutputRateSample(400, 4000, metricStamp) };
        usageSnapshot = usageSnapshot with { SelectedTaskId = metricTask.Id, LocalUsage =
            new(DateOnly.FromDateTime(metricStamp.LocalDateTime), usageSnapshot.LocalUsage?.TodayTokens, [metricTask], metricStamp, "界面验证数据", true) };
        UpdateUsage(usageSnapshot);
        UpdateLayout();
        ApplySettings(original with { UsageEnabled = true, UsageCardVisible = true, ClickThrough = false }); _tick.Stop(); UpdateLayout();
        var hoverPoint = _sprite.PointToScreen(FindOpaquePoint(_sprite) ?? new Point(_sprite.ActualWidth / 2, _sprite.ActualHeight / 2));
        var awayPoint = PointToScreen(new Point(-50, -50));
        SetUsageHover(false);
        checks["usage_hidden_by_default"] = !_usageBadge.IsVisible;
        UpdateUsageHover(_sprite.PointToScreen(new Point(0, 0)), 10, true);
        checks["usage_transparent_pixels_do_not_show"] = !_usageBadge.IsVisible;
        UpdateUsageHover(hoverPoint, 11, false);
        checks["usage_covered_character_does_not_show"] = !_usageBadge.IsVisible;
        var feetBeforeHover = _sprite.PointToScreen(new Point(_sprite.ActualWidth / 2, _sprite.ActualHeight));
        UpdateUsageHover(hoverPoint, 12, true); UpdateLayout();
        checks["usage_hover_shows"] = _usageBadge.IsVisible;
        checks["usage_hover_reveal_keeps_feet"] = feetBeforeHover == _sprite.PointToScreen(new Point(_sprite.ActualWidth / 2, _sprite.ActualHeight));
        _behavior.React(PetState.Idle, 30); _sprite.SetBehaviorState(_behavior.State, _behavior.WalkingPlayback, _behavior.Direction);
        var idleHoverTime = _sprite.AnimationTime; AdvanceFrame(.2);
        checks["usage_hover_keeps_idle_animation"] = _sprite.AnimationTime > idleHoverTime;
        var fixedHoverTop = Canvas.GetTop(_usageBadge);
        var hoverAnchorStable = true;
        var movingHeadTop = Canvas.GetTop(_sprite) + _sprite.VisibleTop;
        var headMoved = false;
        for (var step = 0; step < 144; step++)
        {
            AdvanceFrame(1d / 24); UpdateLayout();
            hoverAnchorStable &= Math.Abs(Canvas.GetTop(_usageBadge) - fixedHoverTop) < .01;
            headMoved |= Math.Abs(Canvas.GetTop(_sprite) + _sprite.VisibleTop - movingHeadTop) > .1;
        }
        checks["usage_hover_anchor_stable_across_idle_frames"] = hoverAnchorStable;
        checks["usage_hover_stability_keeps_character_motion"] = headMoved && _sprite.AnimationTime > idleHoverTime + 5;
        _behavior.React(PetState.Walking, 5);
        var walkingBeforeHover = _behavior.WalkingPlayback?.ElapsedSeconds ?? 0;
        AdvanceFrame(.2);
        checks["usage_hover_keeps_walking_animation"] = (_behavior.WalkingPlayback?.ElapsedSeconds ?? 0) > walkingBeforeHover;
        // Authored turn/lift-skirt intro intentionally stays in place before walking.
        for (var step = 0; step < 60 && _behavior.WalkingPlayback?.Phase == WalkPhase.Start; step++) AdvanceFrame(.25);
        var hoverRect = NativeDesktop.WindowRect(_handle); AdvanceFrame(.2);
        checks["usage_hover_keeps_walking_movement"] = NativeDesktop.WindowRect(_handle).X != hoverRect.X;
        UpdateUsageHover(awayPoint, 12.2, false);
        checks["usage_hover_transfer_grace"] = _usageBadge.IsVisible;
        UpdateUsageHover(_usageBadge.PointToScreen(new Point(20, 20)), 12.3, true);
        UpdateUsageHover(awayPoint, 12.5, false);
        checks["usage_hover_card_keeps_open"] = _usageBadge.IsVisible;
        UpdateUsageHover(awayPoint, 13, false);
        checks["usage_leave_hides"] = !_usageBadge.IsVisible;
        checks["usage_hidden_card_passes_through"] = NativeDesktop.WindowFromPoint(new NativeDesktop.Point
            { X = (int)_usageBadge.PointToScreen(new Point(20, 20)).X, Y = (int)_usageBadge.PointToScreen(new Point(20, 20)).Y }) != _handle;
        ApplySettings(_settings with { Paused = true });
        UpdateUsageHover(_sprite.PointToScreen(FindOpaquePoint(_sprite) ?? new Point(100, 100)), 14, true);
        checks["usage_hover_works_when_paused"] = _usageBadge.IsVisible;
        ApplySettings(_settings with { UsageEnabled = false }); UpdateLayout();
        UpdateUsageHover(_sprite.PointToScreen(FindOpaquePoint(_sprite) ?? new Point(100, 100)), 14.5, true);
        checks["usage_hover_without_account_connection"] = _usageBadge.IsVisible;
        _dragging = true; UpdateUsageHover(hoverPoint, 15, true);
        checks["usage_drag_hides"] = !_usageBadge.IsVisible; _dragging = false;
        ApplySettings(_settings with { ClickThrough = true }); UpdateUsageHover(hoverPoint, 16, true);
        checks["usage_clickthrough_hides"] = !_usageBadge.IsVisible;
        ApplySettings(original with { UsageEnabled = true, UsageCardVisible = false }); UpdateUsageHover(hoverPoint, 17, true);
        checks["usage_setting_off_hides"] = !_usageBadge.IsVisible;
        ApplySettings(original with { UsageEnabled = true, UsageCardVisible = true, ClickThrough = false }); _tick.Stop();
        // Quota fixtures must not be overwritten by either account polling or the
        // independent local token reader when the dispatcher pumps animation frames.
        _usageService.Changed -= OnAccountUsageChanged;
        _usageService.Configure(_settings with { UsageEnabled = false });
        Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        UpdateUsage(usageSnapshot);
        UpdateLayout(); UpdateUsageHover(_sprite.PointToScreen(FindOpaquePoint(_sprite) ?? new Point(100, 100)), 18, true);
        _usageBadge.BeginAnimation(OpacityProperty, null); _usageBadge.Opacity = 1; UpdateLayout();
        Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        // Layered-window alpha is presented asynchronously; let a native frame arrive.
        var presentationFrame = new DispatcherFrame();
        var presentationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        presentationTimer.Tick += (_, _) => { presentationTimer.Stop(); presentationFrame.Continue = false; };
        presentationTimer.Start(); Dispatcher.PushFrame(presentationFrame);
        var usageSummary = UsageSummary.Create(usageSnapshot, DateTimeOffset.Now, _settings.UsageRefreshSeconds);
        checks["usage_four_metrics"] = _usageValues.Select(value => value.Text).SequenceEqual(new[]
            { usageSummary.FiveHours.Value, usageSummary.SevenDays.Value, usageSummary.Today.Value, usageSummary.Task.Value });
        checks["usage_output_rate_metric"] = _outputRateValue.Text == usageSummary.OutputRate.Value && _outputRateValue.ToolTip is string rateHint && rateHint.Contains("不计输入和缓存");
        var staleRate = usageSnapshot with { LocalUsage = usageSnapshot.LocalUsage! with
            { Tasks = [metricTask with { OutputRate = metricTask.OutputRate! with { SampledAt = DateTimeOffset.Now.AddSeconds(-10) } }] } };
        UpdateUsage(staleRate);
        checks["usage_output_rate_stale"] = _outputRateValue.Text == "上次 100 tok/s";
        var quotaNow = DateTimeOffset.Now;
        var mixedQuota = usageSnapshot with { State = UsageConnectionState.Ready, LimitsAvailable = true, LimitsUpdatedAt = quotaNow,
            Quotas = [new("codex", "Codex", 100, 300, quotaNow.AddHours(1)), new("codex", "Codex", 20, 10080, quotaNow.AddDays(6))] };
        UpdateUsage(mixedQuota);
        checks["usage_quota_colors_independent"] = ((SolidColorBrush)_usageValues[0].Foreground).Color == Color.FromRgb(193, 75, 75) &&
            ((SolidColorBrush)_usageValues[1].Foreground).Color == Color.FromRgb(54, 48, 63);
        checks["usage_quota_reset_times_independent"] = _quotaTimes[0].Text == quotaNow.AddHours(1).ToLocalTime().ToString("MM-dd HH:mm") &&
            _quotaTimes[1].Text == quotaNow.AddDays(6).ToLocalTime().ToString("MM-dd HH:mm");
        UpdateLayout();
        var quotaCanvas = RenderElement(_canvas);
        SaveBitmap(new CroppedBitmap(quotaCanvas, new Int32Rect((int)Canvas.GetLeft(_usageBadge), (int)Canvas.GetTop(_usageBadge),
            (int)Math.Ceiling(_usageBadge.ActualWidth * _settings.UsageBubbleScale), (int)Math.Ceiling(_usageBadge.ActualHeight * _settings.UsageBubbleScale))),
            Path.Combine(_store.DirectoryPath, "usage-quota-preview.png"));
        UpdateUsage(mixedQuota with { Quotas = [new("codex", "Codex", 20, 300, quotaNow.AddHours(1)), new("codex", "Codex", 85, 10080, quotaNow.AddDays(6))] });
        checks["usage_weekly_warning_only"] = ((SolidColorBrush)_usageValues[0].Foreground).Color == Color.FromRgb(54, 48, 63) &&
            ((SolidColorBrush)_usageValues[1].Foreground).Color == Color.FromRgb(167, 126, 30);
        checks["quota_bars_follow_independent_balances"] = _quotaBars[0].RemainingPercent == 80 && _quotaBars[1].RemainingPercent == 15;
        var waveVisibility = _usageBadge.Visibility;
        _usageBadge.Visibility = Visibility.Visible; UpdateLayout();
        _quotaBars[0].SetTaskActive(false);
        var waveSize = _quotaBars[0].RenderSize;
        var waveBoundsBefore = ((DrawingVisual)VisualTreeHelper.GetChild(_quotaBars[0], 0)).Drawing.Bounds;
        _quotaBars[0].SetMetric(new("5小时余额", "60%", "", true, 60));
        checks["quota_update_preserves_layout_validity"] = _quotaBars[0].IsMeasureValid && _quotaBars[0].IsArrangeValid;
        checks["quota_consumption_animates_only_when_enabled"] = _quotaBars[0].Animating == SystemParameters.ClientAreaAnimation;
        _quotaBars[0].SetMetric(new("5小时余额", "60%", "", true, 60));
        checks["quota_unchanged_poll_preserves_animation"] = _quotaBars[0].Animating == SystemParameters.ClientAreaAnimation;
        var waveFrames = 0;
        var waveLayoutStable = true;
        var waveTop = Canvas.GetTop(_usageBadge);
        EventHandler checkWaveFrame = (_, _) =>
        {
            waveFrames++;
            waveLayoutStable &= _quotaBars[0].IsMeasureValid && _quotaBars[0].IsArrangeValid
                && _quotaBars[0].RenderSize == waveSize && Math.Abs(Canvas.GetTop(_usageBadge) - waveTop) < .01;
        };
        CompositionTarget.Rendering += checkWaveFrame;
        var waveFrame = new DispatcherFrame();
        var waveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        waveTimer.Tick += (_, _) => { waveTimer.Stop(); waveFrame.Continue = false; };
        try { waveTimer.Start(); Dispatcher.PushFrame(waveFrame); }
        finally { waveTimer.Stop(); CompositionTarget.Rendering -= checkWaveFrame; }
        var waveBoundsAfter = ((DrawingVisual)VisualTreeHelper.GetChild(_quotaBars[0], 0)).Drawing.Bounds;
        checks["quota_render_frames_preserve_layout_and_position"] = (!SystemParameters.ClientAreaAnimation || waveFrames > 2) && waveLayoutStable;
        checks["quota_wave_consumption_changes_drawing"] = waveBoundsAfter.Width < waveBoundsBefore.Width - 5 && !_quotaBars[0].Animating;
        _usageBadge.Visibility = Visibility.Hidden;
        checks["quota_hidden_stops_render_subscription"] = !_quotaBars[0].Animating;
        _quotaBars[0].SetMetric(new("5小时余额", "不限", "", true));
        checks["quota_unlimited_has_no_fake_percentage_fill"] = _quotaBars[0].RemainingPercent is null && !_quotaBars[0].Animating;
        _usageBadge.Visibility = waveVisibility;
        RunQuotaTaskWaveChecks(checks, mixedQuota);
        UpdateUsage(usageSnapshot);
        SetUsageHover(false); SetUsageHover(true); UpdateLayout();
        checks["usage_card_above_character"] = Math.Abs(Canvas.GetTop(_sprite) + _sprite.VisibleTop
            - Canvas.GetTop(_usageBadge) - _usageBadge.ActualHeight * _settings.UsageBubbleScale - 12) <= 1;
        var cardCanvas = RenderElement(_canvas);
        var cardPreview = new CroppedBitmap(cardCanvas, new Int32Rect((int)Canvas.GetLeft(_usageBadge), (int)Canvas.GetTop(_usageBadge),
            (int)Math.Ceiling(_usageBadge.ActualWidth * _settings.UsageBubbleScale), (int)Math.Ceiling(_usageBadge.ActualHeight * _settings.UsageBubbleScale)));
        SaveBitmap(cardPreview, Path.Combine(_store.DirectoryPath, "usage-card-preview.png"));
        SaveBitmap(RenderElement(_canvas), Path.Combine(_store.DirectoryPath, "hover-pet-preview.png"));
        checks["usage_badge_hit"] = _usageBadge.Visibility != Visibility.Visible ||
            NativeDesktop.WindowFromPoint(new NativeDesktop.Point { X = (int)_usageBadge.PointToScreen(new Point(15, 12)).X,
                Y = (int)_usageBadge.PointToScreen(new Point(15, 12)).Y }) == _handle;
        var usagePage = new UsagePage(_usageService, () => _settings);
        var usagePreview = CreateControlPreview(PetControlTab.Usage, usagePage);
        usagePreview.Show(); usagePage.Update(usageSnapshot); usagePreview.UpdateLayout();
        checks["usage_window_loaded"] = usagePreview.IsLoaded && usagePreview.ActualWidth >= 410;
        SaveBitmap(RenderElement(usagePreview), Path.Combine(_store.DirectoryPath, "usage-preview.png"));
        var independentCardSize = _usageBadge.RenderSize;
        foreach (var size in new[] { 160, 240, 360 })
        {
            ApplySettings(original with { Size = size }); _tick.Stop(); UpdateUsage(usageSnapshot); UpdateLayout();
            checks["usage_card_fits_" + size] = _usageValues.All(value => value.DesiredSize.Width <= value.ActualWidth + 1)
                && Math.Abs(Canvas.GetTop(_sprite) + _sprite.VisibleTop - Canvas.GetTop(_usageBadge) - _usageBadge.ActualHeight * _settings.UsageBubbleScale - 12) <= 1;
            checks["usage_card_size_independent_of_character_" + size] = _usageBadge.RenderSize == independentCardSize;
            checks["usage_token_values_centered_" + size] = _usageValues.Skip(2).All(value =>
            {
                var body = (FrameworkElement)VisualTreeHelper.GetParent(value);
                var center = value.TranslatePoint(new Point(0, value.ActualHeight / 2), body).Y;
                return Math.Abs(center - body.ActualHeight / 2) <= 1;
            });
            SaveBitmap(RenderElement(_canvas), Path.Combine(_store.DirectoryPath, "hover-pet-" + size + ".png"));
        }
        ApplySettings(original with { UsageBubbleScale = 1 }); _tick.Stop(); UpdateUsage(usageSnapshot); UpdateLayout();
        var unscaledCardSize = _usageBadge.RenderSize;
        var unscaledCardBounds = _usageBadge.TransformToAncestor(_canvas).TransformBounds(new Rect(unscaledCardSize));
        ApplySettings(original with { UsageBubbleScale = 2 }); _tick.Stop(); UpdateUsage(usageSnapshot); UpdateLayout();
        var scaledCardBounds = _usageBadge.TransformToAncestor(_canvas).TransformBounds(new Rect(_usageBadge.RenderSize));
        checks["usage_card_own_size_control_still_scales_both_axes"] = _usageBadge.RenderSize == unscaledCardSize
            && Math.Abs(scaledCardBounds.Width - unscaledCardBounds.Width * 2) <= 1
            && Math.Abs(scaledCardBounds.Height - unscaledCardBounds.Height * 2) <= 1;
        ApplySettings(original); _tick.Stop(); UpdateUsage(usageSnapshot); UpdateLayout();
        usagePage.Update(new AccountUsageSnapshot { State = UsageConnectionState.SignedOut, Message = "尚未登录，请登录 ChatGPT" });
        usagePreview.UpdateLayout(); SaveBitmap(RenderElement(usagePreview), Path.Combine(_store.DirectoryPath, "usage-signed-out-preview.png"));
        usagePreview.Close();
        SetUsageHover(false);
        var operationMenu = CreateContextMenu(); operationMenu.PlacementTarget = _sprite;
        operationMenu.Placement = PlacementMode.Left; operationMenu.IsOpen = true; operationMenu.UpdateLayout();
        checks["operation_menu_themed"] = operationMenu.Template != null && operationMenu.ActualWidth >= 230;
        SaveBitmap(RenderElement(operationMenu), Path.Combine(_store.DirectoryPath, "menu-preview.png"));
        var actionMenu = operationMenu.Items.OfType<MenuItem>().First(item => (string)item.Header == "动作预览");
        checks["menu_removed_features"] = !operationMenu.Items.OfType<MenuItem>().Any(item =>
            new[] { "成长与玩具…", "一起玩", "唤醒 / 停止休息", "保持待机" }.Contains((string)item.Header));
        checks["menu_only_loaded_actions"] = ActionItems(actionMenu).Select(item => (string)item.Tag)
            .SequenceEqual(_sprite.AvailableActions.OrderBy(action => action.Group).Select(action => action.Key));
        CheckPreviewGroups(checks, actionMenu);
        actionMenu.IsSubmenuOpen = true; actionMenu.UpdateLayout();
        checks["operation_submenu_opens"] = actionMenu.IsSubmenuOpen && actionMenu.Items.Count > 0;
        actionMenu.IsSubmenuOpen = false; operationMenu.IsOpen = false;
        RunDynamicMenuChecks(checks, original);
        File.WriteAllText(Path.Combine(_store.DirectoryPath, "smoke-test.json"), JsonSerializer.Serialize(new
        {
            passed = checks.Values.All(v => v), checks,
            animation = new { skinName = _sprite.SkinName, idleFrames = _sprite.FrameCount(PetState.Idle),
                walkingFrames = _sprite.FrameCount(PetState.Walking), walkingDistinctPoseFrames = _sprite.DistinctFrameCount(PetState.Walking),
                walkingMode = "full-character-animation", walkingSpeedDipPerSecond = _sprite.WalkingSpeedAt(_settings.Size),
                walkingFrameMilliseconds = _sprite.FrameMillisecondsFor(PetState.Walking),
                walkingRenderMilliseconds = _sprite.WalkingRenderMilliseconds,
                walkingRenderSamples = (int)Math.Round(_sprite.FrameCount(PetState.Walking) * _sprite.FrameMillisecondsFor(PetState.Walking) / (double)_sprite.WalkingRenderMilliseconds),
                blinkAvailable = _sprite.HasBlinkFrame },
            dpi = NativeDesktop.GetDpiForWindow(_handle), window = NativeDesktop.WindowRect(_handle), workArea = NativeDesktop.WorkArea(_handle)
        }, SettingsStore.JsonOptions));
        if (checks.Values.Any(v => !v)) Environment.ExitCode = 1;
        // Let menu popups finish closing before tearing down their dispatcher.
        var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        exitTimer.Tick += (_, _) => { exitTimer.Stop(); ExitPet(); };
        exitTimer.Start();
    }

    private static System.Collections.Generic.IEnumerable<MenuItem> ActionItems(MenuItem actions)
        => actions.Items.OfType<MenuItem>().SelectMany(group => group.Items.OfType<MenuItem>());

    private void CheckPreviewGroups(System.Collections.Generic.Dictionary<string, bool> checks, MenuItem actions)
    {
        checks["preview_group_labels"] = actions.Items.OfType<MenuItem>().All(group =>
            (string)group.Header == PreviewGroupLabel((AnimationPreviewGroup)group.Tag));
        checks["preview_group_contents"] = actions.Items.OfType<MenuItem>().All(group =>
            group.Items.OfType<MenuItem>().Select(item => (string)item.Tag).SequenceEqual(
                _sprite.AvailableActions.Where(action => action.Group == (AnimationPreviewGroup)group.Tag).Select(action => action.Key)));
        checks["preview_group_count"] = actions.Items.Count == _sprite.AvailableActions.Select(action => action.Group).Distinct().Count();
    }

    private void RunSegmentedWalkingChecks(System.Collections.Generic.Dictionary<string, bool> checks, PetSettings original)
    {
        if (_sprite.WalkingTiming is not { } timing) return;
        ApplySettings(original with { Size = 240, Paused = false, AutoWalk = false }); _tick.Stop(); SetUsageHover(false);
        foreach (var direction in new[] { 1, -1 })
        {
            _behavior.BeginDrag(); _behavior.EndDrag();
            if (_behavior.Direction != direction) _behavior.Turn();
            UpdateLayout();
            var area = NativeDesktop.WorkArea(_handle); var rect = NativeDesktop.WindowRect(_handle);
            MoveClamped(area.X + (area.Width - rect.Width) / 2, rect.Y, area);
            _behavior.React(PetState.Walking); var playback = _behavior.WalkingPlayback!;
            var prefix = direction == 1 ? "right" : "left";
            var initialX = NativeDesktop.WindowRect(_handle).X;
            for (var i = 0; i < timing.StartFrames; i++) AdvanceFrame(1 / timing.Fps);
            checks["walk_" + prefix + "_start_stays_in_place"] = initialX == NativeDesktop.WindowRect(_handle).X
                && playback.Phase == WalkPhase.Loop && _sprite.CurrentFrameIndex == 0;
            for (var i = 0; i < 8; i++) AdvanceFrame(1 / timing.Fps);
            SaveBitmap(CaptureWalkingSprite(), Path.Combine(_store.DirectoryPath, "walking-" + prefix + ".png"));
            checks["walk_" + prefix + "_frame_clock"] = _sprite.CurrentFrameIndex == playback.FrameIndex && playback.FrameIndex == 8;
            var loopX = NativeDesktop.WindowRect(_handle).X;
            checks["walk_" + prefix + "_direction"] = (loopX - initialX) * direction > 0;
            while (playback.Phase == WalkPhase.Loop) AdvanceFrame(1 / timing.Fps);
            var endX = NativeDesktop.WindowRect(_handle).X;
            for (var i = 0; i < timing.EndMoveFrames; i++) AdvanceFrame(1 / timing.Fps);
            var stoppedX = NativeDesktop.WindowRect(_handle).X;
            var expected = timing.EndMovingSeconds * _sprite.WalkingSpeedAt(240) * DpiScale;
            checks["walk_" + prefix + "_outro_halfstep_moves"] = Math.Abs((stoppedX - endX) * direction - expected) <= 1.1;
            SaveBitmap(CaptureWalkingSprite(), Path.Combine(_store.DirectoryPath, "walking-outro-" + prefix + ".png"));
            while (playback.Playing) AdvanceFrame(1 / timing.Fps);
            checks["walk_" + prefix + "_outro_stands_still_after_halfstep"] = stoppedX == NativeDesktop.WindowRect(_handle).X;
            checks["walk_" + prefix + "_finishes_all_cycles"] = playback.PlannedCycles is >= 3 and <= 6
                && playback.CompletedCycles == playback.PlannedCycles && playback.FrameIndex == timing.EndFrames - 1
                && _behavior.State == PetState.Idle;
        }
        foreach (var direction in new[] { 1, -1 })
        {
            _behavior.BeginDrag(); _behavior.EndDrag();
            if (_behavior.Direction != direction) _behavior.Turn();
            var area = CharacterWorkArea(); var window = NativeDesktop.WindowRect(_handle);
            MoveClamped(area.X + (area.Width - window.Width) / 2, area.Y + (area.Height - window.Height) / 2, area);
            _behavior.React(PetState.Walking); var active = _behavior.WalkingPlayback!;
            AdvanceFrame(.2); PreviewAnimation(nameof(PetState.Walking));
            var manual = _walkingPreview!; var previewDirection = _sprite.Direction;
            var expected = (manual.PlannedCycles * timing.LoopSeconds + timing.EndMovingSeconds) * _sprite.WalkingSpeedAt(240) * DpiScale;
            var pausedTime = active.ElapsedSeconds; var previewX = NativeDesktop.WindowRect(_handle).X;
            while (_walkingPreview is { Playing: true }) AdvanceFrame(.041);
            checks["menu_walk_moves_" + direction] = Math.Abs((NativeDesktop.WindowRect(_handle).X - previewX) * previewDirection - expected) <= 1.1;
            checks["menu_walk_full_sequence_" + direction] = _sprite.PreviewKey is null && manual.CompletedCycles == manual.PlannedCycles
                && manual.FrameIndex == timing.EndFrames - 1 && manual.PlannedCycles is >= 3 and <= 6;
            checks["menu_walk_preserves_background_behavior_" + direction] = active.ElapsedSeconds == pausedTime && !_settings.AutoWalk;
        }
        _behavior.BeginDrag(); _behavior.EndDrag(); ApplySettings(_settings with { Paused = true }); _tick.Stop();
        var start = NativeDesktop.WindowRect(_handle); var screen = CharacterWorkArea();
        MoveClamped(screen.X + (screen.Width - start.Width) / 2, start.Y, screen);
        PreviewAnimation(nameof(PetState.Walking)); var pausedX = NativeDesktop.WindowRect(_handle).X;
        while (_walkingPreview is { Playing: true }) AdvanceFrame(.041);
        checks["menu_walk_runs_once_while_paused_without_changing_settings"] = _settings.Paused && !_settings.AutoWalk
            && Math.Abs(NativeDesktop.WindowRect(_handle).X - pausedX) > 10 && !_tick.IsEnabled;
        foreach (var edge in new[] { -1, 1 })
        {
            _behavior.BeginDrag(); _behavior.EndDrag(); _sprite.Direction = edge;
            var area = CharacterWorkArea(); var window = NativeDesktop.WindowRect(_handle);
            MoveClamped(area.X + edge * 99999, window.Y, area);
            var startX = NativeDesktop.WindowRect(_handle).X;
            PreviewAnimation(nameof(PetState.Walking));
            var selected = _sprite.Direction;
            while (_walkingPreview is { Playing: true }) AdvanceFrame(.041);
            checks["menu_walk_starts_inward_at_edge_" + edge] = selected == -edge
                && (NativeDesktop.WindowRect(_handle).X - startX) * selected > 10;
        }
        _behavior.BeginDrag(); _behavior.EndDrag(); ApplySettings(original); _tick.Stop();
    }

    private void RunCharacterBoundaryChecks(System.Collections.Generic.Dictionary<string, bool> checks, PetSettings original)
    {
        foreach (var size in new[] { 160, 240, 360 })
        {
            ApplySettings(original with { Size = size, AutoWalk = false, Paused = false }); _tick.Stop(); SetUsageHover(false);
            var area = CharacterWorkArea();
            foreach (var xEdge in new[] { -1, 1 })
            foreach (var yEdge in new[] { -1, 1 })
            {
                _behavior.BeginDrag(); _sprite.ResetAnimation();
                MoveClamped(area.X + xEdge * 99999, area.Y + yEdge * 99999, area);
                FinishDrag(); _sprite.ResetAnimation(); ClampCurrent();
                var content = CharacterScreenBounds(); var window = NativeDesktop.WindowRect(_handle);
                var horizontal = xEdge < 0 ? content.X - area.X : area.Right - content.Right;
                var vertical = yEdge < 0 ? content.Y - area.Y : area.Bottom - content.Bottom;
                checks[$"drag_visible_corner_{size}_{xEdge}_{yEdge}"] = Math.Abs(horizontal) <= 1.1 && Math.Abs(vertical) <= 1.1;
                checks[$"drag_padding_outside_{size}_{xEdge}_{yEdge}"] = window.X < area.X || window.Y < area.Y
                    || window.Right > area.Right || window.Bottom > area.Bottom;
            }
        }
        ApplySettings(original); _tick.Stop(); ResetPosition(); _behavior.BeginDrag(); _behavior.EndDrag();
    }

    private BitmapSource CaptureWalkingSprite()
    {
        UpdateLayout();
        return new CroppedBitmap(RenderElement(_canvas), new Int32Rect((int)Math.Round(Canvas.GetLeft(_sprite)),
            (int)Math.Round(Canvas.GetTop(_sprite)), (int)Math.Round(_sprite.ActualWidth), (int)Math.Round(_sprite.ActualHeight)));
    }

    private void CapturePreviewSubmenus(MenuItem actions, System.Collections.Generic.Dictionary<string, bool> checks)
    {
        actions.ApplyTemplate();
        if (actions.Template.FindName("PART_Popup", actions) is Popup parentPopup) parentPopup.PopupAnimation = PopupAnimation.None;
        actions.IsSubmenuOpen = true; actions.UpdateLayout();
        foreach (var group in actions.Items.OfType<MenuItem>())
        {
            group.ApplyTemplate();
            if (group.Template.FindName("PART_Popup", group) is Popup popup)
            {
                popup.PopupAnimation = PopupAnimation.None;
                group.IsSubmenuOpen = true; group.UpdateLayout();
                if (popup.Child is FrameworkElement child)
                {
                    child.UpdateLayout();
                    checks["submenu_open_" + group.Tag] = popup.IsOpen && child.ActualWidth > 0 && child.ActualHeight > 0;
                    if (child.ActualWidth > 0 && child.ActualHeight > 0)
                        SaveBitmap(RenderElement(child), Path.Combine(_store.DirectoryPath, "submenu-" + group.Tag + ".png"));
                }
                else checks["submenu_open_" + group.Tag] = false;
                group.IsSubmenuOpen = false;
            }
            else checks["submenu_open_" + group.Tag] = false;
        }
        actions.IsSubmenuOpen = false;
    }

    private void RunDynamicMenuChecks(System.Collections.Generic.Dictionary<string, bool> checks, PetSettings original)
    {
        var fixture = Path.Combine(_store.DirectoryPath, "animation-menu-fixture"); Directory.CreateDirectory(fixture);
        SaveBitmap(RenderSprite(_sprite), Path.Combine(fixture, "idle.png"));
        var pixels = Enumerable.Repeat((byte)0, 32 * 32 * 4).ToArray();
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i + 1] = 170; pixels[i + 2] = 70; pixels[i + 3] = 255; }
        SaveBitmap(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4), Path.Combine(fixture, "new.png"));
        var definition = new SkinDefinition { Name = "菜单动态更新测试", Clips = new() { ["Idle"] = new() { Frames = ["idle.png"] } } };
        var manifest = Path.Combine(fixture, "skin.json");
        void Save() { File.WriteAllText(manifest, JsonSerializer.Serialize(definition)); File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow.AddSeconds(1)); }
        MenuItem Actions(ContextMenu menu) => menu.Items.OfType<MenuItem>().First(item => (string)item.Header == "动作预览");
        Save(); ApplySettings(original with { SkinDirectory = fixture, AutoWalk = false }); _tick.Stop();
        var idleMenu = CreateContextMenu();
        checks["idle_only_menu"] = Actions(idleMenu).Items.Count == 1 && (string)((MenuItem)Actions(idleMenu).Items[0]).Header == "待机"
            && ActionItems(Actions(idleMenu)).Single().Tag is "Idle";
        checks["walk_switch_only_in_settings"] = !idleMenu.Items.OfType<MenuItem>().Any(item => (string)item.Header == "自动走动");
        ToggleAutoWalk(); _tick.Stop();
        var moveArea = NativeDesktop.WorkArea(_handle);
        MoveClamped(moveArea.X + moveArea.Width / 2, NativeDesktop.WindowRect(_handle).Y, moveArea);
        var beforeMove = NativeDesktop.WindowRect(_handle);
        AdvanceFrame(.2); UpdateLayout();
        var afterMove = NativeDesktop.WindowRect(_handle);
        checks["idle_skin_moves_with_existing_clip"] = _settings.AutoWalk && _behavior.State == PetState.Walking && beforeMove.X != afterMove.X && _sprite.GetFrameCount(PetState.Walking) == 0 && _sprite.UsesImageFrames;
        ToggleAutoWalk(); _tick.Stop();
        definition.Clips["PlayBall"] = new() { DisplayName = "玩球", Frames = ["new.png"], Loop = false };
        Save(); var updatedMenu = CreateContextMenu(); _tick.Stop();
        checks["new_action_added_without_restart"] = ActionItems(Actions(updatedMenu)).Any(item => (string)item.Header == "玩球" && (string)item.Tag == "PlayBall");
        var idlePixels = BitmapPixels(RenderSprite(_sprite));
        var play = ActionItems(Actions(updatedMenu)).First(item => (string)item.Tag == "PlayBall");
        play.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); _tick.Stop();
        _sprite.Advance(.25); // Check the selected action after the transition has settled.
        checks["custom_action_plays_own_frames"] = _sprite.PreviewKey == "PlayBall" && !idlePixels.SequenceEqual(BitmapPixels(RenderSprite(_sprite)));
        StopPreview(); _tick.Stop();
        checks["preview_restores_behavior_mode"] = _sprite.PreviewKey is null && !_settings.AutoWalk && _sprite.State == _behavior.State;
        definition.Clips["Happy"] = new() { Frames = ["idle.png"], Loop = false, PreviewGroup = AnimationPreviewGroup.ClickInteraction };
        Save(); CreateContextMenu(); _tick.Stop();
        var clicked = new System.Collections.Generic.HashSet<string>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            ClickReaction(); if (_sprite.PreviewKey is { } chosen) clicked.Add(chosen);
            StopPreview(); _tick.Stop();
        }
        checks["random_click_includes_custom_actions"] = clicked.SetEquals(new[] { "Happy", "PlayBall" });
        definition.Clips.Remove("Happy");
        definition.Clips["Flying"] = new() { Frames = ["new.png"], PreviewGroup = AnimationPreviewGroup.Walking };
        Save(); CreateContextMenu(); _tick.Stop();
        _behavior.BeginDrag(); _behavior.EndDrag(); _behavior.React(PetState.Walking, 3); AdvanceFrame(.2);
        checks["generic_movement_uses_registered_frames"] = _behavior.SelectedMovementKey == "Flying"
            && _sprite.AutomaticMovementKey == "Flying" && _sprite.CurrentFrameIndex == 0 && _behavior.WalkingPlayback is null;
        _behavior.BeginDrag(); _behavior.EndDrag(); definition.Clips.Remove("Flying");
        definition.Clips["Walking"] = new() { Frames = ["new.png"] };
        Save(); CreateContextMenu(); _tick.Stop();
        ToggleAutoWalk(); _tick.Stop();
        checks["auto_walk_starts_immediately"] = _settings.AutoWalk && _behavior.AutoWalk && _behavior.State == PetState.Walking;
        ToggleAutoWalk(); _tick.Stop();
        checks["auto_walk_can_be_stopped"] = !_settings.AutoWalk && !_behavior.AutoWalk && _behavior.State == PetState.Idle;
        definition.Clips.Remove("PlayBall"); Save();
        checks["removed_action_disappears"] = !ActionItems(Actions(CreateContextMenu())).Any(item => (string)item.Tag == "PlayBall");
        ApplySettings(original); _tick.Stop();
    }

    private void RunMenuSmokeTest()
    {
        var checks = new System.Collections.Generic.Dictionary<string, bool>();
        RunMenuDismissChecks(checks);
        RunStructureChecks(checks);
        var original = _settings; _tick.Stop();
        var menu = CreateContextMenu(); menu.PlacementTarget = _sprite; menu.Placement = PlacementMode.Left;
        menu.IsOpen = true; menu.UpdateLayout();
        checks["menu_no_removed_features"] = !menu.Items.OfType<MenuItem>().Any(item =>
            new[] { "成长与玩具…", "一起玩", "唤醒 / 停止休息", "保持待机" }.Contains((string)item.Header));
        checks["growth_entry_preserved"] = menu.Items.OfType<MenuItem>().Any(item => (string)item.Header == "成长详情…");
        var actions = menu.Items.OfType<MenuItem>().First(item => (string)item.Header == "动作预览");
        checks["menu_exact_loaded_actions"] = ActionItems(actions).Select(item => (string)item.Tag)
            .SequenceEqual(_sprite.AvailableActions.OrderBy(action => action.Group).Select(action => action.Key));
        CheckPreviewGroups(checks, actions);
        checks["walk_switch_only_in_settings"] = !menu.Items.OfType<MenuItem>().Any(item => (string)item.Header == "自动走动");
        checks["skin_loaded"] = _sprite.LoadWarning is null && _sprite.AvailableActions.Length > 0;
        var actionNames = _sprite.AvailableActions.Select(a => new { a.Key, a.Label, a.Group }).ToArray();
        SaveBitmap(RenderElement(menu), Path.Combine(_store.DirectoryPath, "menu-preview.png"));
        CapturePreviewSubmenus(actions, checks); menu.IsOpen = false;
        RunDynamicMenuChecks(checks, original); UpdateLayout();
        RunGrowthUiChecks(checks);
        checks["original_skin_restored"] = _settings.SkinDirectory == original.SkinDirectory && _sprite.AvailableActions.Select(a => a.Key).SequenceEqual(actionNames.Select(a => a.Key));
        File.WriteAllText(Path.Combine(_store.DirectoryPath, "menu-test.json"), JsonSerializer.Serialize(new
            { passed = checks.Values.All(v => v), checks, skin = _sprite.SkinName, actionNames }, SettingsStore.JsonOptions));
        if (checks.Values.Any(v => !v)) Environment.ExitCode = 1;
        var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        exitTimer.Tick += (_, _) => { exitTimer.Stop(); ExitPet(); }; exitTimer.Start();
    }

    private void RunMenuDismissChecks(System.Collections.Generic.Dictionary<string, bool> checks)
    {
        var menu = new ContextMenu { PlacementTarget = _sprite, Placement = PlacementMode.Left };
        PetTheme.Apply(menu);
        var branch = new MenuItem { Header = "菜单测试" };
        var nested = new MenuItem { Header = "二级菜单" };
        nested.Items.Add(new MenuItem { Header = "选项" }); branch.Items.Add(nested); menu.Items.Add(branch);
        var dismiss = new MenuAutoDismiss(menu);
        menu.IsOpen = true; menu.UpdateLayout();
        var inside = menu.PointToScreen(new Point(10, 10));
        dismiss.UpdatePointer(inside, 1);
        checks["menu_pointer_inside_keeps_open"] = menu.IsOpen;
        branch.IsSubmenuOpen = true; branch.UpdateLayout();
        if (branch.Template.FindName("PART_Popup", branch) is Popup { Child: FrameworkElement child })
        {
            child.UpdateLayout();
            var submenuPoint = child.PointToScreen(new Point(10, 10));
            dismiss.UpdatePointer(submenuPoint, 2); dismiss.UpdatePointer(submenuPoint, 3);
            checks["menu_pointer_in_submenu_keeps_open"] = menu.IsOpen && MenuAutoDismiss.ContainsPointer(menu, submenuPoint);
            nested.IsSubmenuOpen = true; nested.UpdateLayout();
            if (nested.Template.FindName("PART_Popup", nested) is Popup { Child: FrameworkElement grandchild })
            {
                grandchild.UpdateLayout(); var deeperPoint = grandchild.PointToScreen(new Point(10, 10));
                dismiss.UpdatePointer(deeperPoint, 4); dismiss.UpdatePointer(deeperPoint, 5);
                checks["menu_pointer_in_nested_submenu_keeps_open"] = menu.IsOpen && MenuAutoDismiss.ContainsPointer(menu, deeperPoint);
            }
            else checks["menu_pointer_in_nested_submenu_keeps_open"] = false;
        }
        else checks["menu_pointer_in_submenu_keeps_open"] = false;
        var outside = new Point(-100000, -100000);
        dismiss.UpdatePointer(outside, 6); dismiss.UpdatePointer(outside, 6.1);
        checks["menu_crossing_grace"] = menu.IsOpen;
        dismiss.UpdatePointer(inside, 6.2); dismiss.UpdatePointer(inside, 7);
        checks["menu_return_cancels_close"] = menu.IsOpen;
        dismiss.UpdatePointer(outside, 8); dismiss.UpdatePointer(outside, 8.3);
        checks["menu_leaving_closes_without_click"] = !menu.IsOpen;
        // Let the popup's asynchronous close complete before simulating a new
        // user opening. Reopening in the same dispatcher callback races WPF.
        var closeFrame = new DispatcherFrame();
        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); closeFrame.Continue = false; };
        closeTimer.Start(); Dispatcher.PushFrame(closeFrame);
        menu.IsOpen = true; menu.UpdateLayout();
        dismiss.UpdatePointer(menu.PointToScreen(new Point(10, 10)), 9);
        checks["menu_reopen_resets_dismissal"] = menu.IsOpen;
        menu.IsOpen = false;
    }

    private void RunGrowthUiChecks(System.Collections.Generic.Dictionary<string, bool> checks)
    {
        var directory = Path.Combine(_store.DirectoryPath, "growth-ui-fixture"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "growth.json"), JsonSerializer.Serialize(new GrowthSave { TotalXp = GrowthRules.TotalTo(20) + 23, Badges = new() { "陪伴 7 天" } }));
        var engine = new GrowthEngine(directory); var page = new GrowthPage(engine);
        var window = CreateControlPreview(PetControlTab.Growth, page);
        window.Show(); window.UpdateLayout();
        checks["growth_levels_preserved"] = page.Displayed is { Level: 20, LevelXp: 23 };
        System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
        var children = Descendants(window).ToArray(); var buttons = children.OfType<Button>().ToArray();
        checks["growth_no_toy_unlock_ui"] = !children.OfType<TextBlock>().Any(text => text.Text.Contains("解锁") || text.Text.Contains("玩具")) &&
            !buttons.Any(button => (button.Content as string)?.Contains("兑换") == true);
        buttons.First(button => (button.Content as string) == "25 分钟").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["growth_focus_preserved"] = page.Displayed?.FocusSeconds == 1500;
        buttons.First(button => (button.Content as string) == "结束专注").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        checks["growth_focus_stops"] = page.Displayed?.FocusSeconds == 0;
        window.UpdateLayout(); SaveBitmap(RenderElement(window), Path.Combine(_store.DirectoryPath, "growth-preview.png")); window.Close();
    }

    private static RenderTargetBitmap RenderSprite(SpriteView sprite)
    {
        sprite.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)sprite.ActualWidth, (int)sprite.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        // Draw the current frame immediately, excluding the parent's layout offset and pending render passes.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) sprite.DrawFrame(dc);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private static RenderTargetBitmap RenderElement(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze(); return bitmap;
    }

    private static byte[] BitmapPixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }

    private void CreateAnimationPreview(System.Collections.Generic.Dictionary<string, bool> checks)
    {
        // Verify only actions present in the retained skin; historical walk/rest packs are no longer bundled.
        using var sprite = new SpriteView();
        sprite.LoadSkin(_settings.SkinDirectory);
        checks["bundled_character_loaded"] = sprite.UsesImageFrames && sprite.LoadWarning == null;
        checks["built_in_uses_webm"] = sprite.UsesWebm;
        checks["idle_frames_available"] = sprite.FrameCount(PetState.Idle) > 0;
        foreach (var size in new[] { 160, 240, 360 })
        {
            var viewport = sprite.ViewportSizeAt(size);
            sprite.Width = viewport; sprite.Height = viewport;
            sprite.Measure(new Size(viewport, viewport)); sprite.Arrange(new Rect(0, 0, viewport, viewport));
            foreach (var action in sprite.AvailableActions)
            {
                sprite.BeginPreview(action.Key);
                if (sprite.UsesStandingHeight)
                {
                    sprite.ResetAnimation();
                    var reference = RenderSprite(sprite);
                    var pixels = BitmapPixels(reference);
                    var rows = Enumerable.Range(0, reference.PixelHeight).Where(y =>
                        Enumerable.Range(0, reference.PixelWidth).Any(x => pixels[(y * reference.PixelWidth + x) * 4 + 3] >= 64)).ToArray();
                    var expectedHeight = size * sprite.CurrentAlignmentScale;
                    checks[$"standing_height_{action.Key}_{size}"] = rows.Length > 0
                        && Math.Abs(rows.Last() - rows.First() + 1 - expectedHeight) <= 3;
                    SaveBitmap(reference, Path.Combine(_store.DirectoryPath, $"standing-{action.Key}-{size}.png"));
                }
                foreach (var fraction in new[] { 0d, 0.5, 0.99 })
                foreach (var direction in new[] { -1, 1 })
                {
                    sprite.ResetAnimation(); sprite.Direction = direction;
                    sprite.Advance(sprite.PreviewSeconds(action.Key) * fraction);
                    var bitmap = RenderSprite(sprite);
                    var key = $"{action.Key}_{size}_{fraction}_{direction}";
                    checks["margin_" + key] = RenderHasMargin(bitmap);
                    checks["alpha_hit_" + key] = AlphaMatchesRender(sprite, bitmap);
                }
                sprite.EndPreview();
            }
        }
        sprite.State = PetState.Idle; sprite.Direction = 1; sprite.ResetAnimation();
        var first = BitmapPixels(RenderSprite(sprite));
        sprite.Advance(0.8);
        checks["animation_time_advances"] = Math.Abs(sprite.AnimationTime - 0.8) < 0.0001;
        checks["idle_motion_changes_pixels"] = sprite.FrameCount(PetState.Idle) == 1 || !first.SequenceEqual(BitmapPixels(RenderSprite(sprite)));
        SaveBitmap(RenderSprite(sprite), Path.Combine(_store.DirectoryPath, "animation-preview.png"));
    }

    private static bool RenderHasMargin(BitmapSource bitmap)
    {
        var pixels = BitmapPixels(bitmap); var stride = bitmap.PixelWidth * 4;
        for (var x = 0; x < bitmap.PixelWidth; x++)
            if (pixels[x * 4 + 3] >= 12 || pixels[(bitmap.PixelHeight - 1) * stride + x * 4 + 3] >= 12) return false;
        for (var y = 0; y < bitmap.PixelHeight; y++)
            if (pixels[y * stride + 3] >= 12 || pixels[y * stride + (bitmap.PixelWidth - 1) * 4 + 3] >= 12) return false;
        return true;
    }

    private static bool AlphaMatchesRender(SpriteView sprite, BitmapSource bitmap)
    {
        var pixels = BitmapPixels(bitmap); var stride = bitmap.PixelWidth * 4; var tested = 0;
        for (var y = 3; y < bitmap.PixelHeight - 3; y += 7)
            for (var x = 3; x < bitmap.PixelWidth - 3; x += 7)
            {
                var alpha = pixels[y * stride + x * 4 + 3];
                if (alpha >= 200)
                {
                    tested++; if (!sprite.IsOpaqueHit(new Point(x + 0.5, y + 0.5))) return false;
                }
                else if (alpha == 0)
                {
                    var clear = true;
                    for (var dy = -2; dy <= 2 && clear; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                            if (pixels[(y + dy) * stride + (x + dx) * 4 + 3] != 0) { clear = false; break; }
                    if (clear) { tested++; if (sprite.IsOpaqueHit(new Point(x + 0.5, y + 0.5))) return false; }
                }
            }
        return tested > 10;
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
