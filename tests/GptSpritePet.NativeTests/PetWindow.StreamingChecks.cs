using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunOnDemandAnimationChecks(Dictionary<string, bool> checks)
    {
        SpriteView.RunColdDragChecks(_settings.SkinDirectory, _store.DirectoryPath, checks);
        using var sprite = new SpriteView { ExactFrameReads = false };
        sprite.LoadSkin(_settings.SkinDirectory);
        var viewport = sprite.ViewportSizeAt(240);
        sprite.Width = sprite.Height = viewport;
        sprite.Measure(new Size(viewport, viewport)); sprite.Arrange(new Rect(0, 0, viewport, viewport));
        bool AwaitFrame(int index)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                RenderSprite(sprite);
                if (sprite.PresentedFrameIndex == index && !sprite.WaitingForVideoFrame) return true;
                Thread.Sleep(10);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            return false;
        }
        checks["stream_startup_loads_current_action"] = AwaitFrame(0);
        var before = BitmapPixels(RenderSprite(sprite));
        var key = sprite.AvailableActions.First(a => a.Key != "Idle").Key;
        var request = Stopwatch.StartNew(); sprite.BeginPreview(key); RenderSprite(sprite); request.Stop();
        checks["stream_switch_does_not_block_ui"] = request.Elapsed < TimeSpan.FromMilliseconds(250);
        if (sprite.WaitingForVideoFrame)
        {
            var pending = BitmapPixels(RenderSprite(sprite));
            // The decoder can finish between two UI reads. Compare only if still waiting.
            var meanDifference = before.Zip(pending, (a, b) => Math.Abs(a - b)).Average();
            checks["stream_pending_frame_preserves_previous_image"] = !sprite.WaitingForVideoFrame || meanDifference < .3;
        }
        checks["stream_switch_loads_new_action"] = AwaitFrame(0);
        sprite.Advance(sprite.PreviewSeconds(key) * .5);
        checks["stream_seek_loads_exact_frame"] = AwaitFrame(sprite.CurrentFrameIndex);
        checks["stream_hit_test_matches_presented_pixels"] = AlphaMatchesRender(sprite, RenderSprite(sprite));
        sprite.ResetAnimation();
        checks["stream_restart_loads_first_frame"] = AwaitFrame(0);
        checks["stream_all_actions_share_memory_limit"] = sprite.DecodedCacheBytes <= 80L * 1024 * 1024;
        checks["stream_all_actions_share_two_decoders"] = WebmFrameStore.ActiveDecoderCount <= 2;
        checks["stream_decoder_reports_no_error"] = sprite.LoadWarning is null;
    }
}
