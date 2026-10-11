namespace GptSpritePet.Core;

public enum WalkPhase { Idle, Start, Loop, End }

public sealed record WalkingTiming(double Fps, int StartFrames, int LoopFrames, int EndFrames, int EndMoveFrames = 16)
{
    public double LoopSeconds => LoopFrames / Fps;
    public double EndMovingSeconds => EndMoveFrames / (2 * Fps);
    public void Validate()
    {
        if (!double.IsFinite(Fps) || Fps is < 1 or > 60 || StartFrames is < 1 or > 3600
            || LoopFrames is < 2 or > 3600 || EndFrames is < 1 or > 3600 || EndMoveFrames < 0 || EndMoveFrames > EndFrames)
            throw new ArgumentOutOfRangeException(nameof(Fps), "走路片段的帧率、帧数或收步区间无效。");
    }
}

/// <summary>One clock drives the authored frames and window travel, including the final half-step.</summary>
public sealed class WalkingPlayback
{
    public WalkingTiming Timing { get; }
    private readonly Random _random;
    private double _position;
    private int _endAt;
    public bool Playing { get; private set; }
    public int PlannedCycles { get; private set; }
    public int CompletedCycles => Math.Max(0, (int)((Math.Min(_position, _endAt) - Timing.StartFrames + 1e-8) / Timing.LoopFrames));
    public double ElapsedSeconds => _position / Timing.Fps;
    public double FinishSeconds => (_endAt + Timing.EndFrames) / Timing.Fps;
    public WalkPhase Phase => !Playing ? WalkPhase.Idle : _position + 1e-8 < Timing.StartFrames ? WalkPhase.Start
        : _position + 1e-8 < _endAt ? WalkPhase.Loop : WalkPhase.End;
    // Keep the last outro frame available for the crossfade back to the standing pose.
    public int FrameIndex => Phase switch {
        WalkPhase.Start => Math.Clamp((int)Math.Floor(_position + 1e-8), 0, Timing.StartFrames - 1),
        WalkPhase.Loop => Math.Clamp((int)Math.Floor(_position - Timing.StartFrames + 1e-8) % Timing.LoopFrames, 0, Timing.LoopFrames - 1),
        _ => Math.Clamp((int)Math.Floor(_position - _endAt + 1e-8), 0, Timing.EndFrames - 1) };
    public string ClipKey => Phase switch { WalkPhase.Start => "WalkStart", WalkPhase.Loop => "Walking", _ => "WalkEnd" };

    public WalkingPlayback(WalkingTiming timing, int? seed = null)
    {
        timing.Validate(); Timing = timing;
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public bool Begin(int minimumCycles = 3, int maximumCycles = 6)
    {
        if (minimumCycles < 3 || maximumCycles < minimumCycles || maximumCycles > 6)
            throw new ArgumentOutOfRangeException(nameof(minimumCycles), "循环范围必须为 3～6。");
        if (Playing) return false;
        PlannedCycles = _random.Next(minimumCycles, maximumCycles + 1);
        _position = 0; _endAt = Timing.StartFrames + PlannedCycles * Timing.LoopFrames;
        Playing = true;
        return true;
    }

    public void RequestStop()
    {
        if (!Playing || Phase == WalkPhase.End) return;
        var cycles = Phase == WalkPhase.Start ? 0 : (int)Math.Ceiling((_position - Timing.StartFrames - 1e-8) / Timing.LoopFrames);
        _endAt = Math.Min(_endAt, Timing.StartFrames + Math.Max(0, cycles) * Timing.LoopFrames);
    }

    /// <returns>Time weighted by forward speed. The first outro half-step slows linearly to rest.</returns>
    public double Advance(double seconds)
    {
        if (!Playing || !double.IsFinite(seconds) || seconds <= 0) return 0;
        var before = _position;
        _position = Math.Min(_position + seconds * Timing.Fps, _endAt + Timing.EndFrames);
        var walkingFrames = Math.Max(0, Math.Min(_position, _endAt) - Math.Max(before, Timing.StartFrames));
        double EndTravel(double position)
        {
            if (Timing.EndMoveFrames == 0) return 0;
            var q = Math.Clamp(position - _endAt, 0, Timing.EndMoveFrames);
            return q - q * q / (2 * Timing.EndMoveFrames);
        }
        var movingFrames = walkingFrames + EndTravel(_position) - EndTravel(before);
        if (_position + 1e-8 >= _endAt + Timing.EndFrames) { _position = _endAt + Timing.EndFrames; Playing = false; }
        return movingFrames / Timing.Fps;
    }
}

public sealed class WalkingSequenceDefinition
{
    public int EndMoveFrames { get; set; } = 16;
    public double SpeedAt240 { get; set; } = 55;
    public WalkingTiming Timing(IReadOnlyDictionary<string, AnimationClip> clips)
    {
        if (!clips.TryGetValue("WalkStart", out var start) || !clips.TryGetValue("Walking", out var loop)
            || !clips.TryGetValue("WalkEnd", out var end) || start.Loop || !loop.Loop || end.Loop
            || Math.Abs(start.EffectiveFrameMilliseconds - loop.EffectiveFrameMilliseconds) > 1e-6
            || Math.Abs(end.EffectiveFrameMilliseconds - loop.EffectiveFrameMilliseconds) > 1e-6
            || !double.IsFinite(SpeedAt240) || SpeedAt240 is < 5 or > 200)
            throw new InvalidDataException("分段走路需要同帧率的 WalkStart、Walking、WalkEnd；只循环 Walking。");
        var timing = new WalkingTiming(1000 / loop.EffectiveFrameMilliseconds, start.FrameCount, loop.FrameCount, end.FrameCount, EndMoveFrames);
        timing.Validate();
        return timing;
    }
}
