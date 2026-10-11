using GptSpritePet.Core;

internal static class CacheChecks
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pet-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        string Write(string name, int size, int age)
        {
            var path = Path.Combine(directory, name); File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, now.AddDays(-age).UtcDateTime); return path;
        }
        try
        {
            var asset = Write("source.webm", 10, 90);
            var expired = Write("vp9-bgra-v1-expired.raw", 100, 40);
            var old = Write("vp9-bgra-v1-older.raw", 100, 10);
            var active = Write("vp9-bgra-v1-active.raw", 100, 0);
            using (var pinned = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var trimmed = WebmCacheMaintenance.Trim(directory, 50, now);
                check(!File.Exists(expired) && !File.Exists(old), "cache cleanup removes expired and oldest unused frames");
                check(File.Exists(active) && trimmed.BytesAfter == 100, "open animation cache is retained even when budget cannot yet be met");
                check(File.Exists(asset), "cache cleanup never deletes source animation assets");
            }
            check(WebmCacheMaintenance.Trim(directory, 50, now).BytesAfter == 0, "released cache handles allow later budget enforcement");
            Write("vp9-bgra-v1-small.raw", 20, 0);
            check(WebmCacheMaintenance.Trim(directory, 100, now).RemovedFiles == 0, "fresh cache within budget is reused");
        }
        finally
        {
            var boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            Directory.Delete(directory, true);
        }
    }
}
