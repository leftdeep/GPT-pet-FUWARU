using System.Text.Json;
using System.Text.Json.Serialization;

namespace GptSpritePet.Core;

public enum AnimationPreviewGroup { Idle, ClickInteraction, Walking }

public sealed class SkinDefinition
{
    public string Name { get; set; } = "功能占位素材";
    public int FrameMilliseconds { get; set; } = 120;
    public double DisplayScale { get; set; } = 0.96;
    // Common clean standing height divided by the larger frame dimension; null keeps legacy canvas sizing.
    public double? StandingHeightRatio { get; set; }
    public WalkingSequenceDefinition? WalkingSequence { get; set; }
    public AnimationTransitionDefinition? Transitions { get; set; }
    // Kept for existing skin packs. An explicitly configured Clip takes precedence.
    public Dictionary<string, string[]> Animations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AnimationClip> Clips { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] BlinkFrames { get; set; } = [];
    public int BlinkFrameMilliseconds { get; set; } = 80;
    public int BlinkIntervalMilliseconds { get; set; } = 4600;

    public static SkinDefinition Load(string directory)
    {
        var manifest = Path.Combine(directory, "skin.json");
        if (new FileInfo(manifest).Length > 64 * 1024) throw new InvalidDataException("素材配置文件过大。");
        var skin = JsonSerializer.Deserialize<SkinDefinition>(File.ReadAllText(manifest), SettingsStore.JsonOptions)
            ?? throw new InvalidDataException("素材配置为空。");
        if (string.IsNullOrWhiteSpace(skin.Name) || skin.Name.Length > 80) throw new InvalidDataException("素材名称无效。");
        if (!double.IsFinite(skin.DisplayScale) || skin.DisplayScale is < 0.5 or > 1)
            throw new InvalidDataException("角色显示比例必须是 0.5～1 之间的有限数值。");
        if (skin.StandingHeightRatio is { } standingRatio && (!double.IsFinite(standingRatio) || standingRatio is < 0.1 or > 1))
            throw new InvalidDataException("站立高度比例必须是 0.1～1 之间的有限数值。");
        skin.FrameMilliseconds = Math.Clamp(skin.FrameMilliseconds, 60, 2000);
        skin.BlinkFrameMilliseconds = Math.Clamp(skin.BlinkFrameMilliseconds, 40, 200);
        skin.BlinkIntervalMilliseconds = Math.Clamp(skin.BlinkIntervalMilliseconds, 2000, 15000);
        skin.Animations = new(skin.Animations ?? [], StringComparer.OrdinalIgnoreCase);
        skin.Clips = new(skin.Clips ?? [], StringComparer.OrdinalIgnoreCase);
        if (skin.BlinkFrames is null || skin.BlinkFrames.Length > 4) throw new InvalidDataException("眨眼素材最多使用 4 帧。");
        var total = skin.BlinkFrames.Length;
        foreach (var frame in skin.BlinkFrames) ResolveFrame(directory, frame);
        foreach (var (state, frames) in skin.Animations)
        {
            ValidateState(state);
            if (frames is null) throw new InvalidDataException("动作帧列表不能为空。");
            total += frames.Length;
            foreach (var frame in frames) ResolveFrame(directory, frame);
        }
        foreach (var (state, clip) in skin.Clips)
        {
            ValidateState(state);
            if (clip is null) throw new InvalidDataException("动作配置不能为空。");
            if (clip.WalkingStride.HasValue && !state.Equals(nameof(PetState.Walking), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("整帧行走定位只能用于 Walking。");
            clip.Validate(directory);
            if (clip.Video is null) total += clip.FrameCount;
        }
        if (total > 256) throw new InvalidDataException("素材包最多使用 256 个播放帧，解码缓存仍限制为 80 MB。");
        skin.WalkingSequence?.Timing(skin.NamedClips());
        skin.Transitions?.Validate(skin.NamedClips());
        return skin;
    }

    public Dictionary<PetState, AnimationClip> EffectiveClips()
    {
        var result = new Dictionary<PetState, AnimationClip>();
        foreach (var (name, clip) in NamedClips())
            if (Enum.TryParse<PetState>(name, true, out var state) && Enum.IsDefined(state)) result[state] = clip;
        return result;
    }

    public Dictionary<string, AnimationClip> NamedClips()
    {
        var result = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, frames) in Animations)
            if (frames.Length > 0) result[name] = new() { Frames = frames, FrameMilliseconds = FrameMilliseconds };
        foreach (var (name, clip) in Clips) result[name] = clip;
        return result;
    }

