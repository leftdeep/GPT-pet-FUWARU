using GptSpritePet.Core;

internal static class EdgePeekChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new DesktopRect(-1920, -100, 1920, 1080);
        var window = new DesktopRect(0, 0, 720, 650);
        var visible = new DesktopRect(80, 150, 190, 240);
        foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            var docked = EdgePeekPlacement.Attach(window, visible, area, edge, -900, 400);
            var character = visible with { X = docked.X + visible.X, Y = docked.Y + visible.Y };
            check(EdgePeekPlacement.Detect(character, area, 16) == edge, "peek attachment and detection agree at " + edge);
            check(character.X >= area.X && character.Right <= area.Right && character.Y >= area.Y && character.Bottom <= area.Bottom,
                "peek visible pixels remain inside negative-origin work area at " + edge);
            foreach (var center in new[] { (-10000d, -10000d), (10000d, 10000d) })
            {
                docked = EdgePeekPlacement.Attach(window, visible, area, edge, center.Item1, center.Item2);
                check(docked.X + visible.X >= area.X && docked.X + visible.Right <= area.Right &&
                    docked.Y + visible.Y >= area.Y && docked.Y + visible.Bottom <= area.Bottom,
                    "peek corner projection remains inside work area at " + edge + " " + center.Item1);
            }
        }
        check(EdgePeekPlacement.Detect(new(-1500, 200, 200, 300), area, 16) == ScreenEdge.None, "an interior drag does not dock");
        check(EdgePeekPlacement.Detect(new(area.X + 16, 200, 200, 300), area, 16) == ScreenEdge.Left &&
            EdgePeekPlacement.Detect(new(area.X + 17, 200, 200, 300), area, 16) == ScreenEdge.None,
            "edge threshold includes sixteen pixels and excludes seventeen");
        var corner = new DesktopRect(area.X, area.Y, 200, 300);
        check(EdgePeekPlacement.Detect(corner, area, 16, ScreenEdge.Top) == ScreenEdge.Top &&
            EdgePeekPlacement.Detect(corner, area, 16, ScreenEdge.Left) == ScreenEdge.Left,
            "corner docking respects the drag's dominant direction");
        check(EdgePeekPlacement.Detect(corner, area, double.NaN) == ScreenEdge.None &&
            new PetSettings { EdgePeek = (ScreenEdge)999 }.Normalize().EdgePeek == ScreenEdge.None,
            "invalid snap settings fall back to normal mode");
        var settings = new PetSettings { EdgePeek = ScreenEdge.Right, Paused = false, AutoWalk = true };
        var json = System.Text.Json.JsonSerializer.Serialize(settings, SettingsStore.JsonOptions);
        var restored = System.Text.Json.JsonSerializer.Deserialize<PetSettings>(json, SettingsStore.JsonOptions)!;
        check(restored.EdgePeek == ScreenEdge.Right && restored.AutoWalk && !restored.Paused,
            "peek state persists independently of automatic-walking and pause preferences");
        check(System.Text.Json.JsonSerializer.Deserialize<PetSettings>("{\"Size\":240}", SettingsStore.JsonOptions)!.EdgePeek == ScreenEdge.None,
            "older settings without a peek field remain compatible");
    }
}
