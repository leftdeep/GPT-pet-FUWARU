using GptSpritePet.Core;

if (args.Length == 2 && args[0] == "--reset-fixtures")
{
    using var timeline = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "timeline.json")));
    using var feed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "feed.json")));
    var batch = ResetFeedParser.ParsePrimary(timeline.RootElement, feed.RootElement);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
        batch.Source, batch.Stale, count = batch.Events.Length,
        lastReset = batch.Events.Where(e => e.Kind == ResetNoticeKind.Completed).OrderByDescending(e => e.At).FirstOrDefault(),
        previews = batch.Events.Where(e => e.Kind == ResetNoticeKind.Preview).Take(4).ToArray(),
        kindCounts = batch.Events.GroupBy(e => e.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count())
    }));
    return;
}

if (args.Length >= 2 && args[0] == "--live-summary")
{
    var data = Path.GetFullPath(args[1]); Directory.CreateDirectory(data);
    using var live = new AccountUsageService(data);
    var ready = new TaskCompletionSource<AccountUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    live.Changed += snapshot => { if (!snapshot.Busy && snapshot.LocalUsage is not null && snapshot.State is UsageConnectionState.Ready or UsageConnectionState.Partial) ready.TrySetResult(snapshot); };
    live.Configure(new PetSettings());
    try
    {
        var snapshot = await ready.Task.WaitAsync(TimeSpan.FromSeconds(50));
        var localUsage = snapshot.LocalUsage!;
        File.WriteAllText(Path.Combine(data, "usage-preview.json"), System.Text.Json.JsonSerializer.Serialize(snapshot));
        var summary = UsageSummary.Create(snapshot, DateTimeOffset.Now, 60);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { fiveHours=summary.FiveHours.Value, sevenDays=summary.SevenDays.Value,
            todayTokens=summary.Today.Value, currentTaskTokens=summary.Task.Value, activeTasks=localUsage.Tasks.Length,
            localToday=localUsage.TodayTokens, localAvailable=localUsage.Available, credentialsStored=false }));
    }
    catch (TimeoutException) { Console.WriteLine("Live usage did not return data: " + live.Snapshot.Message); Environment.ExitCode = 1; }
    return;
}
if (args.Length >= 2 && args[0] == "--live-usage")
{
    var data = Path.GetFullPath(args[1]); Directory.CreateDirectory(data);
    using var live = new AccountUsageService(data);
    var ready = new TaskCompletionSource<AccountUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    live.Changed += snapshot => { if (!snapshot.Busy && snapshot.State is UsageConnectionState.Ready or UsageConnectionState.Partial) ready.TrySetResult(snapshot); };
    live.Configure(new PetSettings());
    try
    {
        var snapshot = await ready.Task.WaitAsync(TimeSpan.FromSeconds(55));
        File.WriteAllText(Path.Combine(data, "usage-preview.json"), System.Text.Json.JsonSerializer.Serialize(snapshot));
        var before = snapshot.LimitsUpdatedAt;
        var refreshed = new TaskCompletionSource<AccountUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Changed += value => { if (!value.Busy && value.LimitsUpdatedAt > before) refreshed.TrySetResult(value); };
        live.RequestRefresh(); var manual = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        var automatic = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Changed += value => { if (!value.Busy && value.LimitsUpdatedAt > manual.LimitsUpdatedAt?.AddSeconds(25)) automatic.TrySetResult(true); };
        live.Configure(new PetSettings { UsageRefreshSeconds = 30 });
        await automatic.Task.WaitAsync(TimeSpan.FromSeconds(55));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { state = snapshot.State.ToString(),
            accountType = snapshot.Account?.Type, cumulativeTokens = snapshot.Tokens?.LifetimeTokens,
            latestDate = snapshot.Tokens?.LatestDate, quotaCount = snapshot.Quotas.Length, manualRefresh = true, automaticRefresh = true,
            credentialsStored = false, output = data }));
    }
    catch (TimeoutException) { Console.WriteLine("Live account connection did not return data: " + live.Snapshot.Message); Environment.ExitCode = 1; }
    return;
}
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
WebmChecks.Run(Check);
AnimationTransitionChecks.Run(Check);
var bounds = new DesktopRect(-1920, 0, 1920, 1040);
Check(DesktopBounds.Clamp(new(-2200, 2000, 280, 340), bounds) == new DesktopRect(-1920, 700, 280, 340), "negative monitor origin and taskbar boundary");
Check(DesktopBounds.Clamp(new(500, 800, 3000, 2000), bounds) == bounds, "oversized window fits work area");
ContentBoundsChecks.Run(Check);
var behavior = new PetBehavior(5);
behavior.BeginDrag(); behavior.React(PetState.Happy); behavior.Advance(30);
Check(behavior.State == PetState.Dragging, "drag cannot be interrupted by reactions or autonomous actions");
behavior.EndDrag(); behavior.React(PetState.Walking, 10); behavior.Paused = true;
Check(behavior.Advance(1) == 0 && behavior.State == PetState.Walking, "pause freezes movement and state");
behavior.Paused = false; var step = behavior.Advance(99);
Check(Math.Abs(step) <= 11.25, "sleep or delayed frame cannot teleport the pet");
behavior.React(PetState.Happy, 0.1); behavior.Advance(0.2);
Check(behavior.State == PetState.Idle, "one-shot reaction returns to idle");
behavior.AutoWalk = false; behavior.Wake();
for (var i = 0; i < 6010; i++) { behavior.Advance(0.1); if (behavior.State == PetState.Walking) throw new Exception("unexpected walking"); }
Check(behavior.State == PetState.Sleeping, "idle timeout enters sleep with auto-walk disabled");
behavior.Wake(); Check(behavior.State == PetState.Idle, "wake resets sleep");
RestAnimationChecks.Run(Check);
AutoWalkChecks.Run(Check);
WalkingChecks.Run(Check);
await UsageServiceChecks.Run(Check);
var settings = new PetSettings { Size = 900, X = double.NaN, Frequency = (InteractionFrequency)99 }.Normalize();
Check(settings.Size == 360 && settings.X is null && settings.Frequency == InteractionFrequency.Normal, "settings normalize invalid size position and enum");
Check(AnimationClock.FrameIndex(0.239, 8, 120, true) == 1 && AnimationClock.FrameIndex(0.241, 8, 120, true) == 2, "clip advances at its own frame duration");
Check(AnimationClock.FrameIndex(0.961, 8, 120, true) == 0, "looping clip wraps at the cycle boundary");
Check(AnimationClock.FrameIndex(20, 4, 120, false) == 3, "one-shot clip holds its last frame until behavior changes");
Check(AnimationClock.FrameIndex(-2, 8, 120, true) == 0 && AnimationClock.FrameIndex(double.NaN, 8, 120, true) == 0,
    "invalid animation elapsed time cannot create an invalid index");
