namespace GptSpritePet.Core;

/// <summary>A matched output/duration sample from usage logs, not streamed decode timing.</summary>
public sealed record OutputRateSample(long OutputTokens, double DurationMilliseconds, DateTimeOffset SampledAt)
{
    public const int FreshSeconds = 8;
    public const int RetainSeconds = 180;
    public double? TokensPerSecond => OutputTokens >= 0 && double.IsFinite(DurationMilliseconds) && DurationMilliseconds > 0
        ? OutputTokens * 1000d / DurationMilliseconds : null;
    public bool IsAvailable(DateTimeOffset now) => TokensPerSecond is not null && now >= SampledAt &&
        now - SampledAt < TimeSpan.FromSeconds(RetainSeconds);
    public bool IsFresh(DateTimeOffset now) => IsAvailable(now) && now - SampledAt < TimeSpan.FromSeconds(FreshSeconds);
}

/// <summary>Fixed-size per-task state. Combine only the matched intervals added in one read.</summary>
internal sealed class OutputRateTracker
{
    private DateTimeOffset? _cursor, _pendingAt;
    private long _pendingOutput;
    private double _pendingDuration;
    public OutputRateSample? Sample { get; private set; }

    public void Anchor(DateTimeOffset at, bool reset = false)
    {
        if (reset) { Sample = null; ClearPending(); }
        _cursor = at;
    }

    public void Observe(DateTimeOffset at, long? output, bool countersReset)
    {
        if (countersReset) { Anchor(at, true); return; }
        if (_cursor is not { } previous) { _cursor = at; return; }
        // Duplicate/backward timestamps cannot provide a corresponding positive duration.
        if (at <= previous) return;
        _cursor = at;
        if (output is null || output < 0) { Sample = null; ClearPending(); return; }
        try { _pendingOutput = checked(_pendingOutput + output.Value); }
        catch (OverflowException) { Sample = null; ClearPending(); return; }
        _pendingDuration += (at - previous).TotalMilliseconds;
        _pendingAt = at;
    }

    public void CompleteRead(DateTimeOffset now, bool initialized)
    {
        if (initialized && _pendingAt is { } at && at <= now && now - at < TimeSpan.FromSeconds(OutputRateSample.RetainSeconds)
            && _pendingDuration > 0 && double.IsFinite(_pendingDuration))
            Sample = new(_pendingOutput, _pendingDuration, at);
        // An initial replay establishes cursors, never a live rate. Unchanged reads hold the last sample.
        if (!initialized || Sample?.IsAvailable(now) == false) Sample = null;
        ClearPending();
    }

    private void ClearPending() { _pendingOutput = 0; _pendingDuration = 0; _pendingAt = null; }
}
