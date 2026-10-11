using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace GptSpritePet;

// WPF's Topmost property can stay true after the shell changes the native Z order.
// Restore without activation after shell transitions; ordinary idle checks never keep raising the window.
internal sealed class WindowTopmostGuard : IDisposable
{
    private readonly Window _window;
    private readonly IntPtr _handle;
    private readonly Func<bool> _enabled;
    private readonly NativeDesktop.WinEventCallback _callback;
    private readonly List<IntPtr> _hooks = new();
    private readonly DispatcherTimer _retry, _watchdog;
    private GCHandle _callbackRoot;
    private bool _active, _queued, _restoring, _disposed;
    private int _retryStep;
    internal int HookCount => _hooks.Count;
    internal bool WatchdogRunning => _watchdog.IsEnabled;
    internal bool RestorePending => _retry.IsEnabled;
    internal int RestoreCount { get; private set; }

    internal WindowTopmostGuard(Window window, IntPtr handle, Func<bool> enabled)
    {
        _window = window; _handle = handle; _enabled = enabled; _callback = OnWinEvent;
        _retry = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher);
        _retry.Tick += (_, _) =>
        {
            Restore(true);
            // Retry after the shell's animations / late Z-order updates, then stop.
            if (++_retryStep >= 3) _retry.Stop();
            else _retry.Interval = TimeSpan.FromMilliseconds(_retryStep == 1 ? 300 : 600);
        };
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _watchdog.Tick += (_, _) => Restore(false);
    }

    internal void Synchronize()
    {
        var active = !_disposed && _window.IsLoaded && _window.IsVisible && _enabled();
        if (!active) { Stop(); return; }
        if (_active) return;
        _active = true;
        _callbackRoot = GCHandle.Alloc(_callback);
        foreach (var type in new[] { NativeDesktop.EventForeground, NativeDesktop.EventSwitchEnd, NativeDesktop.EventDesktopSwitch })
            Hook(type, 0, NativeDesktop.WinEventSkipOwnProcess);
        // Virtual desktops cloak windows; only restore after our own window is uncloaked.
        Hook(NativeDesktop.EventUncloaked, (uint)Environment.ProcessId, 0);
        _watchdog.Start(); RequestRestore();
    }

    private void Hook(uint type, uint process, uint flags)
    {
        var hook = NativeDesktop.SetWinEventHook(type, type, IntPtr.Zero, _callback, process, 0, flags);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (!_active || _disposed || _queued || _window.Dispatcher.HasShutdownStarted) return;
        if (type == NativeDesktop.EventUncloaked && (window != _handle || objectId != 0 || childId != 0)) return;
        // The out-of-context callback runs on our message-loop thread. Defer mutation to avoid reentrancy.
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false; RequestRestore();
        }));
    }

    internal void RequestRestore()
    {
        if (!_active || _disposed || _retry.IsEnabled) return;
        _retryStep = 0; _retry.Interval = TimeSpan.FromMilliseconds(150); _retry.Start();
    }

    internal void NativeStateChanged()
    {
        if (_active && !_restoring && (!NativeDesktop.IsTopmost(_handle) || NativeDesktop.IsIconic(_handle) || !NativeDesktop.IsWindowVisible(_handle)))
            RequestRestore();
    }

    private void Restore(bool force)
    {
        if (!_active || _disposed || !_enabled() || !_window.IsVisible || NativeDesktop.IsCloaked(_handle)) return;
        var show = NativeDesktop.IsIconic(_handle) || !NativeDesktop.IsWindowVisible(_handle);
        if (!force && !show && NativeDesktop.IsTopmost(_handle)) return;
        if (!NativeDesktop.DesktopUnlocked()) return;
        _restoring = true;
        try
        {
            if (show) NativeDesktop.ShowWithoutActivation(_handle);
            if (NativeDesktop.RaiseTopmost(_handle)) RestoreCount++;
        }
        finally { _restoring = false; }
    }

    internal void Suspend() => Stop();
    private void Stop()
    {
        _active = false; _retry.Stop(); _watchdog.Stop();
        foreach (var hook in _hooks) NativeDesktop.UnhookWinEvent(hook);
        _hooks.Clear();
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
}
