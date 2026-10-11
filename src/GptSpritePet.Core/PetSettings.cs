using System.Text.Json;
using System.Text.Json.Serialization;

namespace GptSpritePet.Core;

public sealed record PetSettings
{
    public int SchemaVersion { get; init; } = 1;
    public int Size { get; init; } = 240;
    public double? X { get; init; }
    public double? Y { get; init; }
    public bool Topmost { get; init; } = true;
    public bool ClickThrough { get; init; }
    public bool AutoWalk { get; init; } = true;
    public bool Paused { get; init; }
    public ScreenEdge EdgePeek { get; init; }
    public InteractionFrequency Frequency { get; init; } = InteractionFrequency.Normal;
    public string? SkinDirectory { get; init; }
    public bool UsageEnabled { get; init; } = true;
    public bool UsageCardVisible { get; init; } = true;
    public double UsageBubbleScale { get; init; } = 1.25;
    public bool UsageLowAlerts { get; init; } = true;
    public bool GrowthNotifications { get; init; } = true;
    public bool ResetMonitoringEnabled { get; init; } = true;
    public int UsageRefreshSeconds { get; init; } = 60;
    public string? CodexExecutablePath { get; init; }

    public PetSettings Normalize() => this with
    {
        SchemaVersion = 1,
        Size = Math.Clamp(Size, 160, 360),
        UsageBubbleScale = double.IsFinite(UsageBubbleScale) ? Math.Clamp(UsageBubbleScale, 1, 2) : 1.25,
        X = X.HasValue && double.IsFinite(X.Value) ? X : null,
        Y = Y.HasValue && double.IsFinite(Y.Value) ? Y : null,
        Frequency = Enum.IsDefined(Frequency) ? Frequency : InteractionFrequency.Normal,
        EdgePeek = Enum.IsDefined(EdgePeek) ? EdgePeek : ScreenEdge.None,
        SkinDirectory = string.IsNullOrWhiteSpace(SkinDirectory) ? null : SkinDirectory,
        UsageRefreshSeconds = Math.Clamp(UsageRefreshSeconds, 30, 300),
        CodexExecutablePath = string.IsNullOrWhiteSpace(CodexExecutablePath) ? null : CodexExecutablePath.Trim()
    };

}

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? LastWarning { get; private set; }
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PetSettings Load()
    {
        LastWarning = null;
        if (!File.Exists(FilePath)) return new();
        try
        {
            if (new FileInfo(FilePath).Length > 64 * 1024) throw new InvalidDataException("设置文件过大");
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<PetSettings>(json, JsonOptions)
                ?? throw new InvalidDataException("设置内容为空");
            // Migrate the removed idle lock without retaining it in future saves.
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.EnumerateObject().Any(p => p.Name.Equals("HoldIdle", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True))
                settings = settings with { AutoWalk = true };
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            LastWarning = "设置文件无法读取，已使用默认设置。";
            try { File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true); }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException) { }
            return new();
        }
    }

    public void Save(PetSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), JsonOptions));
        File.Move(temporary, FilePath, true);
    }
}
