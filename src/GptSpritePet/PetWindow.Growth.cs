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
    internal void OpenGrowth() => OpenControls(PetControlTab.Growth);
    private void GrowthTick()
    {
        var elapsed = _clock.Elapsed.TotalSeconds; var now = DateTimeOffset.Now;
        var active = _usageService.Snapshot.LocalUsage?.Tasks.Any(t => t.UpdatedAt.HasValue && now >= t.UpdatedAt && now - t.UpdatedAt < TimeSpan.FromMinutes(2)) == true;
        var progressTicks = System.Threading.Interlocked.Read(ref _lastCodexProgressTicks);
        active |= progressTicks > 0 && now.UtcTicks >= progressTicks && now.UtcTicks - progressTicks < TimeSpan.FromMinutes(2).Ticks;
        _growth.Tick(now, elapsed - _lastGrowthTick, NativeDesktop.DesktopUnlocked(), NativeDesktop.RecentInput(), active); _lastGrowthTick = elapsed;
    }
    private void UpdateGrowth()
    {
        var snapshot = _growth.Snapshot(DateTimeOffset.Now);
        _growthLabel.Text = $"✦  Lv.{snapshot.Level} · {snapshot.LevelXp:0.#}/{snapshot.NeedXp:0} XP";
        _growthBar.Value = Math.Clamp(snapshot.LevelXp / snapshot.NeedXp, 0, 1);
        if (_controlWindow?.SelectedTab == PetControlTab.Growth)
            _controlWindow.Page<GrowthPage>(PetControlTab.Growth)?.Update(snapshot);
    }
}
