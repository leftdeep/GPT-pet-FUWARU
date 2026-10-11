using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunSleepingChecks(Dictionary<string, bool> checks)
    {
        if (!_sprite.HasAnimation(PetState.Sleeping) || !_sprite.HasAnimation(PetState.LyingDown) || !_sprite.HasAnimation(PetState.Waking)) return;
        var original = _settings;
        ApplySettings(original with { Paused = false, AutoWalk = false }); _tick.Stop();
        _behavior.BeginDrag(); _behavior.EndDrag(); StopPreview();
        _behavior.LieDownSeconds = _sprite.ClipSeconds(PetState.LyingDown);
        _behavior.WakeUpSeconds = _sprite.ClipSeconds(PetState.Waking);
        void Step(double seconds)
        {
            while (seconds > 1e-8) { var delta = Math.Min(1d / 24, seconds); AdvanceFrame(delta); seconds -= delta; }
        }
        try
        {
            var commands = CreateMenuCommands().First(command => command.Label == "动作预览").Children!;
            var idle = commands.First(command => Equals(command.Tag, AnimationPreviewGroup.Idle)).Children!;
            checks["sleep_menu_under_idle"] = idle.Count(command => Equals(command.Tag, nameof(PetState.Sleeping))) == 1;
            checks["sleep_internal_segments_hidden"] = !_sprite.AvailableActions.Any(action => action.State is PetState.LyingDown or PetState.Waking);
            checks["sleep_excluded_from_random_clicks"] = !_sprite.AvailableActions.Any(action => action.State == PetState.Sleeping && action.Group == AnimationPreviewGroup.ClickInteraction);
            // Keep the full source diagnostic separate from the user's looping sleep command.
            _sprite.BeginPreview(nameof(PetState.Sleeping)); _tick.Stop();
            var duration = _sprite.PreviewSeconds(nameof(PetState.Sleeping));
            var frames = new HashSet<(PetState?, int)>();
            for (var frame = 0; frame < 240; frame++)
            {
                frames.Add((_sprite.RestPreviewPhase, _sprite.CurrentFrameIndex));
                if (frame is 12 or 100 or 210)
                    SaveBitmap(RenderSprite(_sprite), Path.Combine(_store.DirectoryPath, $"sleep-preview-{frame}.png"));
                if (frame < 239) AdvanceFrame(1d / 24);
            }
            checks["sleep_source_preserves_all_240_frames"] = frames.Count == 240 && Math.Abs(duration - 10) < .001;
            checks["sleep_source_ends_with_waking_last_frame"] = _sprite.RestPreviewPhase == PetState.Waking && _sprite.CurrentFrameIndex == _sprite.FrameCount(PetState.Waking) - 1;
            StopPreview(); RegisterPetInteraction();
            AdvanceFrame(599.75);
            checks["sleep_native_before_ten_minutes"] = _behavior.State == PetState.Idle;
            AdvanceFrame(.25);
            checks["sleep_native_ten_minute_trigger"] = _behavior.State == PetState.LyingDown && _sprite.State == PetState.LyingDown;
            Step(_behavior.LieDownSeconds - .25);
            checks["sleep_native_lies_down_before_loop"] = _behavior.State == PetState.Sleeping && _sprite.State == PetState.Sleeping;
            var sleepCycle = _sprite.ClipSeconds(PetState.Sleeping);
            Step(sleepCycle * 2);
            checks["sleep_native_persistent_loop"] = _behavior.State == PetState.Sleeping && _sprite.PreviewKey is null;
            var sleepingRender = RenderSprite(_sprite);
            checks["sleep_loop_alpha_and_hit"] = RenderHasMargin(sleepingRender) && AlphaMatchesRender(_sprite, sleepingRender);
            SaveBitmap(RenderSprite(_sprite), Path.Combine(_store.DirectoryPath, "sleep-automatic.png"));
            ClickReaction(); _tick.Stop();
            checks["sleep_click_waits_for_waking"] = _behavior.State == PetState.Waking && _sprite.State == PetState.Waking
                && _sprite.PreviewKey is null && _pendingPreviewKey is not null && _behavior.InactiveSeconds == 0;
            var selected = _pendingPreviewKey;
            Step(_behavior.WakeUpSeconds);
            checks["sleep_click_runs_after_waking"] = _sprite.PreviewKey == selected && _pendingPreviewKey is null;
            StopPreview();
            checks["sleep_click_returns_to_awake_idle"] = _behavior.State == PetState.Idle;
            RegisterPetInteraction(); AdvanceFrame(450);
            _tick.Stop();
            var menu = CreateContextMenu();
            try
            {
                var opened = false; var interactionReset = false;
                var openingFrame = new System.Windows.Threading.DispatcherFrame();
                menu.Opened += (_, _) =>
                {
                    // Observe the command in its own event, before a later timer tick.
                    opened = true; interactionReset = _behavior.InactiveSeconds == 0;
                    openingFrame.Continue = false;
                };
                menu.IsOpen = true;
                var openingDeadline = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                openingDeadline.Tick += (_, _) => openingFrame.Continue = false;
                try { openingDeadline.Start(); if (!opened) System.Windows.Threading.Dispatcher.PushFrame(openingFrame); }
                finally { openingDeadline.Stop(); }
                checks["sleep_menu_open_resets_inactivity"] = opened && interactionReset;
            }
            finally { menu.IsOpen = false; }
            RegisterPetInteraction(); Step(12);
            checks["sleep_clock_continues_after_interaction"] = Math.Abs(_behavior.InactiveSeconds - 12) < .001;
            RegisterPetInteraction(); _pointerDown = true;
            try
            {
                AdvanceFrame(600);
                checks["sleep_held_mouse_prevents_timeout"] = _behavior.InactiveSeconds == 0 && _behavior.State == PetState.Idle;
            }
            finally { _pointerDown = false; }
            PreviewAnimation(nameof(PetState.Sleeping)); _tick.Stop();
            checks["sleep_menu_starts_lie_down"] = _behavior.State == PetState.LyingDown && _sprite.PreviewKey is null && !_previewTimer.IsEnabled;
            Step(_behavior.LieDownSeconds);
            checks["sleep_menu_enters_loop"] = _behavior.State == PetState.Sleeping && _behavior.SleepElapsedSeconds == 0;
            AdvanceFrame(599.75);
            checks["sleep_menu_loops_before_limit"] = _behavior.State == PetState.Sleeping;
            AdvanceFrame(.25);
            checks["sleep_menu_wakes_at_ten_minutes"] = _behavior.State == PetState.Waking && _sprite.State == PetState.Waking && _pendingPreviewKey is null;
            Step(_behavior.WakeUpSeconds - .25); Step(1);
            checks["sleep_menu_returns_to_idle_after_limit"] = _behavior.State == PetState.Idle && _behavior.InactiveSeconds < 10;
        }
        finally
        {
            _pendingPreviewKey = null; StopPreview(); _behavior.BeginDrag(); _behavior.EndDrag();
            ApplySettings(original); _tick.Stop();
        }
    }
}
