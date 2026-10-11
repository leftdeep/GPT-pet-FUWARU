using GptSpritePet.Core;

internal static class WalkingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var timing = new WalkingTiming(24, 59, 29, 55, 16);
        var counts = new HashSet<int>();
        bool sessions = true, endTravel = true;
        for (var seed = 0; seed < 2000; seed++)
        {
            var walk = new WalkingPlayback(timing, seed); walk.Begin(); counts.Add(walk.PlannedCycles);
            var travel = 0d; var last = WalkPhase.Start; var visitedEnd = false;
            while (walk.Playing)
            {
                travel += walk.Advance(seed % 3 == 0 ? .013 : seed % 3 == 1 ? .041 : .19);
                visitedEnd |= walk.Phase == WalkPhase.End;
                sessions &= !(last == WalkPhase.End && walk.Phase == WalkPhase.Loop);
                last = walk.Phase;
            }
            sessions &= visitedEnd && walk.CompletedCycles == walk.PlannedCycles && walk.FrameIndex == 54;
            endTravel &= Math.Abs(travel - (walk.PlannedCycles * timing.LoopSeconds + timing.EndMovingSeconds)) < 1e-7;
        }
        check(counts.SetEquals([3, 4, 5, 6]) && sessions, "2000 random walks finish 3–6 full two-step cycles and the complete outro");
        check(endTravel, "outro half-step adds exactly 16/48 seconds of weighted forward travel at all tick rates");
        var fixedWalk = new WalkingPlayback(timing); fixedWalk.Begin(3, 3);
        check(fixedWalk.Advance(timing.StartFrames / timing.Fps) == 0, "turning and lifting skirt stay in place");
        fixedWalk.Advance(3 * timing.LoopSeconds);
        var early = fixedWalk.Advance(8 / timing.Fps); var late = fixedWalk.Advance(8 / timing.Fps);
        check(Math.Abs(early - .25) < 1e-7 && Math.Abs(late - 1 / 12d) < 1e-7
            && fixedWalk.Advance(1) == 0, "final half-step decelerates smoothly and stays still after source frame 104");
        bool stop = true;
        for (var frame = 0; frame < 59 + 6 * 29; frame++)
        {
            var walk = new WalkingPlayback(timing); walk.Begin(6, 6); walk.Advance((frame + .3) / 24);
            walk.RequestStop(); var finish = walk.FinishSeconds; walk.Advance(100);
            stop &= !walk.Playing && Math.Abs(walk.ElapsedSeconds - finish) < 1e-8 && walk.FrameIndex == 54;
        }
        check(stop, "stop requested at every start/loop frame always finishes putting the skirt down");
        var behavior = new PetBehavior(19) { WalkingTiming = timing, WalkingSpeed = 55, AutoWalk = false };
        behavior.React(PetState.Walking, .1);
        var active = behavior.WalkingPlayback!; behavior.Advance(.2);
        behavior.Paused = true; var time = active.ElapsedSeconds;
        check(behavior.Advance(.25) == 0 && time == active.ElapsedSeconds, "pause freezes shared walking animation and position clock");
        behavior.Paused = false; behavior.React(PetState.Walking, .1);
        check(ReferenceEquals(active, behavior.WalkingPlayback), "repeated walking requests do not restart lifting the skirt");
        behavior.Turn();
        check(behavior.Direction == -1 && ReferenceEquals(active, behavior.WalkingPlayback) && active.ElapsedSeconds == time,
            "screen edge reverses direction immediately without restarting the walking clock");
        while (active.Phase == WalkPhase.Start) behavior.Advance(.041);
        check(behavior.Advance(.041) < 0, "edge turn continues the current walk toward the screen interior");
        behavior.React(PetState.Happy, 3);
        check(behavior.State == PetState.Walking, "click interaction waits for the walking outro");
        for (var i = 0; i < 200 && behavior.State == PetState.Walking; i++) behavior.Advance(.041);
        check(behavior.State == PetState.Happy && active.FrameIndex == 54 && !active.Playing, "queued interaction begins after full skirt-lowering action");
        behavior.Wake(); behavior.React(PetState.Walking);
        check(behavior.Direction == -1, "manual walking keeps the current inward facing direction");
        behavior.Advance(.25); behavior.BeginDrag();
        check(behavior.State == PetState.Dragging && behavior.WalkingPlayback is null, "direct dragging interrupts movement immediately");
        behavior.EndDrag(); behavior.React(PetState.Walking); behavior.Wake();
        for (var i = 0; i < 200 && behavior.State == PetState.Walking; i++) behavior.Advance(.041);
        check(behavior.State == PetState.Idle, "disabling walking returns to idle after the closing pose");
        check(Math.Abs((59 + 3 * 29 + 55) / 24d - 8.375) < 1e-8
            && Math.Abs((59 + 6 * 29 + 55) / 24d - 12) < 1e-8, "full walking sessions last 8.375–12 seconds");
        var clips = new Dictionary<string, AnimationClip> {
            ["WalkStart"] = new() { Video = "s.webm", VideoFrameCount = 59, FrameRate = 24, Loop = false },
            ["Walking"] = new() { Video = "l.webm", VideoFrameCount = 29, FrameRate = 24, Loop = true },
            ["WalkEnd"] = new() { Video = "e.webm", VideoFrameCount = 55, FrameRate = 24, Loop = false } };
        check(new WalkingSequenceDefinition().Timing(clips) == timing, "manifest resolves the authored walking sequence timing");
        clips["WalkEnd"].FrameRate = 30;
        var rejected = false; try { new WalkingSequenceDefinition().Timing(clips); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "mixed frame rates cannot desynchronize the three walking clips");
    }
}
