using GptSpritePet.Core;

internal static class MovementSelectionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var seen = new HashSet<string>();
        var behavior = new PetBehavior(42) { MovementActionKeys = ["Walking", "Flying", "Hopping"],
            WalkingTiming = new(24, 59, 29, 55) };
        bool timedWalk = true, moving = true, pause = true;
        for (var i = 0; i < 120; i++)
        {
            behavior.BeginDrag(); behavior.EndDrag(); behavior.React(PetState.Walking);
            seen.Add(behavior.SelectedMovementKey!);
            timedWalk &= (behavior.WalkingPlayback is not null) == (behavior.SelectedMovementKey == "Walking");
            if (behavior.SelectedMovementKey != "Walking") moving &= behavior.Advance(.2) != 0;
            behavior.Paused = true; pause &= behavior.Advance(.2) == 0; behavior.Paused = false;
        }
        check(seen.SetEquals(["Walking","Flying","Hopping"]), "all registered movement actions participate in random selection");
        check(timedWalk, "only authored walking uses the segmented gait clock");
        check(moving && pause, "generic future movement travels and obeys explicit pause");
    }
}
