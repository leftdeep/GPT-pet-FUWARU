using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GptSpritePet.Core;

namespace GptSpritePet;

internal sealed partial class SpriteView
{
    internal static void RunColdDragChecks(string? skin, string directory, Dictionary<string, bool> checks)
    {
        byte[] Pixels(RenderTargetBitmap image)
        {
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
        }
        foreach (var edge in new[] { ScreenEdge.None, ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            using var sprite = new SpriteView { ExactFrameReads = false, Width = 480, Height = 480, PeekSize = 96 };
            sprite.LoadSkin(skin);
            sprite.Measure(new Size(480, 480)); sprite.Arrange(new Rect(0, 0, 480, 480));
            // Deterministically gate the first frame, even on a machine with a warm decoder.
            // These delegates exist only in the test assembly; production stays asynchronous.
            var ready = false;
            foreach (var clip in sprite._namedClips.Values.Distinct())
                for (var i = 0; i < clip.Frames.Length; i++)
                {
                    var original = clip.Frames[i];
                    clip.Frames[i] = () => ready ? original() : null;
                }
            RenderTargetBitmap Draw()
            {
                var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) sprite.DrawFrame(dc);
                var image = new RenderTargetBitmap(480, 480, 96, 96, PixelFormats.Pbgra32); image.Render(visual); return image;
            }
            if (edge == ScreenEdge.None)
            {
                var first = Pixels(Draw());
                checks["cold_start_waits_without_placeholder"] = first.All(value => value == 0) && sprite.WaitingForVideoFrame && !sprite.IsPlaceholder;
                checks["cold_start_has_no_phantom_placeholder_hit"] = !sprite.IsOpaqueHit(new Point(240, 240));
                sprite.State = PetState.Dragging;
                checks["cold_start_drag_has_no_placeholder"] = Pixels(Draw()).All(value => value == 0);
            }
            else
            {
                checks["cold_drag_static_pose_loads_" + edge] = sprite.SetEdgePeek(edge);
                var before = Pixels(Draw());
                sprite.SetEdgePeek(ScreenEdge.None); sprite.State = PetState.Dragging;
                var pending = Draw();
                checks["cold_drag_keeps_static_pose_" + edge] = sprite.WaitingForVideoFrame && before.SequenceEqual(Pixels(pending));
                checks["cold_drag_repeated_wait_keeps_pose_" + edge] = before.SequenceEqual(Pixels(Draw()));
                checks["cold_drag_hit_test_keeps_pose_" + edge] = sprite.IsOpaqueHit(new Point(240, 240)) == (before[(240 * 480 + 240) * 4 + 3] >= 12);
                using var file = File.Create(Path.Combine(directory, "cold-drag-pending-" + edge + ".png"));
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(pending)); png.Save(file);
            }
            ready = true; sprite.ExactFrameReads = true;
            var decoded = Pixels(Draw());
            checks["cold_drag_recovers_character_" + edge] = !sprite.WaitingForVideoFrame && sprite.PresentedFrameIndex == 0 && decoded.Any(value => value != 0);
            sprite.State = PetState.Idle;
            checks["cold_drag_release_keeps_loaded_skin_" + edge] = !sprite.IsPlaceholder && sprite.LoadWarning is null && Pixels(Draw()).Any(value => value != 0);
        }
        using var missing = new SpriteView { Width = 480, Height = 480 };
        missing.LoadSkin(Path.Combine(directory, "missing-skin"));
        missing.Measure(new Size(480, 480)); missing.Arrange(new Rect(0, 0, 480, 480));
        var fallback = new DrawingVisual(); using (var dc = fallback.RenderOpen()) missing.DrawFrame(dc);
        var surface = new RenderTargetBitmap(480, 480, 96, 96, PixelFormats.Pbgra32); surface.Render(fallback);
        checks["missing_skin_still_has_functional_placeholder"] = missing.IsPlaceholder && Pixels(surface).Any(value => value != 0)
            && missing.IsOpaqueHit(new Point(240, 240));
    }
}
