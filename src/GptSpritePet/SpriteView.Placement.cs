using System;
using System.Windows;
using System.Windows.Media;

namespace GptSpritePet;

internal sealed partial class SpriteView
{
    private DrawingGroup? _placementDrawing;
    private Vector _placementOrigin;
    private int _placementDepth;
    internal bool PlacementPresentationHeld => _placementDepth > 0;

    internal IDisposable HoldPlacementPresentation()
    {
        if (_placementDepth == 0 && ActualWidth > 0 && ActualHeight > 0)
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open()) DrawFrame(dc);
            // Clone mutable transition images before freezing the snapshot. Freezing the
            // live drawing also freezes the blend buffer, which the next frame must update.
            _placementDrawing = (DrawingGroup)drawing.GetCurrentValueAsFrozen();
            _placementOrigin = VisualTreeHelper.GetOffset(this);
        }
        _placementDepth++;
        return new PlacementPresentation(this);
    }

    private bool DrawPlacementPresentation(DrawingContext dc)
    {
        if (_placementDrawing is null) return false;
        // Layout may render during a native window resize. Keep the previous drawing at its
        // original canvas position until the new geometry has been moved to the screen edge.
        var offset = _placementOrigin - VisualTreeHelper.GetOffset(this);
        dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
        dc.DrawDrawing(_placementDrawing); dc.Pop();
        return true;
    }

    private sealed class PlacementPresentation(SpriteView owner) : IDisposable
    {
        private SpriteView? _owner = owner;
        public void Dispose()
        {
            if (_owner is not { } sprite) return;
            _owner = null;
            if (--sprite._placementDepth != 0) return;
            sprite._placementDrawing = null;
            sprite.InvalidateVisual();
        }
    }
}
