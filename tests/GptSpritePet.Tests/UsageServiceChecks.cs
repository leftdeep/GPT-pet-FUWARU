using GptSpritePet.Core;

internal static class UsageServiceChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pet-local-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new AccountUsageService(directory);
            var local = new TaskCompletionSource<AccountUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += value => { if (value.State == UsageConnectionState.Disabled && value.LocalUsage is not null) local.TrySetResult(value); };
            service.Configure(new PetSettings { UsageEnabled = false });
            var first = await local.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(!first.Busy && first.LocalUsage is not null, "local token statistics update even when account connection is disabled");
            var missing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += value => { if (value.State == UsageConnectionState.MissingClient) missing.TrySetResult(true); };
            service.Configure(new PetSettings { CodexExecutablePath = Path.Combine(directory, "missing.exe") });
            await missing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(service.Snapshot.LocalUsage is not null, "account connection failure preserves local token statistics");
            var stamp = service.Snapshot.LocalUsage!.UpdatedAt;
            var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Changed += value => { if (value.State == UsageConnectionState.Disabled && value.LocalUsage?.UpdatedAt > stamp) resumed.TrySetResult(true); };
            service.Configure(new PetSettings { UsageEnabled = false });
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(service.Snapshot.LocalUsage!.UpdatedAt > stamp, "local token polling resumes after changing account connection settings");
        }
        finally { Directory.Delete(directory, true); }
    }
}
