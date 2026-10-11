namespace GptSpritePet.Core;

/// <summary>Fixed uniform calibration, in fractions of the full animation canvas.</summary>
public sealed class AnimationAlignment
{
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double? LeftOffsetX { get; set; }
    public double PivotX { get; set; } = .5;
    public double PivotY { get; set; } = .9;

    internal void Validate()
    {
        if (!double.IsFinite(Scale) || Scale is < .8 or > 1.2
            || !double.IsFinite(OffsetX) || Math.Abs(OffsetX) > .25
            || !double.IsFinite(OffsetY) || Math.Abs(OffsetY) > .25
            || LeftOffsetX is { } left && (!double.IsFinite(left) || Math.Abs(left) > .25)
            || !double.IsFinite(PivotX) || PivotX is < 0 or > 1
            || !double.IsFinite(PivotY) || PivotY is < 0 or > 1)
            throw new InvalidDataException("动画对齐需要小幅、有限的缩放和平移，以及画布内的基准点。");
    }
}

public sealed class AnimationTransitionDefinition
{
    public double DurationSeconds { get; set; } = .2;
    public int IdleEntryFrame { get; set; }
    public AnimationAlignment? IdleToWalking { get; set; }
    public AnimationAlignment? WalkingToIdle { get; set; }

    internal void Validate(IReadOnlyDictionary<string, AnimationClip> clips)
    {
        if (!double.IsFinite(DurationSeconds) || DurationSeconds is < .08 or > .4)
            throw new InvalidDataException("动画过渡时长必须为 0.08～0.4 秒。");
        if (!clips.TryGetValue("Idle", out var idle) || !idle.Loop
            || IdleEntryFrame < 0 || IdleEntryFrame >= idle.FrameCount)
            throw new InvalidDataException("待机入口帧必须位于循环待机动作内。");
        IdleToWalking?.Validate(); WalkingToIdle?.Validate();
    }
}

public static class AnimationTransition
{
    public static double SmoothStep(double progress)
    {
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        var t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Mix premultiplied BGRA once, retaining opaque alpha where both frames are opaque.</summary>
    public static void BlendPremultiplied(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, Span<byte> result, double progress)
    {
        if (before.Length != after.Length || before.Length != result.Length || before.Length % 4 != 0)
            throw new ArgumentException("过渡缓冲区的 BGRA 像素数量必须相同。");
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        var t = Math.Clamp(progress, 0, 1);
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)Math.Round(before[i] * (1 - t) + after[i] * t);
    }
}
