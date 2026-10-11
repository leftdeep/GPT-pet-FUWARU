namespace GptSpritePet.Core;

public sealed record WebmCacheCleanup(long BytesBefore, long BytesAfter, int RemovedFiles);

/// <summary>Migration cleanup for the retired raw disk cache; never touch assets or open clips.</summary>
public static class WebmCacheMaintenance
{
    public const long DefaultBudget = 1024L * 1024 * 1024;
    public static WebmCacheCleanup Trim(string directory, long budget = DefaultBudget,
        DateTimeOffset? now = null, TimeSpan? maxAge = null)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        if (!Directory.Exists(directory)) return new(0, 0, 0);
        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - (maxAge ?? TimeSpan.FromDays(30));
        // Only this decoder's cache format is eligible; no recursive deletion.
        var files = new DirectoryInfo(directory).EnumerateFiles("vp9-bgra-v1-*.raw")
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var before = files.Sum(f => f.Length); var remaining = before; var removed = 0;
        foreach (var file in files)
        {
            if (remaining <= budget && file.LastWriteTimeUtc >= cutoff) continue;
            try { var length = file.Length; file.Delete(); remaining -= length; removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(before, remaining, removed);
    }
}
