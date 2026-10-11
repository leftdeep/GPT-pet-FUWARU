using GptSpritePet.Core;

internal static class AutoWalkChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var pet = new PetBehavior(15) { AvailableStates = new HashSet<PetState> { PetState.Idle, PetState.Walking } };
        double movement = 0;
        for (var i = 0; i < 2400; i++) movement += Math.Abs(pet.Advance(.25));
        check(movement > 0 && pet.State is PetState.Idle or PetState.Walking, "automatic movement works with idle and walking states only");
        pet.AutoWalk = false; pet.Wake();
        movement = 0;
        for (var i = 0; i < 2400; i++) movement += Math.Abs(pet.Advance(.25));
        check(movement == 0 && pet.State == PetState.Idle, "turning off automatic movement stops scheduling walks");
        var directory = Path.Combine(Path.GetTempPath(), "pet-walk-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            File.WriteAllText(store.FilePath, "{\"AutoWalk\":false,\"HoldIdle\":true,\"UsageEnabled\":true,\"Size\":300}");
            var migrated = store.Load();
            check(migrated.AutoWalk && migrated.UsageEnabled && migrated.Size == 300, "legacy idle lock migrates to automatic walking without changing other preferences");
            store.Save(migrated);
            check(!File.ReadAllText(store.FilePath).Contains("HoldIdle"), "removed idle lock is absent from new saves");
            store.Save(migrated with { AutoWalk = false });
            check(!store.Load().AutoWalk, "explicitly disabled automatic walking survives restart");
        }
        finally { Directory.Delete(directory, true); }
    }
}
