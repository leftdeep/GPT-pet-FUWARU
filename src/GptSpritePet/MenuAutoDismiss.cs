using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace GptSpritePet;

/// <summary>Keep all open submenu popups usable, then dismiss after a short crossing grace.</summary>
internal sealed class MenuAutoDismiss
{
    private readonly ContextMenu _menu;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private double? _outsideSince;

    internal MenuAutoDismiss(ContextMenu menu)
    {
        _menu = menu;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) =>
        {
            if (NativeDesktop.GetCursorPos(out var point)) UpdatePointer(new Point(point.X, point.Y), _clock.Elapsed.TotalSeconds);
        };
        menu.Opened += (_, _) => { _outsideSince = null; _clock.Restart(); if (!PetApplication.SmokeTest) _timer.Start(); };
        menu.Closed += (_, _) => { _timer.Stop(); _outsideSince = null; };
    }

    internal void UpdatePointer(Point screenPoint, double now)
    {
        if (!_menu.IsOpen) { _timer.Stop(); return; }
        if (ContainsPointer(_menu, screenPoint)) { _outsideSince = null; return; }
        _outsideSince ??= now;
        if (now - _outsideSince >= .25) _menu.IsOpen = false;
    }

    internal static bool ContainsPointer(ContextMenu menu, Point point) =>
        Contains(menu, point) || ContainsSubmenu(menu.Items, point);

    private static bool ContainsSubmenu(ItemCollection items, Point point)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem { IsSubmenuOpen: true } item) continue;
            if (item.Template?.FindName("PART_Popup", item) is Popup { IsOpen: true, Child: FrameworkElement child }
                && Contains(child, point)) return true;
            if (ContainsSubmenu(item.Items, point)) return true;
        }
        return false;
    }

    private static bool Contains(FrameworkElement element, Point point)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null) return false;
        var topLeft = element.PointToScreen(new Point());
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(topLeft, bottomRight).Contains(point);
    }
}
