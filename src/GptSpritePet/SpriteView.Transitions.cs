using System;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GptSpritePet.Core;

namespace GptSpritePet;

internal sealed partial class SpriteView
{
    private AnimationTransitionDefinition? _transitions;
    private AnimationAlignment? _transitionAlignment;
    private int _idleFrameOffset;
    private WriteableBitmap? _blendImage;
    private byte[]? _blendBefore, _blendPixels;
    private double _blendedAt = double.NaN;
    private string? _blendedFingerprint;
    private Size _blendedViewport;
    private int _blendedDirection;
    private double TransitionSeconds => _transitions?.DurationSeconds ?? .2;
    internal bool HasSmoothTransitions => UsesImageFrames;
    internal bool TransitionActive => _transitionFrame is not null;
    internal int IdleEntryFrame => _transitions?.IdleEntryFrame ?? 0;

    private AnimationAlignment? TransitionAlignment(PetState before, PetState after) => (before, after) switch
    {
        (PetState.Idle, PetState.Walking) => _transitions?.IdleToWalking,
        (PetState.Walking, PetState.Idle) => _transitions?.WalkingToIdle,
        _ => null
    };

    private void ResetBlendCache()
    {
        _blendImage = null; _blendBefore = null; _blendPixels = null;
        _blendedAt = double.NaN; _blendedFingerprint = null;
    }
    private void ClearTransition()
    {
        _transitionFrame = null; _transitionAlignment = null;
        _transitionTime = TransitionSeconds; ResetBlendCache();
    }
    private void CaptureTransitionSource()
    {
        var current = CurrentFrame();
        if (current is null || ActualWidth <= 0 || ActualHeight <= 0) { ClearTransition(); return; }
        if (_transitionFrame is not null)
        {
            // If interrupted, continue from the visible composite instead of jumping to an unmixed frame.
            var blended = TransitionImage(current).Clone(); blended.Freeze();
            var pixels = (byte[])_blendPixels!.Clone();
            _transitionFrame = DecodedFrame(blended, pixels, blended.PixelWidth * 4,
                "transition|" + Convert.ToHexString(SHA256.HashData(pixels)));
            _transitionBounds = new Rect(0, 0, ActualWidth, ActualHeight); _transitionMatrix = Matrix.Identity;
        }
        else
        {
            _transitionFrame = current; _transitionBounds = ImageBounds(current); _transitionMatrix = ImageTransform(current);
        }
        _transitionTime = 0; ResetBlendCache();
    }

    private Matrix CalibratedTransform(Matrix matrix, Frame frame)
    {
        var bounds = ImageBounds(frame);
        void Apply(AnimationAlignment alignment, double weight)
        {
            var scale = 1 + (alignment.Scale - 1) * weight;
            var offsetX = Direction < 0 ? alignment.LeftOffsetX ?? alignment.OffsetX : alignment.OffsetX;
            matrix.ScaleAt(scale, scale, bounds.X + bounds.Width * alignment.PivotX,
                bounds.Y + bounds.Height * alignment.PivotY);
            matrix.Translate(offsetX * bounds.Width * weight, alignment.OffsetY * bounds.Height * weight);
        }
        if ((_presentedClip ?? FindClip(State))?.Alignment is { } fixedAlignment) Apply(fixedAlignment, 1);
        if (_transitionFrame is not null && _transitionAlignment is { } residual)
            Apply(residual, 1 - AnimationTransition.SmoothStep(_transitionTime / TransitionSeconds));
        return matrix;
    }

    private byte[] RenderTransitionLayer(Frame frame, Rect bounds, Matrix matrix, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(width / ActualWidth, height / ActualHeight));
            dc.PushTransform(new MatrixTransform(matrix));
            dc.DrawImage(frame.Image, bounds); dc.Pop(); dc.Pop();
        }
        var surface = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        surface.Render(visual);
        var pixels = new byte[width * height * 4]; surface.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private WriteableBitmap TransitionImage(Frame current)
    {
        // Bounded buffers also support legacy skins with unusually large transparent viewports.
        var ratio = Math.Min(1, 1024 / Math.Max(ActualWidth, ActualHeight));
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * ratio));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * ratio));
        var viewport = new Size(ActualWidth, ActualHeight);
        if (_blendImage is null || _blendImage.PixelWidth != width || _blendImage.PixelHeight != height
            || _blendedViewport != viewport)
        {
            _blendImage = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            _blendBefore = RenderTransitionLayer(_transitionFrame!, _transitionBounds, _transitionMatrix, width, height);
            _blendPixels = new byte[width * height * 4]; _blendedAt = double.NaN; _blendedViewport = viewport;
        }
        if (_blendedAt != _transitionTime || _blendedFingerprint != current.Fingerprint || _blendedDirection != Direction)
        {
            var after = RenderTransitionLayer(current, ImageBounds(current), ImageTransform(current), width, height);
            AnimationTransition.BlendPremultiplied(_blendBefore!, after, _blendPixels!,
                AnimationTransition.SmoothStep(_transitionTime / TransitionSeconds));
            _blendImage.WritePixels(new Int32Rect(0, 0, width, height), _blendPixels!, width * 4, 0);
            _blendedAt = _transitionTime; _blendedFingerprint = current.Fingerprint; _blendedDirection = Direction;
        }
        return _blendImage;
    }
}
