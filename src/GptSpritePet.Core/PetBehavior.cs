namespace GptSpritePet.Core;

public enum PetState { Idle, Walking, Dragging, Happy, Surprised, Thinking, Talking, Sleeping, LyingDown, Waking }
public enum InteractionFrequency { Low, Normal, High }

public sealed class PetBehavior
{
    public const double AutoSleepAfterSeconds = 10 * 60;
    public const double MaximumSleepSeconds = 10 * 60;
    private readonly Random _random;
    private double _remaining = 8;
    private double _sinceInput;
    private double _sleepElapsed;
    private bool _dragActive, _wakeAfterLyingDown;
    private PetState _pendingState = PetState.Idle;
    private double _pendingDuration = 8;
    private WalkingTiming? _walkingTiming;
    private PetState? _afterWalk;
    private double _afterWalkSeconds;
    public WalkingPlayback? WalkingPlayback { get; private set; }
    public string[] MovementActionKeys { get; set; } = [nameof(PetState.Walking)];
    public string? SelectedMovementKey { get; private set; }
    public WalkingTiming? WalkingTiming
    {
        get => _walkingTiming;
        set
        {
            value?.Validate();
            if (_walkingTiming == value) return;
            _walkingTiming = value; WalkingPlayback = null; _afterWalk = null;
            if (State == PetState.Walking) Change(PetState.Idle, 8);
        }
    }
    public double LieDownSeconds { get; set; }
    public double WakeUpSeconds { get; set; }
    public PetState State { get; private set; } = PetState.Idle;
    public bool Paused { get; set; }
    public bool AutoWalk { get; set; } = true;
    public IReadOnlySet<PetState> AvailableStates { get; set; } = Enum.GetValues<PetState>().ToHashSet();
    public double WalkingSpeed { get; set; } = 45;
    public InteractionFrequency Frequency { get; set; } = InteractionFrequency.Normal;
    public int Direction { get; private set; } = 1;
    public double InactiveSeconds => _sinceInput;
    public double SleepElapsedSeconds => _sleepElapsed;
    public event Action<PetState>? StateChanged;

    public PetBehavior(int? seed = null) => _random = seed.HasValue ? new Random(seed.Value) : new Random();

    public void React(PetState state, double seconds = 3)
    {
        if (_dragActive && state != PetState.Dragging) return;
        _sinceInput = 0;
        if (state is PetState.Sleeping or PetState.LyingDown) { BeginRest(); return; }
        if (state == PetState.Waking) { Wake(); return; }
        RequestAwakeState(state, seconds);
    }

    public void BeginDrag() { _sinceInput = 0; _dragActive = true; RequestAwakeState(PetState.Dragging, double.PositiveInfinity); }
    public void EndDrag() { _sinceInput = 0; _dragActive = false; RequestAwakeState(PetState.Idle, 8); }
    public void Wake() { _sinceInput = 0; if (!_dragActive) RequestAwakeState(PetState.Idle, 8); }
    public void NotifyInteraction()
    {
        _sinceInput = 0;
        if (_afterWalk is PetState.Sleeping or PetState.LyingDown)
        { _afterWalk = PetState.Idle; _afterWalkSeconds = 8; }
        if (!_dragActive && State is (PetState.Sleeping or PetState.LyingDown or PetState.Waking))
            RequestAwakeState(PetState.Idle, 8);
    }
    // Reverse travel immediately; the renderer blends the facing change without restarting the walking clock.
    public void Turn() => Direction = -Direction;

    public void AdvanceInactivity(double seconds)
    {
        if (Paused || _dragActive || seconds <= 0 || !double.IsFinite(seconds)) return;
        if (State == PetState.Sleeping)
        {
            _sleepElapsed = Math.Min(MaximumSleepSeconds, _sleepElapsed + seconds);
            if (_sleepElapsed >= MaximumSleepSeconds) Wake();
            return;
        }
        _sinceInput = Math.Min(AutoSleepAfterSeconds, _sinceInput + seconds);
        if (_sinceInput >= AutoSleepAfterSeconds && AvailableStates.Contains(PetState.Sleeping)
            && State is not (PetState.Sleeping or PetState.LyingDown or PetState.Waking)) BeginRest();
    }

