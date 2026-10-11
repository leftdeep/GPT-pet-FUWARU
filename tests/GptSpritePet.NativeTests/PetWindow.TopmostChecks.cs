using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunTopmostChecks(Dictionary<string, bool> checks)
    {
        var original = _settings;
        ApplySettings(original with { Topmost = true, Paused = true, AutoWalk = false }); _tick.Stop();
        var guard = _topmostGuard!;
        WaitTopmost(() => !guard.RestorePending, 1800);
        checks["topmost_guard_hooks_registered"] = guard.HookCount == 4 && guard.WatchdogRunning;
        var before = NativeDesktop.WindowRect(_handle);
        var foreground = NativeDesktop.GetForegroundWindow();
        DemotePet();
        checks["topmost_reproduces_native_flag_loss_with_wpf_true"] = Topmost && !NativeDesktop.IsTopmost(_handle);
        checks["topmost_native_message_recovers_layer"] = WaitTopmost(() => NativeDesktop.IsTopmost(_handle), 1500);
        checks["topmost_recovery_does_not_move_resize"] = NativeDesktop.WindowRect(_handle) == before;
        checks["topmost_recovery_does_not_activate"] = NativeDesktop.GetForegroundWindow() == foreground;
        WaitTopmost(() => !guard.RestorePending, 1800);

        // Simulate a shell Z-order update that leaves WS_EX_TOPMOST intact but covers the character.
        UpdateLayout(); PumpResetUi();
        var solid = _sprite.PointToScreen(FindOpaquePoint(_sprite) ?? new Point(_sprite.ActualWidth / 2, _sprite.ActualHeight / 2));
        var point = new NativeDesktop.Point { X = (int)solid.X, Y = (int)solid.Y };
        var cover = new Window { Width = 80, Height = 80, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White, ShowActivated = false, ShowInTaskbar = false, Topmost = true };
        cover.Show(); var coverHandle = new WindowInteropHelper(cover).Handle;
        NativeDesktop.ApplyStyles(coverHandle, false); NativeDesktop.Move(coverHandle, solid.X - 20, solid.Y - 20);
        cover.UpdateLayout(); PumpResetUi();
        NativeDesktop.RaiseTopmost(coverHandle);
        checks["topmost_reproduces_cover_with_topmost_flag_intact"] = NativeDesktop.IsTopmost(_handle) && NativeDesktop.WindowFromPoint(point) == coverHandle;
        guard.RequestRestore();
        checks["topmost_shell_request_recovers_above_cover"] = WaitTopmost(() => NativeDesktop.WindowFromPoint(point) == _handle, 1800);
        checks["topmost_cover_recovery_preserves_foreground"] = NativeDesktop.GetForegroundWindow() == foreground;
        using (var probe = new WindowTopmostGuard(cover, coverHandle, () => true))
        {
            probe.Synchronize(); probe.Dispose(); probe.RequestRestore();
            checks["topmost_dispose_removes_hooks_and_timers"] = probe.HookCount == 0 && !probe.WatchdogRunning && !probe.RestorePending;
        }
        cover.Close();
        WaitTopmost(() => !guard.RestorePending, 1800);

        // A shell minimize/hide must be recovered even though WPF is logically visible.
        SaveNow(); var savedBeforeMinimize = _store.Load();
        NativeDesktop.ShowWindow(_handle, 6); // SW_MINIMIZE
        checks["topmost_shell_minimize_reproduced"] = NativeDesktop.IsIconic(_handle);
        SaveNow(); var savedWhileMinimized = _store.Load();
        checks["topmost_minimize_does_not_save_sentinel_position"] = savedWhileMinimized.X == savedBeforeMinimize.X && savedWhileMinimized.Y == savedBeforeMinimize.Y;
        checks["topmost_shell_minimize_restored"] = WaitTopmost(() => !NativeDesktop.IsIconic(_handle) && NativeDesktop.IsWindowVisible(_handle), 1800);
        checks["topmost_minimize_restores_original_bounds"] = NativeDesktop.WindowRect(_handle) == before;
        checks["topmost_minimize_recovery_preserves_foreground"] = NativeDesktop.GetForegroundWindow() == foreground;
        NativeDesktop.ShowWindow(_handle, 0); // SW_HIDE
        checks["topmost_shell_hide_reproduced"] = !NativeDesktop.IsWindowVisible(_handle) && IsVisible;
        checks["topmost_shell_hide_restored"] = WaitTopmost(() => NativeDesktop.IsWindowVisible(_handle), 1800);
        WaitTopmost(() => !guard.RestorePending, 1800);

        // Drop the native notification path: the two-second fallback still repairs a lost flag.
        _source!.RemoveHook(WindowMessage);
        try
        {
            DemotePet();
            checks["topmost_watchdog_recovers_without_window_notification"] = WaitTopmost(() => NativeDesktop.IsTopmost(_handle), 2500);
        }
        finally { _source.AddHook(WindowMessage); }
        var idleRestores = guard.RestoreCount;
        WaitTopmost(() => false, 2300);
        checks["topmost_idle_watchdog_does_not_keep_raising"] = guard.RestoreCount == idleRestores && !guard.RestorePending;

        ApplySettings(_settings with { ClickThrough = true }); _tick.Stop();
        DemotePet(); WaitTopmost(() => NativeDesktop.IsTopmost(_handle), 1800);
        var style = NativeDesktop.GetWindowLongPtr(_handle, NativeDesktop.GwlExStyle).ToInt64();
        checks["topmost_recovery_preserves_click_through_no_activate"] =
            (style & NativeDesktop.WsExTransparent) != 0 && (style & NativeDesktop.WsExNoActivate) != 0;

        HidePet();
        var hiddenRestores = guard.RestoreCount; guard.RequestRestore();
        WaitTopmost(() => false, 450);
        checks["topmost_explicit_tray_hide_stays_hidden"] = !IsVisible && !NativeDesktop.IsWindowVisible(_handle) && guard.RestoreCount == hiddenRestores;
        checks["topmost_tray_hide_stops_guard"] = !guard.WatchdogRunning && !guard.RestorePending && guard.HookCount == 0;
        RestoreFromTray(); _tick.Stop();
        checks["topmost_tray_restore_restarts_guard"] = IsVisible && guard.WatchdogRunning && guard.HookCount == 4;

        ApplySettings(_settings with { Topmost = false }); _tick.Stop();
        var disabledRestores = guard.RestoreCount; guard.RequestRestore();
        WaitTopmost(() => false, 450);
        checks["topmost_disabled_respects_user_choice"] = !Topmost && !NativeDesktop.IsTopmost(_handle) && guard.RestoreCount == disabledRestores;
        checks["topmost_disabled_removes_hooks_timers"] = guard.HookCount == 0 && !guard.WatchdogRunning && !guard.RestorePending;
        ApplySettings(_settings with { Topmost = true }); _tick.Stop();
        checks["topmost_reenable_restores_guard"] = NativeDesktop.IsTopmost(_handle) && guard.HookCount == 4 && guard.WatchdogRunning;
        guard.Suspend();
        checks["topmost_system_suspend_stops_guard"] = guard.HookCount == 0 && !guard.WatchdogRunning;
        guard.Synchronize();
        checks["topmost_system_resume_restarts_guard"] = guard.HookCount == 4 && guard.WatchdogRunning;
        ApplySettings(original); _tick.Stop();
    }

    private void DemotePet() => NativeDesktop.SetWindowPos(_handle, new IntPtr(-2), 0, 0, 0, 0,
        NativeDesktop.SwpNoMove | NativeDesktop.SwpNoSize | NativeDesktop.SwpNoActivate | NativeDesktop.SwpNoOwnerZOrder);

    private static bool WaitTopmost(Func<bool> condition, int timeoutMilliseconds)
    {
        if (condition()) return true;
        var clock = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        { if (condition() || clock.ElapsedMilliseconds >= timeoutMilliseconds) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); return condition();
    }
}