    private static void ValidateState(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')) || char.IsDigit(value[0]))
            throw new InvalidDataException("动作名称需要是 1～64 个字母、汉字、数字、下划线或短横线，且不能以数字开头。");
    }

    public static string ResolveFrame(string directory, string relativePath)
        => ResolveAsset(directory, relativePath, ".png");

    public static string ResolveVideo(string directory, string relativePath)
        => ResolveAsset(directory, relativePath, ".webm");

    private static string ResolveAsset(string directory, string relativePath, string extension)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':')
            || !string.Equals(Path.GetExtension(relativePath), extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("动画素材必须是素材目录内的 " + extension + " 文件。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("动画帧超出素材目录。");
        // Reject links/junctions, including parent folders: lexical containment alone is insufficient.
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("动画帧不能使用指向其他位置的链接。");
        }
        return path;
    }
}

public sealed class AnimationClip
{
    public string? DisplayName { get; set; }
    public AnimationPreviewGroup? PreviewGroup { get; set; }
    public string? Video { get; set; }
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public int VideoFrameCount { get; set; }
    public bool StreamFrames { get; set; }
    public bool DisableProceduralMotion { get; set; }
    public AnimationAlignment? Alignment { get; set; }
    public int[] BlinkFrameIndices { get; set; } = [];
    public string[] Frames { get; set; } = [];
    public string? SpriteSheet { get; set; }
    public string[] SpriteSheets { get; set; } = [];
    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;
    public int[] FrameIndices { get; set; } = [];
    public int FrameMilliseconds { get; set; } = 120;
    public double? FrameRate { get; set; }
    [JsonIgnore]
    public double EffectiveFrameMilliseconds => FrameRate is { } fps ? 1000d / fps : FrameMilliseconds;
    public bool Loop { get; set; } = true;
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.96;
    public FrameAnchor[] FrameAnchors { get; set; } = [];
    public double? WalkingStride { get; set; }
    public FrameAnchor[] FootContacts { get; set; } = [];
    public int[] ContactLegIds { get; set; } = [];
    [JsonIgnore]
    public int FrameCount => Video is not null ? VideoFrameCount : SpriteSheet is null && SpriteSheets.Length == 0 ? Frames.Length
        : FrameIndices.Length == 0 ? Columns * Rows * (SpriteSheet is null ? SpriteSheets.Length : 1) : FrameIndices.Length;

