using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GptSpritePet.Core;

namespace GptSpritePet;

internal sealed partial class SpriteView
{
    private readonly Dictionary<ScreenEdge, Frame> _peekFrames = new();
    private readonly Dictionary<ScreenEdge, double> _peekHeadHeights = new();
    private long _peekBytes;
    internal ScreenEdge EdgePeek { get; private set; }
    internal double PeekSize { get; set; } = 240;
    private sealed record PeekDefinition(string? Atlas, Dictionary<string, int[]>? Regions, Dictionary<string, string>? Frames = null,
        Dictionary<string, double>? HeadHeights = null);

    internal double PeekViewportSize => _peekFrames.TryGetValue(EdgePeek, out var frame)
        ? Math.Max(frame.VisiblePixels.Width, frame.VisiblePixels.Height) * PeekSize / _peekHeadHeights[EdgePeek] + 24 : PeekSize * 1.5;
    internal double PeekHeadHeight => _peekFrames.TryGetValue(EdgePeek, out var frame)
        ? _peekHeadHeights[EdgePeek] * PeekImageBounds(frame).Height / frame.Image.PixelHeight : 0;

    internal bool SetEdgePeek(ScreenEdge edge)
    {
        if (edge != ScreenEdge.None && _peekFrames.Count == 0)
        {
            try { LoadPeekFrames(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or FileFormatException)
            { _peekFrames.Clear(); _peekHeadHeights.Clear(); _peekBytes = 0; return false; }
        }
        if (edge != ScreenEdge.None && !_peekFrames.ContainsKey(edge)) return false;
        EdgePeek = edge;
        ClearTransition(); InvalidateVisual();
        return true;
    }

    private void LoadPeekFrames()
    {
        var folder = Path.Combine(ProjectPaths.Root, "assets", "edge-peek");
        var definition = JsonSerializer.Deserialize<PeekDefinition>(File.ReadAllText(Path.Combine(folder, "peek.json")))
            ?? throw new InvalidDataException("偷看素材定义为空。");
        BitmapSource Load(string file)
        {
            if (string.IsNullOrEmpty(file) || Path.GetFileName(file) != file)
                throw new InvalidDataException("偷看素材路径无效。");
            var path = Path.Combine(folder, file);
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("偷看素材文件过大。");
            using var stream = File.OpenRead(path);
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream; source.EndInit(); source.Freeze();
            if (source.PixelWidth is <= 0 or > 4096 || source.PixelHeight is <= 0 or > 4096)
                throw new InvalidDataException("偷看素材图片尺寸无效。");
            return source;
        }
        BitmapSource? atlas = null;
        foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            BitmapSource source;
            if (definition.Frames?.TryGetValue(edge.ToString(), out var file) == true)
                source = Load(file);
            else
            {
                atlas ??= Load(definition.Atlas ?? "");
                if (definition.Regions is null || !definition.Regions.TryGetValue(edge.ToString(), out var region) || region.Length != 4 ||
                    region[0] < 0 || region[1] < 0 || region[2] <= 0 || region[3] <= 0 ||
                    region[0] + region[2] > atlas.PixelWidth || region[1] + region[3] > atlas.PixelHeight)
                    throw new InvalidDataException("偷看素材裁取区域无效。");
                source = new CroppedBitmap(atlas, new Int32Rect(region[0], region[1], region[2], region[3]));
            }
            // Crown-to-chin height excludes horns, ornaments and trailing hair. Metadata is in source pixels.
            if (definition.HeadHeights?.TryGetValue(edge.ToString(), out var headHeight) != true ||
                !double.IsFinite(headHeight) || headHeight <= 0 || headHeight > source.PixelHeight)
                throw new InvalidDataException("偷看素材缺少有效的头部尺寸标定。");
            var scale = Math.Min(1, 384d / Math.Max(source.PixelWidth, source.PixelHeight));
            var sized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var converted = new FormatConvertedBitmap(sized, PixelFormats.Pbgra32, null, 0);
            var stride = converted.PixelWidth * 4; var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            // Detach each small frame from the original image to keep retained memory bounded.
            var image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            image.Freeze();
            _peekFrames[edge] = DecodedFrame(image, pixels, stride, "peek:" + edge);
            _peekHeadHeights[edge] = headHeight * image.PixelHeight / source.PixelHeight;
            _peekBytes += pixels.Length * 2L;
        }
    }

    private Rect PeekImageBounds(Frame frame)
    {
        var pixels = frame.VisiblePixels;
        var factor = PeekSize / _peekHeadHeights[EdgePeek];
        factor = Math.Min(factor, Math.Min(ActualWidth / pixels.Width, ActualHeight / pixels.Height));
        return new(ActualWidth / 2 - (pixels.X + pixels.Width / 2) * factor,
            ActualHeight / 2 - (pixels.Y + pixels.Height / 2) * factor, frame.Image.PixelWidth * factor, frame.Image.PixelHeight * factor);
    }
}
