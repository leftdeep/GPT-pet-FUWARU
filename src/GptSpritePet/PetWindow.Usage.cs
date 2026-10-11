using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GptSpritePet.Core;
using Microsoft.Win32;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    internal void OpenUsage() => OpenControls(PetControlTab.Usage);
    private void OnAccountUsageChanged(AccountUsageSnapshot snapshot)
    {
        if (!_exiting && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => UpdateUsage(_usageService.Snapshot)));
    }
    private void UpdateUsage(AccountUsageSnapshot snapshot)
    {
        if (_exiting) return;
        if (_controlWindow?.SelectedTab == PetControlTab.Usage)
            _controlWindow.Page<UsagePage>(PetControlTab.Usage)?.Update(snapshot);
        var now = DateTimeOffset.Now;
        var summary = UsageSummary.Create(snapshot, now, _settings.UsageRefreshSeconds);
        var taskActive = summary.Task.Current;
        var metrics = new[] { summary.FiveHours, summary.SevenDays, summary.Today, summary.Task };
        for (var i = 0; i < metrics.Length; i++)
        {
            _usageValues[i].Text = metrics[i].Value;
            _usageValues[i].ToolTip = metrics[i].Hint;
        }
        _outputRateValue.Text = summary.OutputRate.Value;
        _outputRateValue.ToolTip = summary.OutputRate.Hint;
        _outputRateValue.Foreground = PetTheme.Brush(summary.OutputRate.Current ? "#8063AC" : "#94899F");
        _usageBadge.ToolTip = snapshot.Message + "\n点击查看 5小时、7日余额和当前任务详情";
        for (var i = 0; i < 2; i++)
        {
            _usageValues[i].Foreground = PetTheme.Brush(metrics[i].Level switch
            { UsageMetricLevel.Critical => "#C14B4B", UsageMetricLevel.Warning => "#A77E1E",
                UsageMetricLevel.Unknown => "#9C8DAA", _ => "#36303F" });
            _quotaTimes[i].Text = metrics[i].ResetsAt is { } reset ? reset.ToLocalTime().ToString("MM-dd HH:mm") : "";
            _quotaTimes[i].ToolTip = metrics[i].Hint;
            _quotaBars[i].SetMetric(metrics[i]);
            _quotaBars[i].SetTaskActive(taskActive);
        }
    }
}
