using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GptSpritePet.Core;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void RunQuotaTaskWaveChecks(Dictionary<string, bool> checks, AccountUsageSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        var active = snapshot with { Quotas = [new("codex", "Codex", 40, 300, now.AddHours(1)),
            new("codex", "Codex", 40, 10080, now.AddDays(6))] };
        SetUsageHover(true); _usageBadge.Visibility = Visibility.Visible;
        UpdateUsage(active); UpdateLayout();
        checks["quota_active_task_starts_both_waves"] = _quotaBars.All(bar => bar.Rippling == SystemParameters.ClientAreaAnimation);
        var top = Canvas.GetTop(_usageBadge);
        var size = _usageBadge.RenderSize;
        var before = _quotaBars.Select(WavePixels).ToArray();
        UpdateUsage(active);
        checks["quota_unchanged_task_poll_keeps_wave_phase"] = _quotaBars.Select((bar, i) => WavePixels(bar).SequenceEqual(before[i])).All(same => same);
        PumpQuotaFrames(.45);
        checks["quota_tasks_ripple_without_consuming_balance"] = _quotaBars.Select((bar, i) =>
            bar.RemainingPercent == 60 && (!SystemParameters.ClientAreaAnimation || !WavePixels(bar).SequenceEqual(before[i]))).All(changed => changed);
        checks["quota_windows_have_distinct_wave_shapes"] = !SystemParameters.ClientAreaAnimation || !WavePixels(_quotaBars[0]).SequenceEqual(WavePixels(_quotaBars[1]));
        checks["quota_active_wave_edges_have_subtle_excursion"] = _quotaBars.All(bar =>
            { var edges = WaveEdges(bar); return edges.Max() - edges.Min() <= 4; });
        // Exceed the old 1.8-second consumption animation even with unchanged quotas.
        PumpQuotaFrames(2);
        checks["quota_task_waves_continue_after_consumption_duration"] = _quotaBars.All(bar => bar.Rippling == SystemParameters.ClientAreaAnimation);
        checks["quota_continuous_waves_keep_card_position_and_layout"] = Math.Abs(Canvas.GetTop(_usageBadge) - top) < .01 && _usageBadge.RenderSize == size
            && _quotaBars.All(bar => bar.IsArrangeValid && bar.IsMeasureValid);
        SaveQuotaPreview("usage-task-waves-preview.png");
        _usageBadge.Visibility = Visibility.Hidden;
        checks["quota_hidden_active_task_unsubscribes_both_waves"] = _quotaBars.All(bar => !bar.Animating);
        _usageBadge.Visibility = Visibility.Visible;
        checks["quota_revealed_active_task_resumes_both_waves"] = _quotaBars.All(bar => bar.Rippling == SystemParameters.ClientAreaAnimation);
        var finished = active with { LocalUsage = active.LocalUsage! with { Tasks = [] } };
        UpdateUsage(finished);
        checks["quota_finished_task_stops_both_waves"] = _quotaBars.All(bar => !bar.Animating);
        checks["quota_finished_task_edges_are_flat"] = _quotaBars.All(bar => WaveEdges(bar).Distinct().Count() == 1);
        checks["quota_flat_edges_match_remaining_balance"] = _quotaBars.All(bar => Math.Abs(WaveEdges(bar)[0] + 1 - bar.ActualWidth * .6) <= 1);
        SaveQuotaPreview("usage-resting-bars-preview.png");
        var still = _quotaBars.Select(WavePixels).ToArray();
        PumpQuotaFrames(.15);
        checks["quota_finished_task_drawing_stays_still"] = _quotaBars.Select((bar, i) => WavePixels(bar).SequenceEqual(still[i])).All(same => same);
        UpdateUsage(active with { LocalUsage = active.LocalUsage! with { Available = false } });
        checks["quota_unavailable_task_status_does_not_run_waves"] = _quotaBars.All(bar => !bar.Animating);
        _usageBadge.Visibility = Visibility.Hidden;
        _quotaBars[0].SetMetric(new("5小时余额", "不限", "", true));
        _quotaBars[1].SetMetric(new("7日余额", "0%", "", true, 0));
        _quotaBars[0].SetTaskActive(true); _quotaBars[1].SetTaskActive(true);
        _usageBadge.Visibility = Visibility.Visible;
        checks["quota_unlimited_and_empty_tasks_have_no_fake_waves"] = _quotaBars.All(bar => !bar.Animating)
            && WaveDrawing(_quotaBars[0]).Drawing?.Bounds.IsEmpty != false && WaveDrawing(_quotaBars[1]).Drawing?.Bounds.IsEmpty != false;
    }

    private static DrawingVisual WaveDrawing(QuotaWaveBar bar) => (DrawingVisual)VisualTreeHelper.GetChild(bar, 0);

    private static byte[] WavePixels(QuotaWaveBar bar)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(WaveDrawing(bar));
        return BitmapPixels(bitmap);
    }

    private static int[] WaveEdges(QuotaWaveBar bar)
    {
        var pixels = WavePixels(bar);
        var width = (int)Math.Ceiling(bar.ActualWidth);
        var height = (int)Math.Ceiling(bar.ActualHeight);
        // Exclude the rounded clip corners and inspect the visible right boundary.
        return Enumerable.Range(6, height - 12).Select(y => Enumerable.Range(0, width)
            .Last(x => pixels[(y * width + x) * 4 + 3] >= 32)).ToArray();
    }

    private void SaveQuotaPreview(string filename)
    {
        var canvas = RenderElement(_canvas);
        SaveBitmap(new CroppedBitmap(canvas, new Int32Rect((int)Canvas.GetLeft(_usageBadge), (int)Canvas.GetTop(_usageBadge),
            (int)Math.Ceiling(_usageBadge.ActualWidth * _settings.UsageBubbleScale), (int)Math.Ceiling(_usageBadge.ActualHeight * _settings.UsageBubbleScale))),
            Path.Combine(_store.DirectoryPath, filename));
    }

    private void PumpQuotaFrames(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}
