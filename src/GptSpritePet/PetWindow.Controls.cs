using System;
using System.Windows.Controls;

namespace GptSpritePet;

public sealed partial class PetWindow
{
    private void OpenControls(PetControlTab tab)
    {
        RegisterPetInteraction(); _clickTimer.Stop();
        if (_controlWindow is null)
        {
            _controlWindow = new PetControlWindow(CreateControlPage, PrepareControlTab, tab) { Owner = this };
            TrackWindowInteraction(_controlWindow);
            _controlWindow.Closed += (_, _) => _controlWindow = null;
            _controlWindow.Show();
        }
        else
        {
            if (_controlWindow.SelectedTab == tab) PrepareControlTab(tab);
            _controlWindow.SelectTab(tab);
        }
        _controlWindow.Activate();
    }

    private void PrepareControlTab(PetControlTab tab)
    {
        if (tab != PetControlTab.Usage) return;
        if (!_settings.UsageEnabled) ApplySettings(_settings with { UsageEnabled = true });
        if (!PetApplication.SmokeTest) _usageService.RequestRefresh();
    }

    private UserControl CreateControlPage(PetControlTab tab) => tab switch
    {
        PetControlTab.Settings => new SettingsPage(() => _settings, _sprite.SkinName, _store.DirectoryPath, ApplySettings, _sprite.UsesStandingHeight),
        PetControlTab.Usage => new UsagePage(_usageService, () => _settings),
        PetControlTab.ResetHistory => new ResetHistoryPage(_resetService, CheckResetNow),
        PetControlTab.Growth => new GrowthPage(_growth),
        _ => throw new ArgumentOutOfRangeException(nameof(tab))
    };
}