    public double Advance(double seconds, bool countInactivity = true)
    {
        if (Paused || State == PetState.Dragging || seconds <= 0 || !double.IsFinite(seconds)) return 0;
        if (countInactivity) AdvanceInactivity(seconds);
        seconds = Math.Min(seconds, 0.25); // Resume never teleports the pet after a blocked UI tick.
        if (State == PetState.Walking && WalkingPlayback is { } playback)
        {
            var movement = Direction * Speed() * playback.Advance(seconds);
            if (!playback.Playing)
            {
                var next = _afterWalk ?? PetState.Idle;
                var duration = _afterWalk.HasValue ? _afterWalkSeconds : IdleDuration();
                _afterWalk = null;
                Change(next, duration);
            }
            return movement;
        }
        _remaining -= seconds;
        if (State == PetState.LyingDown && _remaining <= 1e-9)
        {
            if (_wakeAfterLyingDown) StartWaking();
            else Change(PetState.Sleeping, double.PositiveInfinity);
            return 0;
        }
        if (State == PetState.Waking && _remaining <= 1e-9)
        {
            if (_pendingState == PetState.Sleeping)
            {
                _wakeAfterLyingDown = false;
                Change(ValidDuration(LieDownSeconds) ? PetState.LyingDown : PetState.Sleeping,
                    ValidDuration(LieDownSeconds) ? LieDownSeconds : double.PositiveInfinity);
            }
            else Change(_pendingState, _pendingDuration);
            return 0;
        }
        if (State is PetState.LyingDown or PetState.Waking or PetState.Sleeping) return 0;
        if (_remaining <= 0 && State != PetState.Sleeping)
        {
            if (State != PetState.Idle) Change(PetState.Idle, IdleDuration());
            else
            {
                var choices = AutoWalk
                    ? new[] { PetState.Walking, PetState.Idle, PetState.Thinking, PetState.Surprised }
                    : new[] { PetState.Idle, PetState.Thinking, PetState.Surprised };
                choices = choices.Where(AvailableStates.Contains).ToArray();
                var next = choices.Length > 0 ? choices[_random.Next(choices.Length)] : PetState.Idle;
                if (next == PetState.Walking)
                Direction = _random.Next(2) == 0 ? -1 : 1;
                else if (_walkingTiming is null) Direction = _random.Next(2) == 0 ? -1 : 1;
                Change(next, next == PetState.Idle ? IdleDuration() : _random.Next(3, 7));
            }
        }
        return State == PetState.Walking ? WalkingPlayback is { } started
            ? Direction * Speed() * started.Advance(seconds) : Direction * Speed() * seconds : 0;
    }

    private void BeginRest()
    {
        if (State is PetState.Sleeping or PetState.LyingDown) return;
        if (State == PetState.Waking) { _pendingState = PetState.Sleeping; _pendingDuration = double.PositiveInfinity; return; }
        _wakeAfterLyingDown = false;
        var next = ValidDuration(LieDownSeconds) ? PetState.LyingDown : PetState.Sleeping;
        var seconds = ValidDuration(LieDownSeconds) ? LieDownSeconds : double.PositiveInfinity;
        if (!QueueAfterWalk(next, seconds)) Change(next, seconds);
    }
    private void RequestAwakeState(PetState state, double seconds)
    {
        _pendingState = state; _pendingDuration = seconds;
        if (State == PetState.LyingDown) { _wakeAfterLyingDown = true; return; }
        if (State == PetState.Waking) return;
        if (State == PetState.Sleeping && ValidDuration(WakeUpSeconds)) { StartWaking(); return; }
        if (state != PetState.Dragging && QueueAfterWalk(state, seconds)) return;
        Change(state,seconds);
    }
    private bool QueueAfterWalk(PetState state, double seconds)
    {
        if (State != PetState.Walking || WalkingPlayback is not { Playing: true } playback) return false;
        if (state != PetState.Walking) { _afterWalk = state; _afterWalkSeconds = seconds; playback.RequestStop(); }
        return true;
    }
    private double Speed() => double.IsFinite(WalkingSpeed) ? Math.Clamp(WalkingSpeed, 5, 300) : 45;
    private void StartWaking()
    {
        _wakeAfterLyingDown = false;
        if (ValidDuration(WakeUpSeconds)) Change(PetState.Waking,WakeUpSeconds);
        else Change(_pendingState,_pendingDuration);
    }
    private static bool ValidDuration(double value) => double.IsFinite(value) && value > 0 && value <= 15;

    private double IdleDuration() => Frequency switch
    {
        InteractionFrequency.Low => _random.Next(20, 36),
        InteractionFrequency.High => _random.Next(4, 9),
        _ => _random.Next(9, 18)
    };

    private void Change(PetState state, double duration)
    {
        _remaining = duration;
        if (State == state) return;
        _sleepElapsed = 0;
        // Retain the completed outro until StateChanged captures its last frame for the crossfade.
        if (state == PetState.Walking)
            SelectedMovementKey = MovementActionKeys.Length > 0 ? MovementActionKeys[_random.Next(MovementActionKeys.Length)] : nameof(PetState.Walking);
        else SelectedMovementKey = null;
        if (state == PetState.Walking && SelectedMovementKey == nameof(PetState.Walking) && _walkingTiming is { } timing)
        {
            WalkingPlayback = new(timing, _random.Next()); WalkingPlayback.Begin();
            _remaining = double.PositiveInfinity; _afterWalk = null;
        }
        State = state;
        StateChanged?.Invoke(state);
        if (state != PetState.Walking) { WalkingPlayback = null; _afterWalk = null; }
    }
}
