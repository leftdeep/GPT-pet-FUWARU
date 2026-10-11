namespace GptSpritePet.Core;

public readonly record struct DesktopRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public static class DesktopBounds
{
    // Transparent window padding may extend beyond the screen; the character stays inside.
    public static DesktopRect ClampVisible(DesktopRect window, DesktopRect visible, DesktopRect workArea)
    {
        var content = Clamp(new(window.X + visible.X, window.Y + visible.Y, visible.Width, visible.Height), workArea);
        return window with { X = content.X - visible.X, Y = content.Y - visible.Y };
    }

    public static DesktopRect Clamp(DesktopRect window, DesktopRect workArea)
    {
        var width = Math.Min(Math.Max(1, window.Width), workArea.Width);
        var height = Math.Min(Math.Max(1, window.Height), workArea.Height);
        return new DesktopRect(
            Math.Clamp(window.X, workArea.X, workArea.Right - width),
            Math.Clamp(window.Y, workArea.Y, workArea.Bottom - height), width, height);
    }
}
