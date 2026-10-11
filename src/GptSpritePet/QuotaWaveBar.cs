using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using GptSpritePet.Core;

namespace GptSpritePet;

internal enum QuotaWaveStyle { FiveHours, SevenDays }

/// <summary>Remaining quota fills left to right; only the trailing edge ripples.</summary>
internal sealed class QuotaWaveBar : FrameworkElement
{
    private readonly DrawingVisual _drawing = new();
    private readonly QuotaWaveStyle _style;
    private double? _target;
    private double _display, _from;
    private bool _current, _animating, _consuming, _taskActive;
    private UsageMetricLevel _level;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _consumptionClock = new();
    private double _lastFrame = -1;
    internal double? RemainingPercent => _target;
    internal bool Animating => _animating;
    internal bool Rippling => _animating && _taskActive && _target is > 0;
    private const double Duration = 1.8;

    internal QuotaWaveBar(QuotaWaveStyle style = QuotaWaveStyle.FiveHours)
    {
        _style = style;
        AddVisualChild(_drawing);
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => Stop();
    }

    internal void SetTaskActive(bool active)
    {
        if (_taskActive == active) return;
        _taskActive = active;
        UpdateAnimation();
        DrawWave();
    }

    internal void SetMetric(UsageMetric metric)
    {
        var target = metric.RemainingPercent is { } value ? Math.Clamp(value, 0, 100) : (double?)null;
        if (_target == target && _current == metric.Current && _level == metric.Level) return;
        var consume = _current && metric.Current && _target.HasValue && target.HasValue && target < _target;
        var from = _display;
        _target = target; _current = metric.Current; _level = metric.Level;
        if (consume && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            _from = from; _consuming = true;
            _consumptionClock.Restart();
        }
        else { _display = target ?? 0; _consuming = false; _consumptionClock.Reset(); }
        UpdateAnimation();
        DrawWave();
    }

    private bool NeedsAnimation => IsVisible && SystemParameters.ClientAreaAnimation &&
        (_consuming || _taskActive && _target is > 0);

    private void UpdateAnimation()
    {
        if (!NeedsAnimation) { Stop(); return; }
        if (_animating) return;
        _animating = true; _lastFrame = -1;
        _clock.Start(); CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        if (!NeedsAnimation) { Stop(); return; }
        if (seconds - _lastFrame < 1d / 30) return;
        _lastFrame = seconds;
        if (_consuming)
        {
            var progress = Math.Min(1, _consumptionClock.Elapsed.TotalSeconds / Duration);
            var eased = progress * progress * (3 - 2 * progress);
            _display = _from + ((_target ?? 0) - _from) * eased;
            if (progress >= 1) { _consuming = false; _consumptionClock.Stop(); }
        }
        if (!NeedsAnimation) { Stop(); return; }
        DrawWave();
    }

    private void Stop()
    {
        if (_animating) CompositionTarget.Rendering -= RenderFrame;
        _animating = false; _consuming = false; _clock.Stop(); _consumptionClock.Stop(); _display = _target ?? 0;
        DrawWave();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0 ? _drawing : throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        DrawWave();
    }

    private void DrawWave()
    {
        // Retained drawing updates do not invalidate the element's arrange/layout pass.
        using var dc = _drawing.RenderOpen();
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 0 || height <= 0 || !_target.HasValue || _display <= 0) return;
        var baseColor = _level switch { UsageMetricLevel.Critical => "#F3D5DE", UsageMetricLevel.Warning => "#F4E4BE",
            UsageMetricLevel.Unknown => "#E6DEED", _ => "#D8C5F0" };
        var edgeColor = _level switch { UsageMetricLevel.Critical => "#DFB2C2", UsageMetricLevel.Warning => "#E3C780",
            UsageMetricLevel.Unknown => "#D2C7DC", _ => "#C5A8E7" };
        var weekly = _style == QuotaWaveStyle.SevenDays;
        var phase = (Rippling ? _clock.Elapsed.TotalSeconds * (weekly ? -1.1 : 1.8) : 0) + (weekly ? 1.6 : 0);
        // A resting quota has a straight edge. Active waves are broad, shallow
        // ripples rather than pronounced crests competing with the text.
        var amplitude = Rippling ? Math.Min(Math.Min(weekly ? 1 : 1.5, width * .012), width * _display / 100 * .4) : 0;
        var points = new Point[33];
        for (var i = 0; i < points.Length; i++)
        {
            var t = i / (double)(points.Length - 1);
            var y = height * t;
            var offset = amplitude * (Math.Sin(t * Math.PI * (weekly ? .9 : 1.3) + phase) +
                .1 * Math.Sin(t * Math.PI * (weekly ? 1.6 : 2) - phase * .4));
            points[i] = new(Math.Clamp(width * _display / 100 + offset, 0, width), y);
        }
        var fill = new StreamGeometry();
        using (var context = fill.Open())
        {
            context.BeginFigure(new(0, 0), true, true);
            foreach (var point in points) context.LineTo(point, true, false);
            context.LineTo(new(0, height), true, false);
        }
        fill.Freeze();
        var band = new StreamGeometry();
        using (var context = band.Open())
        {
            context.BeginFigure(points[0], true, true);
            foreach (var point in points) context.LineTo(point, true, false);
            for (var i = points.Length - 1; i >= 0; i--)
                context.LineTo(new(Math.Max(0, points[i].X - 3), points[i].Y), true, false);
        }
        band.Freeze();
        dc.PushClip(new RectangleGeometry(new(0, 0, width, height), 5, 5));
        dc.DrawGeometry(PetTheme.Brush(baseColor), null, fill);
        dc.PushOpacity(.35); dc.DrawGeometry(PetTheme.Brush(edgeColor), null, band); dc.Pop();
        dc.Pop();
    }
}