Check(AnimationClock.FrameIndex(double.MaxValue, 8, 120, true) is >= 0 and < 8, "large elapsed time stays within the clip");
var videoFrameMs = 1000d / 24;
Check(Enumerable.Range(0, 25).All(i => AnimationClock.FrameIndex((i + 0.5) / 24, 25, videoFrameMs, true) == i)
    && AnimationClock.FrameIndex(25d / 24, 25, videoFrameMs, true) == 0,
    "native 24-fps video retains all twenty-five poses and its exact loop duration");
foreach (var invalidDuration in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
{
    var rejected = false;
    try { AnimationClock.FrameIndex(1, 25, invalidDuration, true); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "reject non-finite or nonpositive frame duration: " + invalidDuration);
}
var accumulated = 0d;
Check(Enumerable.Range(1, 240).All(i => { accumulated += 0.04; return AnimationClock.FrameIndex(accumulated, 24, 40, true) == i % 24; }),
    "accumulated frame time does not repeat or drop a frame at a floating point boundary");
Check(AnimationClock.BlinkFrameIndex(4.3, 2, 80, 4600) == -1, "idle does not blink continuously");
Check(AnimationClock.BlinkFrameIndex(4.38, 2, 80, 4600) == 0 && AnimationClock.BlinkFrameIndex(4.46, 2, 80, 4600) == 1
    && AnimationClock.BlinkFrameIndex(4.54, 2, 80, 4600) == 0, "blink closes and reopens independently from idle frames");
Check(AnimationClock.BlinkFrameIndex(4.7, 2, 80, 4600) == -1, "blink returns to the ordinary idle clip");
var directory = Path.Combine(Path.GetTempPath(), "GptSpritePet-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new SettingsStore(directory); Check(store.Load().Size == 240, "missing settings use defaults");
    var saved = new PetSettings { Size = 180, X = -500, Y = 210, ClickThrough = true, Paused = true };
    store.Save(saved); Check(store.Load() == saved, "settings survive atomic round trip");
    File.WriteAllText(store.FilePath, "{corrupt");
    Check(store.Load().Size == 240 && store.LastWarning != null, "corrupt settings safely recover");
    Check(Directory.GetFiles(directory, "*.invalid-*").Length == 1, "corrupt settings are retained for diagnosis");
    File.WriteAllText(Path.Combine(directory, "skin.json"), "{\"Name\":\"test\",\"FrameMilliseconds\":5,\"Animations\":{\"Idle\":[\"idle.png\"]}}");
    Check(SkinDefinition.Load(directory).FrameMilliseconds == 60, "skin timing is bounded");
    Check(SkinDefinition.Load(directory).EffectiveClips()[PetState.Idle].FrameCount == 1, "legacy animation manifests remain usable");
    var named = new SkinDefinition { Clips = new() { ["Idle"] = new() { Frames = ["idle.png"] },
        ["PlayBall"] = new() { DisplayName = "玩球", Frames = ["ball.png"], Loop = false } } };
    File.WriteAllText(Path.Combine(directory, "skin.json"), System.Text.Json.JsonSerializer.Serialize(named));
    var namedLoaded = SkinDefinition.Load(directory);
    Check(namedLoaded.NamedClips().Count == 2 && namedLoaded.NamedClips()["PlayBall"].DisplayName == "玩球",
        "custom action keys and display names extend the menu without new enum members");
    Check(namedLoaded.EffectiveClips().Count == 1 && namedLoaded.EffectiveClips().ContainsKey(PetState.Idle),
        "custom previews remain separate from autonomous behavior states");
    Check(SkinDefinition.Load(directory).DisplayScale == 0.96, "legacy skins retain the original display scale");
    Check(SkinDefinition.ResolveFrame(directory, "idle.png") == Path.Combine(directory, "idle.png"), "valid PNG resolves inside skin");
    foreach (var path in new[] { "../outside.png", "../GptSpritePet-other/a.png", "C:\\outside.png", "image.jpg", "idle.png:alternate.png" })
    {
        var rejected = false;
        try { SkinDefinition.ResolveFrame(directory, path); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject invalid skin frame: " + path);
    }
    File.WriteAllText(Path.Combine(directory, "skin.json"), """
        {"Name":"clips", "Animations":{"Idle":["legacy.png"]}, "BlinkFrames":["half.png","closed.png"],
         "Clips":{"Idle":{"Frames":["idle.png"],"FrameMilliseconds":500},
                  "Walking":{"SpriteSheet":"walk.png","Columns":4,"Rows":2,"FrameIndices":[0,1,2,3,4,5,6,7],"FrameMilliseconds":100,"AnchorX":0.65,"AnchorY":0.986},
                  "Happy":{"Frames":["happy1.png","happy2.png"],"Loop":false,"FrameMilliseconds":10}}}
        """);
    var clips = SkinDefinition.Load(directory); var effective = clips.EffectiveClips();
    Check(effective[PetState.Idle].Frames[0] == "idle.png" && effective[PetState.Idle].FrameMilliseconds == 500,
        "explicit clips override legacy frames and keep independent timing");
    Check(effective[PetState.Walking].FrameCount == 8 && effective[PetState.Walking].Loop, "sheet indices define an eight-frame walking loop");
    Check(!effective[PetState.Happy].Loop && effective[PetState.Happy].FrameMilliseconds == 60, "one-shot clip mode survives loading and timing is bounded");
    Check(clips.BlinkFrames.Length == 2 && clips.BlinkIntervalMilliseconds == 4600, "idle blink has a separate frame sequence and interval");
    Check(effective[PetState.Walking].AnchorX == 0.65 && effective[PetState.Walking].AnchorY == 0.986,
        "per-clip body and foot anchors survive manifest loading");
    Check(effective[PetState.Idle].AnchorX == 0.5 && effective[PetState.Idle].AnchorY == 0.96,
        "skins without anchor configuration retain their original placement");
    File.WriteAllText(Path.Combine(directory, "skin.json"), """
        {"Name":"aligned", "DisplayScale":0.84,
         "Clips":{"Walking":{"SpriteSheet":"walk.png","Columns":4,"Rows":2,"FrameIndices":[1,6],
                  "FrameAnchors":[{"X":0.676,"Y":0.992},{"X":0.613,"Y":0.975}]}}}
        """);
    var aligned = SkinDefinition.Load(directory);
    Check(aligned.DisplayScale == 0.84, "skin display scale can reserve transparent margins");
    Check(aligned.Clips["Walking"].FrameAnchors.Length == 2 && aligned.Clips["Walking"].FrameAnchors[1].X == 0.613,
        "frame anchors follow playback sequence rather than sheet cell indices");
    foreach (var (json, name) in new[]
    {
        ("""{"Name":"bad","Clips":{"Walking":{"SpriteSheet":"../outside.png","Columns":4,"Rows":2}}}""", "sheet path outside the skin"),
        ("""{"Name":"bad","BlinkFrames":["../outside.png"]}""", "blink path outside the skin"),
        ("""{"Name":"bad","Clips":{"Walking":{"SpriteSheet":"walk.png","Columns":4,"Rows":2,"FrameIndices":[8]}}}""", "sheet frame index outside the grid"),
        ("""{"Name":"bad","Clips":{"Walking":{"SpriteSheet":"walk.png","Columns":0,"Rows":2}}}""", "zero sheet columns"),
        ("""{"Name":"bad","Clips":{"Walking":{"SpriteSheet":"walk.png","Frames":["idle.png"]}}}""", "mixed sheet and independent frames"),
        ("""{"Name":"bad","Clips":{"Happy":{"Frames":[]}}}""", "empty explicit clip"),
        ("""{"Name":"bad","Animations":{"99":["idle.png"]}}""", "undefined numeric state"),
        ("""{"Name":"bad","Clips":{"../Unknown":{"Frames":["idle.png"]}}}""", "unsafe custom action name"),
        ("""{"Name":"bad","BlinkFrames":["1.png","2.png","3.png","4.png","5.png"]}""", "excessive blink sequence"),
        ("""{"Name":"bad","Clips":{"Idle":{"Frames":["idle.png"],"AnchorX":-0.01}}}""", "body anchor outside the image"),
        ("""{"Name":"bad","Clips":{"Walking":{"SpriteSheet":"walk.png","AnchorY":1.01}}}""", "foot anchor outside the image"),
        ("""{"Name":"bad","DisplayScale":0.49}""", "display scale below the allowed range"),
        ("""{"Name":"bad","DisplayScale":1.01}""", "display scale above the allowed range"),
        ("""{"Name":"bad","DisplayScale":1e309}""", "non-finite display scale"),
        ("""{"Name":"bad","Clips":{"Idle":{"Frames":["1.png","2.png"],"FrameAnchors":[{"X":0.5,"Y":0.96}]}}}""", "frame anchor count differs from the animation"),
        ("""{"Name":"bad","Clips":{"Idle":{"Frames":["idle.png"],"FrameAnchors":[{"X":-0.01,"Y":0.96}]}}}""", "per-frame body anchor outside the image"),
        ("""{"Name":"bad","Clips":{"Idle":{"Frames":["idle.png"],"FrameAnchors":[{"X":0.5,"Y":1.01}]}}}""", "per-frame foot anchor outside the image"),
        ("""{"Name":"bad","Clips":{"Idle":{"Frames":["idle.png"],"FrameAnchors":[{"X":1e309,"Y":0.96}]}}}""", "non-finite per-frame anchor"),
        ("""{"Name":"bad","Clips":{"Idle":{"Frames":["idle.png"],"FrameAnchors":[null]}}}""", "null per-frame anchor")
    })
    {
        File.WriteAllText(Path.Combine(directory, "skin.json"), json); var rejected = false;
        try { SkinDefinition.Load(directory); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject " + name);
    }
    var tooMany = new SkinDefinition { Name = "large", Clips = new() {
        ["Walking"] = new() { SpriteSheet = "walk.png", Columns = 16, Rows = 8 },
        ["Idle"] = new() { SpriteSheet = "walk.png", Columns = 16, Rows = 8 } }, BlinkFrames = ["closed.png"] };
    File.WriteAllText(Path.Combine(directory, "skin.json"), """{"Name":"bad","Clips":{"Idle":{"Frames":["idle.png"],"AnchorY":1e309}}}""");
    var nonFiniteAnchorRejected = false;
    try { SkinDefinition.Load(directory); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { nonFiniteAnchorRejected = true; }
    Check(nonFiniteAnchorRejected, "non-finite anchor cannot enter the renderer");
    File.WriteAllText(Path.Combine(directory, "skin.json"), System.Text.Json.JsonSerializer.Serialize(tooMany));
    var overLimit = false;
    try { SkinDefinition.Load(directory); } catch (InvalidDataException) { overLimit = true; }
    Check(overLimit, "total frame limit includes sheet cells and blink frames");
}
finally
{
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(directory).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Test cleanup escaped temporary directory.");
    Directory.Delete(directory, true);
}
var fullWalkDirectory = Path.Combine(Path.GetTempPath(), "GptSpritePet-fullwalk-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fullWalkDirectory);
try
{
    AnimationClip FullClip() => new()
    {
        SpriteSheet = "full.png", Columns = 4, Rows = 2, WalkingStride = 128, FrameMilliseconds = 120,
        FrameAnchors = Enumerable.Range(0, 8).Select(_ => new FrameAnchor { X = 0.6, Y = 0.95 }).ToArray(),
        FootContacts = Enumerable.Range(0, 8).Select(_ => new FrameAnchor { X = 0.65, Y = 0.95 }).ToArray()
    };
    var fullSkin = new SkinDefinition { Name = "full character", Clips = new() { ["Walking"] = FullClip() } };
    void SaveFull() => File.WriteAllText(Path.Combine(fullWalkDirectory, "skin.json"), System.Text.Json.JsonSerializer.Serialize(fullSkin));
    SaveFull(); var loadedFull = SkinDefinition.Load(fullWalkDirectory);
    Check(loadedFull.Clips["Walking"].FrameCount == 8 && loadedFull.Clips["Walking"].WalkingStride == 128,
        "full-character walking loads as eight intact PNG cells without a cutout rig");
    Check(loadedFull.Clips["Walking"].FootContacts.Length == 8 && loadedFull.Clips["Walking"].FrameMilliseconds == 120,
        "authored walking frames retain measured feet and independent frame duration");
    AnimationClip VideoClip() => new()
    {
        Frames = Enumerable.Range(1, 25).Select(i => $"video-{i:00}.png").ToArray(), FrameRate = 24, WalkingStride = 70,
        FrameAnchors = Enumerable.Range(0, 25).Select(_ => new FrameAnchor { X = 0.6, Y = 0.95 }).ToArray(),
        FootContacts = Enumerable.Range(0, 25).Select(_ => new FrameAnchor { X = 0.65, Y = 0.95 }).ToArray(),
        ContactLegIds = Enumerable.Range(0, 25).Select(i => i < 12 ? 0 : 1).ToArray()
    };
    fullSkin.Clips = new() { ["Walking"] = VideoClip() }; SaveFull();
    var video = SkinDefinition.Load(fullWalkDirectory).Clips["Walking"];
    Check(video.FrameCount == 25 && Math.Abs(video.EffectiveFrameMilliseconds - 1000d / 24) < 1e-12
        && video.ContactLegIds.Count(id => id == 0) == 12,
        "odd-length original video cycle loads at native fps with explicit support-leg contacts");
    foreach (var issue in new[] { "invalid fps", "excessive fps", "missing video legs", "invalid leg id", "extra support swaps" })
    {
        var clip = VideoClip();
        switch (issue)
        {
            case "invalid fps": clip.FrameRate = 0; break;
            case "excessive fps": clip.FrameRate = 61; break;
            case "missing video legs": clip.ContactLegIds = []; break;
            case "invalid leg id": clip.ContactLegIds[0] = 2; break;
            case "extra support swaps": clip.ContactLegIds[3] = 1; break;
        }
        fullSkin.Clips = new() { ["Walking"] = clip }; SaveFull(); var rejected = false;
        try { SkinDefinition.Load(fullWalkDirectory); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject video clip " + issue);
    }
    foreach (var issue in new[] { "missing feet", "missing anchors", "invalid foot", "excessive stride", "nonlooping walk", "unsupported speed", "feet without stride", "stride on idle" })
    {
        var clip = FullClip(); fullSkin.Clips = new() { ["Walking"] = clip };
        switch (issue)
        {
            case "missing feet": clip.FootContacts = clip.FootContacts[..7]; break;
            case "missing anchors": clip.FrameAnchors = []; break;
            case "invalid foot": clip.FootContacts[0].X = 1.1; break;
            case "excessive stride": clip.WalkingStride = 193; break;
            case "nonlooping walk": clip.Loop = false; break;
            case "unsupported speed": clip.FrameMilliseconds = 60; break;
            case "feet without stride": clip.WalkingStride = null; break;
            case "stride on idle": fullSkin.Clips = new() { ["Idle"] = clip }; break;
        }
        SaveFull(); var rejected = false;
        try { SkinDefinition.Load(fullWalkDirectory); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject full-character " + issue);
    }
}
finally
{
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(fullWalkDirectory).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
    Directory.Delete(fullWalkDirectory, true);
}
var multiWalkDirectory = Path.Combine(Path.GetTempPath(), "GptSpritePet-multiwalk-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(multiWalkDirectory);
try
{
    AnimationClip MultiClip() => new()
    {
        SpriteSheets = ["near.png", "far.png"], Columns = 4, Rows = 2, WalkingStride = 128, FrameMilliseconds = 60,
        FrameAnchors = Enumerable.Range(0, 16).Select(_ => new FrameAnchor { X = 0.6, Y = 0.95 }).ToArray(),
        FootContacts = Enumerable.Range(0, 16).Select(_ => new FrameAnchor { X = 0.65, Y = 0.95 }).ToArray()
    };
    var multiSkin = new SkinDefinition { Name = "sixteen poses", Clips = new() { ["Walking"] = MultiClip() } };
    void SaveMulti() => File.WriteAllText(Path.Combine(multiWalkDirectory, "skin.json"), System.Text.Json.JsonSerializer.Serialize(multiSkin));
    SaveMulti(); var multi = SkinDefinition.Load(multiWalkDirectory).Clips["Walking"];
    Check(multi.FrameCount == 16 && multi.SpriteSheets.SequenceEqual(new[] { "near.png", "far.png" }), "two sheets preserve ordered sixteen-frame walk");
    multiSkin.Clips["Walking"].FrameIndices = Enumerable.Range(0, 16).ToArray(); SaveMulti();
    Check(SkinDefinition.Load(multiWalkDirectory).Clips["Walking"].FrameIndices[8] == 8, "global frame indices cross into the second half");
    foreach (var issue in new[] { "mixed atlas modes", "mixed frames", "outside atlas", "outside global index", "null atlas list", "missing second contacts" })
    {
        var clip = MultiClip(); multiSkin.Clips["Walking"] = clip;
        switch (issue)
        {
            case "mixed atlas modes": clip.SpriteSheet = "other.png"; break;
            case "mixed frames": clip.Frames = ["other.png"]; break;
            case "outside atlas": clip.SpriteSheets[1] = "../far.png"; break;
            case "outside global index": clip.FrameIndices = [16]; break;
            case "null atlas list": clip.SpriteSheets = null!; break;
            case "missing second contacts": clip.FootContacts = clip.FootContacts[..8]; break;
        }
        SaveMulti(); var rejected = false;
        try { SkinDefinition.Load(multiWalkDirectory); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "reject multiple-sheet " + issue);
    }
    var elapsed = 0d;
    Check(Enumerable.Range(1, 4800).All(i => { elapsed += .02; return AnimationClock.FrameIndex(elapsed, 16, 60, true) == i / 3 % 16; }),
        "sixteen-frame sixty-ms walk retains all poses over one hundred cycles at twenty-ms updates");
}
finally
{
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(multiWalkDirectory).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
    Directory.Delete(multiWalkDirectory, true);
}
LocalUsageChecks.Run(Check);
EdgePeekChecks.Run(Check);
MovementSelectionChecks.Run(Check);
OutputRateChecks.Run(Check);
LocalScanChecks.Run(Check);
CacheChecks.Run(Check);
TaskSelectionChecks.Run(Check);
GrowthChecks.Run(Check);
await ResetWatchChecks.Run(Check);
UsageChecks.Run(Check);
Console.WriteLine($"{passed} checks passed.");
