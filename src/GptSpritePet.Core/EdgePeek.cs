namespace GptSpritePet.Core;

public enum ScreenEdge { None, Left, Right, Top, Bottom }

public static class EdgePeekPlacement
{
    public static ScreenEdge Detect(DesktopRect character, DesktopRect area, double threshold, ScreenEdge preferred = ScreenEdge.None)
    {
        if (!double.IsFinite(threshold) || threshold < 0 || character.Width <= 0 || character.Height <= 0) return ScreenEdge.None;
        var distances = new[] { (ScreenEdge.Left, Math.Abs(character.X - area.X)),
            (ScreenEdge.Right, Math.Abs(area.Right - character.Right)), (ScreenEdge.Top, Math.Abs(character.Y - area.Y)),
            (ScreenEdge.Bottom, Math.Abs(area.Bottom - character.Bottom)) };
        if (preferred != ScreenEdge.None && distances.Any(x => x.Item1 == preferred && x.Item2 <= threshold)) return preferred;
        return distances.Where(x => x.Item2 <= threshold).OrderBy(x => x.Item2).Select(x => x.Item1).FirstOrDefault();
    }

    public static DesktopRect Attach(DesktopRect window, DesktopRect visible, DesktopRect area, ScreenEdge edge, double centerX, double centerY)
    {
        var proposed = window with { X = centerX - visible.X - visible.Width / 2, Y = centerY - visible.Y - visible.Height / 2 };
        proposed = edge switch
        {
            ScreenEdge.Left => proposed with { X = area.X - visible.X },
            ScreenEdge.Right => proposed with { X = area.Right - visible.Right },
            ScreenEdge.Top => proposed with { Y = area.Y - visible.Y },
            ScreenEdge.Bottom => proposed with { Y = area.Bottom - visible.Bottom },
            _ => proposed
        };
        return DesktopBounds.ClampVisible(proposed, visible, area);
    }
}
