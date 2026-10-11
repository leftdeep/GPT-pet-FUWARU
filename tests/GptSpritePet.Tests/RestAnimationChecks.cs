using GptSpritePet.Core;

internal static class RestAnimationChecks
{
    internal static void Run(Action<bool,string> check)
    {
        static PetBehavior New() => new(15) { LieDownSeconds = 1, WakeUpSeconds = 1.5, AutoWalk = true };
        static void Step(PetBehavior pet,double seconds)
        {
            while (seconds > 1e-9) { var step = Math.Min(.25,seconds); pet.Advance(step); seconds -= step; }
        }
        static void Sleep(PetBehavior pet) { pet.React(PetState.Sleeping); Step(pet,1); }
        var pet = New(); pet.React(PetState.Sleeping);
        check(pet.State == PetState.LyingDown,"rest starts with the lie-down clip");
        Step(pet,1); check(pet.State == PetState.Sleeping,"lie-down finishes into persistent sleep");
        Step(pet,599.75); check(pet.State == PetState.Sleeping,"sleep loops until ten full minutes have elapsed");
        pet.AdvanceInactivity(.25);
        check(pet.State == PetState.Waking && pet.InactiveSeconds == 0,"sleep limit starts waking and restarts inactivity");
        Step(pet,1.5); Step(pet,1);
        check(pet.State == PetState.Idle,"automatic waking does not immediately re-enter sleep");
        Sleep(pet);
        pet.React(PetState.Happy,2); check(pet.State == PetState.Waking,"click wakes before its happy reaction");
        Step(pet,.75); pet.Wake(); Step(pet,.75);
        check(pet.State == PetState.Idle,"repeated wake does not restart the wake animation");
        Sleep(pet); pet.React(PetState.Walking,4);
        check(pet.State == PetState.Waking && pet.Advance(.25) == 0,"requested walking waits for waking and does not move the sleeping body");
        Step(pet,1.25); check(pet.State == PetState.Walking,"queued walking begins after waking completes");
        pet = New(); Sleep(pet); pet.React(PetState.Happy,2); pet.Paused = true; Step(pet,5);
        check(pet.State == PetState.Waking,"pause freezes the wake transition");
        pet.Paused = false; Step(pet,1.5); Step(pet,1);
        check(pet.State == PetState.Happy,"reaction duration starts after waking rather than while asleep");
        pet = New(); Sleep(pet); pet.BeginDrag(); pet.React(PetState.Surprised); Step(pet,1.5);
        check(pet.State == PetState.Dragging,"drag wakes smoothly and remains protected from reactions");
        pet.EndDrag(); check(pet.State == PetState.Idle,"release after waking returns the drag to idle");
        pet = New(); Sleep(pet); pet.BeginDrag(); Step(pet,.25); pet.EndDrag(); Step(pet,1.25);
        check(pet.State == PetState.Idle,"early drag release does not leave a queued dragging state");
        pet = New(); pet.React(PetState.Sleeping); Step(pet,.25); pet.React(PetState.Happy); Step(pet,.75);
        check(pet.State == PetState.Waking,"interrupting lie-down queues waking without jumping to another pose");
        Step(pet,1.5); check(pet.State == PetState.Happy,"interrupted lie-down resumes the requested reaction");
        pet = New(); Sleep(pet); pet.Wake(); Step(pet,.25); pet.React(PetState.Sleeping); Step(pet,1.25);
        check(pet.State == PetState.LyingDown,"rest requested during waking waits for the standing endpoint");
        Step(pet,1); check(pet.State == PetState.Sleeping,"rest requested during waking returns to persistent sleep");
        pet = New(); pet.AutoWalk = false; Step(pet,601);
        check(pet.State == PetState.LyingDown || pet.State == PetState.Sleeping,"automatic idle timeout uses the rest transition");
        pet = New(); pet.AutoWalk = false; Step(pet,599.75);
        check(pet.State is not (PetState.LyingDown or PetState.Sleeping),"sleep does not start before ten minutes");
        pet.NotifyInteraction(); Step(pet,599.75);
        check(pet.State is not (PetState.LyingDown or PetState.Sleeping),"pet interaction restarts the full ten-minute interval");
        pet.Advance(.25);
        check(pet.State == PetState.LyingDown && pet.InactiveSeconds == 600,"ten-minute boundary starts lying down");
        Step(pet,.75); pet.NotifyInteraction();
        check(pet.State == PetState.Waking && pet.InactiveSeconds == 0,"interaction wakes persistent sleep and resets inactivity");
        pet = New(); pet.AdvanceInactivity(599.75); pet.React(PetState.Walking,5); pet.AdvanceInactivity(600);
        check(pet.State == PetState.LyingDown,"autonomous movement does not suppress sleep after inactivity");
        pet = New(); pet.AvailableStates = new HashSet<PetState> { PetState.Idle, PetState.Walking };
        pet.React(PetState.Walking,5); var delayedMovement = pet.Advance(600);
        check(Math.Abs(delayedMovement) <= 11.25 && pet.InactiveSeconds == 600,
            "delayed frame uses elapsed time for inactivity while keeping movement bounded");
        pet = New(); pet.Paused = true; pet.AdvanceInactivity(600);
        check(pet.InactiveSeconds == 0 && pet.State == PetState.Idle,"pause suspends automatic rest");
        pet.Paused = false; pet.BeginDrag(); pet.AdvanceInactivity(600); pet.EndDrag();
        check(pet.InactiveSeconds == 0 && pet.State == PetState.Idle,"holding a drag counts as ongoing interaction");
        pet = New(); pet.AutoWalk = false; pet.WalkingTiming = new WalkingTiming(24,59,29,55,16);
        pet.React(PetState.Walking); pet.Advance(.25); pet.AdvanceInactivity(600);
        check(pet.State == PetState.Walking,"automatic sleep allows walking to finish its outro");
        pet.NotifyInteraction(); Step(pet,15);
        check(pet.State == PetState.Idle && pet.InactiveSeconds < 600,"input cancels sleep queued behind the walking outro");
        pet = New(); Sleep(pet); Step(pet,599); pet.Paused = true; pet.AdvanceInactivity(600);
        check(pet.State == PetState.Sleeping && pet.SleepElapsedSeconds == 599,"pause freezes the separate sleep limit timer");
        pet.Paused = false; pet.AdvanceInactivity(1);
        check(pet.State == PetState.Waking,"sleep limit resumes after unpausing");
        pet = New(); Sleep(pet); pet.AdvanceInactivity(300); pet.NotifyInteraction(); Step(pet,1.5); Sleep(pet);
        pet.AdvanceInactivity(300);
        check(pet.State == PetState.Sleeping && pet.SleepElapsedSeconds == 300,"each new sleep session starts its own ten-minute timer");
    }
}