    internal void Validate(string directory)
    {
        Alignment?.Validate();
        if (PreviewGroup is { } group && !Enum.IsDefined(group))
            throw new InvalidDataException("动作预览分组无效。");
        if (DisplayName is { } label && (string.IsNullOrWhiteSpace(label) || label.Length > 40 || label.Any(char.IsControl)))
            throw new InvalidDataException("动作显示名称需要是 1～40 个可见字符。");
        if (Frames is null || SpriteSheets is null || FrameIndices is null || FrameAnchors is null || FootContacts is null || ContactLegIds is null || BlinkFrameIndices is null) throw new InvalidDataException("动作帧列表不能为空。");
        if (BlinkFrameIndices.Any(index => index < 0 || index >= FrameCount)) throw new InvalidDataException("内嵌眨眼帧超出动作范围。");
        if (StreamFrames && (SpriteSheet is not null || SpriteSheets.Length > 0 || WalkingStride.HasValue))
            throw new InvalidDataException("按需解码用于独立整帧 PNG；测量行走和图集继续预加载。");
        if (FrameRate is { } fps && (!double.IsFinite(fps) || fps is < 1 or > 60))
            throw new InvalidDataException("视频帧率必须是 1～60 之间的有限数值。");
        if (SpriteSheet is not null && SpriteSheets.Length > 0) throw new InvalidDataException("单图集与多图集不能同时配置。");
        if (!double.IsFinite(AnchorX) || !double.IsFinite(AnchorY) || AnchorX is < 0 or > 1 || AnchorY is < 0 or > 1)
            throw new InvalidDataException("动作定位点必须是 0～1 之间的有限数值。");
        FrameMilliseconds = Math.Clamp(FrameMilliseconds, 60, 2000);
        if (Video is not null)
        {
            SkinDefinition.ResolveVideo(directory, Video);
            if (Frames.Length > 0 || SpriteSheet is not null || SpriteSheets.Length > 0 || FrameIndices.Length > 0 || StreamFrames)
                throw new InvalidDataException("同一动作只能选择透明 WebM、PNG 序列或图集中的一种。");
            if (VideoWidth is < 1 or > 4096 || VideoHeight is < 1 or > 4096 || VideoFrameCount is < 1 or > 3600 || FrameRate is null)
                throw new InvalidDataException("WebM 动作需要有效的视频宽高、1～3600 的帧数和帧率。");
            ValidateFrameAnchors();
            return;
        }
        if (SpriteSheet is null && SpriteSheets.Length == 0)
        {
            if (Frames.Length == 0 || FrameIndices.Length > 0) throw new InvalidDataException("动作必须提供 PNG 帧，或提供图集及帧索引。");
            foreach (var frame in Frames) SkinDefinition.ResolveFrame(directory, frame);
            ValidateFrameAnchors();
            return;
        }
        if (Frames.Length > 0) throw new InvalidDataException("同一动作不能同时指定独立 PNG 和图集。");
        var sheets = SpriteSheet is { } single ? new[] { single } : SpriteSheets;
        foreach (var sheet in sheets) SkinDefinition.ResolveFrame(directory, sheet);
        if (sheets.Length > 16 || Columns is < 1 or > 16 || Rows is < 1 or > 16 || Columns * Rows * sheets.Length > 128)
            throw new InvalidDataException("图集行列数无效，最多使用 128 个格子。");
        if (FrameIndices.Any(index => index < 0 || index >= Columns * Rows * sheets.Length))
            throw new InvalidDataException("图集帧索引超出格子范围。");
        ValidateFrameAnchors();
    }

    private void ValidateFrameAnchors()
    {
        if (FrameAnchors.Length != 0 && FrameAnchors.Length != FrameCount)
            throw new InvalidDataException("逐帧定位点数量必须与动作帧数量相同。");
        foreach (var anchor in FrameAnchors)
            if (anchor is null || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y) || anchor.X is < 0 or > 1 || anchor.Y is < 0 or > 1)
                throw new InvalidDataException("逐帧定位点必须是 0～1 之间的有限数值。");
        if (WalkingStride is { } stride)
        {
            if (!double.IsFinite(stride) || stride is < 32 or > 192 || !Loop || FrameCount < 8 || (FrameCount % 2 != 0 && ContactLegIds.Length == 0)
                || FootContacts.Length != FrameCount || FrameAnchors.Length != FrameCount
                || stride / (FrameCount * EffectiveFrameMilliseconds / 1000d) * 360 / 256 > 300)
                throw new InvalidDataException("整帧步态需要逐帧脚底与定位点、32～192 的有效步幅；奇数帧视频须标定支撑腿。");
        }
        else if (FootContacts.Length > 0 || ContactLegIds.Length > 0) throw new InvalidDataException("脚底定位需要配置行走步幅。");
        if (ContactLegIds.Length > 0 && (ContactLegIds.Length != FrameCount || ContactLegIds.Any(id => id is < 0 or > 1)
            || !ContactLegIds.Contains(0) || !ContactLegIds.Contains(1)
            || Enumerable.Range(0, FrameCount).Count(i => ContactLegIds[i] != ContactLegIds[(i + 1) % FrameCount]) != 2))
            throw new InvalidDataException("支撑腿标定须与帧数一致，并在单个走路周期内交替两次。");
        foreach (var contact in FootContacts)
            if (contact is null || !double.IsFinite(contact.X) || !double.IsFinite(contact.Y)
                || contact.X is <= 0 or >= 1 || contact.Y is <= 0 or >= 1)
                throw new InvalidDataException("脚底接触点必须在帧内。");
    }
}

public sealed class FrameAnchor
{
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.96;
}
