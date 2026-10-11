using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using GptSpritePet.Core;

internal static class WebmChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "build.ps1")))
            root = Directory.GetParent(root)?.FullName ?? throw new IOException("Run tests inside the project.");
        var skin = Path.Combine(root, "assets", "dragon-girl-0.40");
        var definition = SkinDefinition.Load(skin);
        check(definition.Clips.Values.All(c => c.Video is not null), "all built-in actions use WebM assets");
        var standingClips = definition.Clips.Where(pair => new[] { "Idle", "IdleLook", "Happy" }.Contains(pair.Key)).ToArray();
        check(standingClips.Length == 3, "retained built-in skin includes the three standing actions");
        check(definition.Clips["Idle"].DisplayName == "普通待机" && definition.Clips["IdleLook"].DisplayName == "待机张望"
            && definition.Clips["Happy"].DisplayName == "开心跳跃", "retained action labels match source videos");
        check(definition.Clips["Idle"].PreviewGroup == AnimationPreviewGroup.Idle
            && definition.Clips["IdleLook"].PreviewGroup == AnimationPreviewGroup.Idle
            && definition.Clips["Happy"].PreviewGroup == AnimationPreviewGroup.ClickInteraction, "retained action preview grouping");
        check(definition.Clips["Idle"].FrameCount == 143 && definition.Clips["IdleLook"].FrameCount == 241
            && definition.Clips["Happy"].FrameCount == 95 && !definition.Clips["Happy"].Loop, "retained source frame counts and one-shot jump preserved");
        check(definition.StandingHeightRatio == 209d / 384, "built-in skin provides the shared clean-standing calibration");
        var cache = Path.Combine(Path.GetTempPath(), "GptSpritePet-webm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            var decoder = Path.Combine(root, "runtime", "ffmpeg.exe");
            var standingHeights = new List<int>(); var groundLines = new List<int>();
            foreach (var (name, clip) in standingClips)
            {
                using var frames = new WebmFrameStore(skin, clip, decoder, cache);
                var first = frames.ReadFrame(0); var last = frames.ReadFrame(frames.FrameCount - 1);
                var visibleRows = Enumerable.Range(0, frames.Height).Where(y =>
                    Enumerable.Range(0, frames.Width).Any(x => first[(y * frames.Width + x) * 4 + 3] >= 64)).ToArray();
                standingHeights.Add(visibleRows.Last() - visibleRows.First() + 1);
                groundLines.Add(visibleRows.Last());
                check(first.Length == frames.Width * frames.Height * 4 && first[3] == 0
                    && Enumerable.Range(0, first.Length / 4).Any(i => first[i * 4 + 3] > 200),
                    name + " VP9 decode preserves clear background and visible character");
                check(last.Length == first.Length, name + " last video frame is accessible");
                var hasMargin = true;
                var distinct = new HashSet<string>();
                for (var index = 0; index < frames.FrameCount; index++)
                {
                    var pixels = frames.ReadFrame(index);
                    distinct.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    for (var x = 0; x < frames.Width; x++)
                        if (pixels[x * 4 + 3] >= 12 || pixels[((frames.Height - 1) * frames.Width + x) * 4 + 3] >= 12) hasMargin = false;
                    for (var y = 0; y < frames.Height; y++)
                        if (pixels[y * frames.Width * 4 + 3] >= 12 || pixels[(y * frames.Width + frames.Width - 1) * 4 + 3] >= 12) hasMargin = false;
                }
                check(hasMargin, name + " all frames stay inside the transparent canvas");
                check(distinct.Count > 1, name + " includes distinct animated poses");
                using var again = new WebmFrameStore(skin, clip, decoder, cache);
                check(again.ReadFrame(0).SequenceEqual(first), name + " repeated stream decode matches first decode");
                check(frames.CachedBytes <= WebmFrameStore.BufferFrames * frames.Width * frames.Height * 4L,
                    name + " decoder retains only bounded frames");
            }
            check(standingHeights.Max() - standingHeights.Min() <= 2, "all three standing characters have equal decoded height within two pixels");
            check(groundLines.Max() - groundLines.Min() <= 2, "all three standing characters share the same decoded ground line");
            var invalid = new AnimationClip { Video = "idle.webm", VideoWidth = 384, VideoHeight = 384,
                VideoFrameCount = 300, FrameRate = 24 };
            void Manifest(AnimationClip clip) => File.WriteAllText(Path.Combine(cache, "skin.json"),
                JsonSerializer.Serialize(new SkinDefinition { Name = "test", Clips = new() { ["Idle"] = clip } }));
            Manifest(invalid);
            check(SkinDefinition.Load(cache).Clips["Idle"].FrameCount == 300, "WebM frame count is independent of PNG preload limit");
            check(SkinDefinition.Load(cache).StandingHeightRatio is null, "legacy skins keep canvas sizing without calibration");
            foreach (var badRatio in new[] { 0d, 0.099, 1.01 })
            {
                File.WriteAllText(Path.Combine(cache, "skin.json"), JsonSerializer.Serialize(new SkinDefinition
                    { Name = "test", StandingHeightRatio = badRatio, Clips = new() { ["Idle"] = invalid } }));
                var rejected = false;
                try { SkinDefinition.Load(cache); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "reject invalid standing calibration: " + badRatio);
            }
            foreach (var kind in new[] { "path", "mixed", "dimensions", "count", "fps", "group" })
            {
                var clip = new AnimationClip { Video = kind == "path" ? "../outside.webm" : "idle.webm",
                    VideoWidth = kind == "dimensions" ? 0 : 384, VideoHeight = 384,
                    VideoFrameCount = kind == "count" ? 0 : 300, FrameRate = kind == "fps" ? null : 24,
                    Frames = kind == "mixed" ? ["idle.png"] : [],
                    PreviewGroup = kind == "group" ? (AnimationPreviewGroup)99 : null };
                Manifest(clip); var rejected = false;
                try { SkinDefinition.Load(cache); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "reject invalid WebM manifest: " + kind);
            }
            var wrong = JsonSerializer.Deserialize<AnimationClip>(JsonSerializer.Serialize(definition.Clips["Idle"]))!;
            wrong.VideoFrameCount++;
            var mismatch = false;
            try { using var store = new WebmFrameStore(skin, wrong, decoder, cache); store.ValidateAsync().GetAwaiter().GetResult(); }
            catch (InvalidDataException) { mismatch = true; }
            check(mismatch, "reject actual video frame count mismatch");
            wrong.VideoFrameCount -= 2; mismatch = false;
            try { using var store = new WebmFrameStore(skin, wrong, decoder, cache); store.ValidateAsync().GetAwaiter().GetResult(); }
            catch (InvalidDataException) { mismatch = true; }
            check(mismatch, "reject understated video frame count");
            var large = JsonSerializer.Deserialize<AnimationClip>(JsonSerializer.Serialize(definition.Clips["Idle"]))!;
            large.VideoWidth = large.VideoHeight = 640; large.VideoFrameCount = 3600;
            using (var metadata = new WebmFrameStore(skin, large, decoder, cache))
                check(metadata.Width == 640 && metadata.Height == 640 && metadata.CachedBytes == 0,
                    "long HD action registers without whole-clip 256 MiB restriction");
            var many = Enumerable.Range(0, 100).Select(_ => new WebmFrameStore(skin, definition.Clips["Idle"], decoder, cache)).ToArray();
            try
            {
                check(many.All(s => s.CachedBytes == 0), "100 registered actions do not preload decoded pixels");
                var expected = many[0].ReadFrame(0);
                foreach (var store in many.Take(5))
                {
                    check(store.ReadFrame(0).SequenceEqual(expected), "switch decoder preserves exact frame");
                    check(WebmFrameStore.ActiveDecoderCount <= 2, "global decoder count is bounded");
                }
                foreach (var index in new[] { 142, 1, 71, 0 })
                    check(many[0].ReadFrame(index).Length == expected.Length, "forward/backward frame selection: " + index);
                check(many[0].ReadFrame(0).SequenceEqual(expected), "backward seek preserves exact pixel content");
                check(WebmFrameStore.TotalCachedBytes <= 2L * 6 * 640 * 640 * 4, "global raw frame buffers fit fixed memory budget");
                check(!Directory.EnumerateFiles(cache, "*.raw", SearchOption.AllDirectories).Any(), "stream decoding writes no raw disk files");
            }
            finally { foreach (var store in many) store.Dispose(); }
            // The playback-only release decoder does not encode test fixtures.
            var fixtureEncoder = Environment.GetEnvironmentVariable("FUWARU_TEST_ENCODER") ?? decoder;
            var start = new ProcessStartInfo(fixtureEncoder) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true };
            foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", "color=red:s=16x16:r=24", "-frames:v", "1", "-c:v", "libvpx-vp9", Path.Combine(cache, "opaque.webm") })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new IOException("Fixture encode timed out."); }
            check(process.ExitCode == 0, "opaque video fixture encoded: " + errors.GetAwaiter().GetResult());
            var opaque = false;
            try { using var store = new WebmFrameStore(cache, new() { Video = "opaque.webm", VideoWidth = 16,
                VideoHeight = 16, VideoFrameCount = 1, FrameRate = 24 }, decoder, cache); store.ReadFrame(0); }
            catch (InvalidDataException) { opaque = true; }
            check(opaque, "reject opaque video instead of displaying a background rectangle");
            using var cancelled = new WebmFrameStore(skin, definition.Clips["Idle"], decoder, cache);
            cancelled.RequestFrame(0); cancelled.Dispose(); var disposed = false;
            try { cancelled.TryReadFrame(0, out _); } catch (ObjectDisposedException) { disposed = true; }
            check(disposed, "cancelled decoder cannot publish or serve frames after disposal");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(cache).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cleanup escaped temporary folder.");
            Directory.Delete(cache, true);
        }
    }
}
