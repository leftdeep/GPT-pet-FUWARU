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
    internal void ApplySettings(PetSettings settings)
    {
        using var presentation = _sprite.HoldPlacementPresentation();
        StopPreview();
        var oldCharacter = _handle == IntPtr.Zero ? default : CharacterScreenBounds();
        var oldArea = _handle == IntPtr.Zero ? default : CharacterWorkArea();
        var oldSkin = _settings.SkinDirectory;
        var requestedSkin = settings.SkinDirectory;
        _settings = settings.Normalize(); ApplyView();
        if (_loadedOnce && !PetApplication.SmokeTest) _resetService.Configure(_settings.ResetMonitoringEnabled);
        if (_skinLoadWarning != null && oldSkin != requestedSkin)
        {
            MessageBox.Show(_controlWindow ?? (Window)this, _skinLoadWarning, "素材加载提示");
        }
        if (_handle != IntPtr.Zero)
        {
            UpdateLayout();
            if (_peekEdge != ScreenEdge.None)
                AttachPeek(oldArea, oldCharacter.X + oldCharacter.Width / 2, oldCharacter.Y + oldCharacter.Height / 2);
            else
            {
                var local = LocalCharacterBounds();
                MoveClamped(oldCharacter.X + oldCharacter.Width / 2 - local.X - local.Width / 2,
                    oldCharacter.Bottom - local.Bottom, oldArea);
            }
        }
        if (!_settings.AutoWalk && _behavior.State == PetState.Walking) _behavior.Wake();
        UpdateTimer(); ScheduleSave();
        if (_loadedOnce && !PetApplication.SmokeTest) _usageService.Configure(_settings);
        UpdateUsage(_usageService.Snapshot);
    }
    internal void OpenSettings() => OpenControls(PetControlTab.Settings);
    internal void HidePet() { SaveNow(); Hide(); }
    internal void RestoreFromTray()
    {
        var preservePosition = _handle != IntPtr.Zero && !NativeDesktop.IsIconic(_handle);
        var position = preservePosition ? NativeDesktop.WindowRect(_handle) : default;
        if (_settings.ClickThrough) { _settings = _settings with { ClickThrough = false }; NativeDesktop.ApplyStyles(_handle, false); }
        WindowState = WindowState.Normal;
        Show(); NativeDesktop.ShowWithoutActivation(_handle);
        // Showing a hidden layered WPF window can replay its managed Left/Top.
        if (preservePosition) NativeDesktop.Move(_handle, position.X, position.Y);
        _topmostGuard?.Synchronize(); _topmostGuard?.RequestRestore();
        ClampCurrent(); _behavior.Wake(); UpdateTimer(); ScheduleSave();
    }
    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }
    private void SaveNow()
    {
        // Minimized windows use sentinel screen coordinates; never persist them as the pet's placement.
        if (_handle == IntPtr.Zero || NativeDesktop.IsIconic(_handle)) return;
        var rect = NativeDesktop.WindowRect(_handle); _settings = _settings with { X = rect.X, Y = rect.Y };
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("设置暂时无法保存：" + ex.Message);
        }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => { ClampCurrent(); _topmostGuard?.RequestRestore(); }));
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Mode == PowerModes.Suspend) { SaveNow(); _topmostGuard?.Suspend(); _growthTimer.Stop(); _growth.EndRun(DateTimeOffset.Now); _tick.Stop(); _hoverTimer.Stop(); SetUsageHover(false); }
            else if (e.Mode == PowerModes.Resume) { _growth.StartRun(DateTimeOffset.Now); _lastGrowthTick = _clock.Elapsed.TotalSeconds; _growthTimer.Start(); ClampCurrent(); UpdateTimer(); _topmostGuard?.Synchronize(); _topmostGuard?.RequestRestore(); _ = _resetService.CheckAsync(); }
        }));
    }
}
