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
    internal void WriteRuntimeStatus()
    {
        var rect = NativeDesktop.WindowRect(_handle);
        var hoverPoint = _sprite.PointToScreen(FindOpaquePoint(_sprite) ?? new Point(_sprite.ActualWidth / 2, _sprite.ActualHeight / 2));
        var status = new
        {
            generatedAt = DateTimeOffset.Now,
            executable = Environment.ProcessPath,
            version = typeof(PetWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion,
            windowVisible = NativeDesktop.IsWindowVisible(_handle),
            topmostConfigured = _settings.Topmost,
            topmostNative = NativeDesktop.IsTopmost(_handle),
            windowMinimized = NativeDesktop.IsIconic(_handle),
            windowCloaked = NativeDesktop.IsCloaked(_handle),
            topmostGuard = new { hooks = _topmostGuard?.HookCount ?? 0, running = _topmostGuard?.WatchdogRunning == true,
                pending = _topmostGuard?.RestorePending == true, restores = _topmostGuard?.RestoreCount ?? 0 },
            position = new { rect.X, rect.Y, rect.Width, rect.Height },
            skin = _sprite.SkinName,
            state = _sprite.State.ToString(),
            edgePeek = _peekEdge.ToString(),
            automaticAnimationTimerRunning = _tick.IsEnabled,
            previewAction = _sprite.PreviewKey,
            characterBounds = CharacterScreenBounds(),
            configuredSize = _settings.Size,
            peekHeadHeight = _peekEdge == ScreenEdge.None ? (double?)null : _sprite.PeekHeadHeight,
            idleFrames = _sprite.FrameCount(PetState.Idle),
            smoothTransitions = _sprite.HasSmoothTransitions,
            idleEntryFrame = _sprite.IdleEntryFrame,
            actions = _sprite.AvailableActions.Select(action => new { action.Key, action.Label, action.Group }),
            lyingDownFrames = _sprite.FrameCount(PetState.LyingDown),
            sleepingFrames = _sprite.FrameCount(PetState.Sleeping),
            wakingFrames = _sprite.FrameCount(PetState.Waking),
            lyingDownSeconds = _behavior.LieDownSeconds,
            wakeUpSeconds = _behavior.WakeUpSeconds,
            inactiveSeconds = _behavior.InactiveSeconds,
            autoSleepAfterSeconds = PetBehavior.AutoSleepAfterSeconds,
            maximumSleepSeconds = PetBehavior.MaximumSleepSeconds,
            sleepElapsedSeconds = _behavior.SleepElapsedSeconds,
            decodedCacheBytes = _sprite.DecodedCacheBytes,
            animationDecoders = WebmFrameStore.ActiveDecoderCount,
            animationFrameBufferBytes = WebmFrameStore.TotalCachedBytes,
            animationRawDiskCacheEnabled = false,
            walkingFrames = _sprite.FrameCount(PetState.Walking),
            walkingFrameMilliseconds = _sprite.FrameMillisecondsFor(PetState.Walking),
            walkingPhase = (_walkingPreview ?? _behavior.WalkingPlayback)?.Phase.ToString(),
            walkingCycles = (_walkingPreview ?? _behavior.WalkingPlayback)?.PlannedCycles,
            walkingFrame = _sprite.CurrentFrameIndex,
            walkingEndMoveFrames = _sprite.WalkingTiming?.EndMoveFrames,
            usageEnabled = _settings.UsageEnabled,
            resetWatch = _resetService.Snapshot,
            resetAlertVisible = _resetAlert?.IsVisible == true,
            usageCardMode = "hover",
            usageCardEnabled = _settings.UsageCardVisible,
            usageHoverTimerRunning = _hoverTimer.IsEnabled,
            usageCardVisible = _usageBadge.IsVisible,
            hoverTarget = new { hoverPoint.X, hoverPoint.Y },
            usageCardTarget = new { X = _usageBadge.PointToScreen(new Point(20, 20)).X, Y = _usageBadge.PointToScreen(new Point(20, 20)).Y },
            controlWindowVisible = _controlWindow?.IsVisible == true,
            controlWindowTab = _controlWindow?.SelectedTab.ToString(),
            usageWindowVisible = _controlWindow?.IsVisible == true && _controlWindow.SelectedTab == PetControlTab.Usage,
            usageCardText = string.Join(" · ", _usageValues.Select(value => value.Text)),
            outputRateText = _outputRateValue.Text,
            outputRateSample = _usageService.Snapshot.CurrentTask?.OutputRate,
            autoWalk = _settings.AutoWalk,
            usageConnectionState = _usageService.Snapshot.State.ToString(),
            usageConnectionMessage = _usageService.Snapshot.Message,
            localUsageAvailable = _usageService.Snapshot.LocalUsage?.Available,
            localUsageUpdatedAt = _usageService.Snapshot.LocalUsage?.UpdatedAt,
            quotaUpdatedAt = _usageService.Snapshot.LimitsUpdatedAt,
            windowsUser = Environment.UserName,
            availableActions = _sprite.AvailableActions.Select(a => new { a.Key, a.Label }).ToArray(),
            growth = _growth.Snapshot(DateTimeOffset.Now),
            growthTimerRunning = _growthTimer.IsEnabled,
            paused = _settings.Paused
        };
        Directory.CreateDirectory(_store.DirectoryPath);
        var statusPath = Path.Combine(_store.DirectoryPath, "runtime-status.json");
        var temporary = statusPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(status, SettingsStore.JsonOptions));
        File.Move(temporary, statusPath, overwrite: true);
    }
    private static Point? FindOpaquePoint(SpriteView sprite)
    {
        var candidates = from y in Enumerable.Range(1, 24)
                         from x in Enumerable.Range(1, 24)
                         let point = new Point(sprite.ActualWidth * x / 25, sprite.ActualHeight * y / 25)
                         orderby Math.Pow(point.X - sprite.ActualWidth / 2, 2) + Math.Pow(point.Y - sprite.ActualHeight / 2, 2)
                         select point;
        foreach (var point in candidates)
        {
            if (sprite.IsOpaqueHit(point) && sprite.IsOpaqueHit(point + new Vector(2, 0))
                && sprite.IsOpaqueHit(point + new Vector(-2, 0)) && sprite.IsOpaqueHit(point + new Vector(0, 2))
                && sprite.IsOpaqueHit(point + new Vector(0, -2))) return point;
        }
        return null;
    }

}
