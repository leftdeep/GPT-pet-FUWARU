using GptSpritePet.Core;

internal static class ContentBoundsChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new DesktopRect(-1920, 0, 1920, 1040);
        var visible = new DesktopRect(200, 300, 240, 360);
        var clamped = DesktopBounds.ClampVisible(new(-9999, 9999, 900, 1000), visible, area);
        check(clamped == new DesktopRect(-2120, 380, 900, 1000), "transparent padding extends outside the work area while the character reaches the left and bottom edges");
        clamped = DesktopBounds.ClampVisible(new(9999, -9999, 900, 1000), visible, area);
        check(clamped == new DesktopRect(-440, -300, 900, 1000), "visible character reaches the right and top edges without resizing its window");
        clamped = DesktopBounds.ClampVisible(new(-1500, 150, 2800, 2000), visible, area);
        check(clamped == new DesktopRect(-1500, 150, 2800, 2000), "large transparent animation canvas does not constrain an already visible character");
        var random = new Random(33); var valid = true;
        for (var i = 0; i < 1000; i++)
        {
            var content = new DesktopRect(random.Next(0, 500), random.Next(0, 700), random.Next(50, 600), random.Next(50, 600));
            var window = new DesktopRect(random.Next(-5000, 5000), random.Next(-5000, 5000), 1000, 1400);
            var target = DesktopBounds.ClampVisible(window, content, area);
            valid &= target.X + content.X >= area.X - 1e-9 && target.Y + content.Y >= area.Y - 1e-9
                && target.X + content.Right <= area.Right + 1e-9 && target.Y + content.Bottom <= area.Bottom + 1e-9
                && target.Width == window.Width && target.Height == window.Height;
        }
        check(valid, "1000 positions preserve the canvas and keep only visible content inside a monitor with a negative origin");
    }
}
