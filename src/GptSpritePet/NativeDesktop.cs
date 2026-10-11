using System;
using System.Runtime.InteropServices;
using GptSpritePet.Core;

namespace GptSpritePet;

internal static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder name, int length, out int needed);
    internal static bool RecentInput()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Time) < 15 * 60 * 1000;
    }
    internal static bool DesktopUnlocked()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try { var name = new System.Text.StringBuilder(128); return GetUserObjectInformation(desktop, 2, name, 256, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase); }
        finally { CloseDesktop(desktop); }
    }
    internal const int GwlExStyle = -20;
    internal const long WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTransparent = 0x20, WsExTopmost = 8;
    internal const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoZOrder = 4, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    internal const uint EventForeground = 3, EventSwitchEnd = 0x15, EventDesktopSwitch = 0x20, EventUncloaked = 0x8018;
    internal const uint WinEventSkipOwnProcess = 2;
    internal delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
    internal static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    internal static bool IsTopmost(IntPtr window) => (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExTopmost) != 0;
    internal static bool IsCloaked(IntPtr window) => DwmGetWindowAttribute(window, 20, out var value, sizeof(uint)) == 0 && value != 0;
    internal static bool RaiseTopmost(IntPtr window) => SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0,
        SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
    }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    internal static void ShowWithoutActivation(IntPtr window) => ShowWindow(window, 4); // SW_SHOWNOACTIVATE
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);

    internal static DesktopRect WindowRect(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    internal static DesktopRect WorkArea(IntPtr window) => GetWork(MonitorFromWindow(window, 2));
    internal static DesktopRect WorkAreaAt(int x, int y) => GetWork(MonitorFromPoint(new Point { X = x, Y = y }, 2));
    private static DesktopRect GetWork(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return new(0, 0, 1280, 720);
        return new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }
    internal static void ApplyStyles(IntPtr window, bool through)
    {
        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64() | WsExNoActivate | WsExToolWindow;
        style = through ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(window, GwlExStyle, new IntPtr(style));
    }
    internal static void Move(IntPtr window, double x, double y) => SetWindowPos(window, IntPtr.Zero,
        (int)Math.Round(x), (int)Math.Round(y), 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
}
