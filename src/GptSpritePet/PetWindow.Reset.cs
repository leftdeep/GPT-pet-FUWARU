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
    internal void ToggleResetMonitoring() => ApplySettings(_settings with { ResetMonitoringEnabled = !_settings.ResetMonitoringEnabled });
    internal void OpenResetHistory() => OpenControls(PetControlTab.ResetHistory);
    internal void CheckResetNow()
    {
        _ = _resetService.CheckAsync(true);
    }
    private void UpdateReset(ResetWatchSnapshot snapshot)
    {
        if (_exiting) return;
        if (_controlWindow?.SelectedTab == PetControlTab.ResetHistory)
            _controlWindow.Page<ResetHistoryPage>(PetControlTab.ResetHistory)?.Update(snapshot);
        if (snapshot.LastReset?.Key is { } resetKey && resetKey != _lastPublicResetKey)
        {
            _lastPublicResetKey = resetKey;
            if (snapshot.Pending.Any(e => e.Kind == ResetNoticeKind.Completed)) _usageService.RequestRefresh();
        }
        if (!snapshot.IsOwner) return;
        var notice = snapshot.Pending.FirstOrDefault();
        if (notice is null) { _resetAlert?.Close(); _resetAlert = null; return; }
        if (_resetAlert is null)
        {
            _resetAlert = new ResetAlertWindow(key => { _ = _resetService.AcknowledgeAsync(key); });
            _resetAlert.PositionRequested += PositionResetAlert;
            _resetAlert.Update(notice, snapshot.Pending.Length); _resetAlert.Show();
        }
        else _resetAlert.Update(notice, snapshot.Pending.Length);
        PositionResetAlert();
    }
    private void PositionResetAlert()
    {
        if (_resetAlert?.IsVisible == true && _handle != IntPtr.Zero)
            _resetAlert.Place(CharacterScreenBounds(), CharacterWorkArea());
    }
}
