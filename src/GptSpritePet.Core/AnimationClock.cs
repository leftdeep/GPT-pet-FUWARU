namespace GptSpritePet.Core;

public static class AnimationClock
{
    public static int FrameIndex(double elapsedSeconds, int frameCount, double frameMilliseconds, bool loop)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return 0;
        var frameSeconds = frameMilliseconds / 1000d;
        var cycleSeconds = frameCount * frameSeconds;
        if (!loop && elapsedSeconds >= cycleSeconds) return frameCount - 1;
        var time = loop ? elapsedSeconds % cycleSeconds : elapsedSeconds;
        // Accumulated time can be a few ulps below an exact frame boundary.
        var index = (int)Math.Floor(time / frameSeconds + 1e-9);
        return loop ? index % frameCount : Math.Min(frameCount - 1, index);
    }

    // Play closing frames in order, then reverse them to reopen. The ordinary idle clip continues underneath.
    public static int BlinkFrameIndex(double elapsedSeconds, int frameCount, int frameMilliseconds, int intervalMilliseconds)
    {
        if (frameCount <= 0 || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return -1;
        if (frameMilliseconds <= 0 || intervalMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));
        var steps = frameCount * 2 - 1;
        var phase = elapsedSeconds % (intervalMilliseconds / 1000d) * 1000;
        var start = intervalMilliseconds - steps * frameMilliseconds;
        if (phase < start) return -1;
        var step = Math.Min(steps - 1, (int)((phase - start) / frameMilliseconds));
        return step < frameCount ? step : steps - 1 - step;
    }
}
