using GptSpritePet.Core;

internal static class AnimationTransitionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        byte[] red = [0, 0, 255, 255], blue = [255, 0, 0, 255], mixed = new byte[4];
        AnimationTransition.BlendPremultiplied(red, blue, mixed, .5);
        check(mixed.SequenceEqual(new byte[] { 128, 0, 128, 255 }), "crossfade preserves opaque alpha and equal color weights");
        AnimationTransition.BlendPremultiplied(red, new byte[4], mixed, .5);
        check(mixed.SequenceEqual(new byte[] { 0, 0, 128, 128 }), "crossfade into transparency retains premultiplied edges");
        AnimationTransition.BlendPremultiplied(red, blue, mixed, 0);
        check(mixed.SequenceEqual(red), "transition begins on the unchanged outgoing image");
        AnimationTransition.BlendPremultiplied(red, blue, mixed, 1);
        check(mixed.SequenceEqual(blue), "transition finishes on the unchanged incoming image");
        check(AnimationTransition.SmoothStep(0) == 0 && AnimationTransition.SmoothStep(.5) == .5
            && AnimationTransition.SmoothStep(1) == 1 && AnimationTransition.SmoothStep(.1) < .1,
            "transition eases motion at both ends");
        var rejected = false;
        try { AnimationTransition.BlendPremultiplied(red, new byte[8], mixed, .5); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "reject mismatched transition surfaces");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build.ps1"))) root = root.Parent;
        var skinPath = Path.Combine(root!.FullName, "assets", "imported", "dragon-girl-calibrated");
        var skin = SkinDefinition.Load(skinPath);
        check(skin.Name == "龙娘 · 色调与锚点校准" && skin.Clips["Idle"].FrameCount == 143
            && skin.Clips["Idle"].DisplayName == "普通待机", "selected idle animation retains asset with simplified label");
        check(skin.Transitions is { DurationSeconds: .2, IdleEntryFrame: 9 }
            && skin.Clips["WalkStart"].Alignment?.Scale == skin.Clips["Walking"].Alignment?.Scale
            && skin.Clips["WalkEnd"].Alignment?.OffsetX == skin.Clips["Walking"].Alignment?.OffsetX,
            "current skin retains fixed walk calibration and the reviewed 0.20-second transition");
        var directory = Path.Combine(Path.GetTempPath(), "GptSpritePet-transitions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var invalid in new[] { "duration", "entry", "scale", "offset", "pivot" })
            {
                var definition = new SkinDefinition { Clips = new() { ["Idle"] = new() { Frames = ["idle.png"] } },
                    Transitions = new(), };
                if (invalid == "duration") definition.Transitions.DurationSeconds = 4;
                if (invalid == "entry") definition.Transitions.IdleEntryFrame = 1;
                definition.Clips["Idle"].Alignment = new();
                if (invalid == "scale") definition.Clips["Idle"].Alignment!.Scale = 2;
                if (invalid == "offset") definition.Clips["Idle"].Alignment!.LeftOffsetX = 1;
                if (invalid == "pivot") definition.Clips["Idle"].Alignment!.PivotY = -1;
                File.WriteAllText(Path.Combine(directory, "skin.json"), System.Text.Json.JsonSerializer.Serialize(definition));
                rejected = false;
                try { SkinDefinition.Load(directory); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "reject invalid animation transition configuration: " + invalid);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
