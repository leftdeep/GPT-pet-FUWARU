using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GptSpritePet;

internal enum PetControlTab { Settings, Usage, ResetHistory, Growth }

internal sealed class PetControlWindow : Window
{
    private readonly TabControl _tabs = new();
    private readonly Dictionary<PetControlTab, UserControl> _pages = new();
    private readonly Func<PetControlTab, UserControl> _createPage;
    private readonly Action<PetControlTab> _selecting;
    internal PetControlTab SelectedTab => (PetControlTab)_tabs.SelectedIndex;
    internal int CreatedPageCount => _pages.Count;

    internal PetControlWindow(Func<PetControlTab, UserControl> createPage, Action<PetControlTab> selecting, PetControlTab initial)
    {
        _createPage = createPage; _selecting = selecting;
        Title = "芙哇噜 · 桌宠中心"; Width = 560; Height = 800; MinWidth = 520; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13; UseLayoutRounding = true;
        var root = new Grid { Margin = new Thickness(16, 4, 16, 0) };
        PetTheme.Frame(this, root, "桌宠中心", "Fuwaru · 版本 " + ProjectPaths.Version);
        foreach (var label in new[] { "设置", "账号用量", "重置历史", "成长" })
            _tabs.Items.Add(new TabItem { Header = label });
        _tabs.SelectedIndex = -1;
        root.Children.Add(_tabs);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _tabs) || _tabs.SelectedIndex < 0) return;
            var tab = SelectedTab;
            _selecting(tab);
            if (!_pages.TryGetValue(tab, out var page))
            {
                page = _createPage(tab); _pages.Add(tab, page);
                ((TabItem)_tabs.SelectedItem).Content = page;
            }
        };
        Closed += (_, _) =>
        {
            foreach (var page in _pages.Values)
                if (page is IDisposable disposable) disposable.Dispose();
            _pages.Clear();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        SelectTab(initial);
    }

    internal void SelectTab(PetControlTab tab)
    {
        if (!Enum.IsDefined(tab)) throw new ArgumentOutOfRangeException(nameof(tab));
        _tabs.SelectedIndex = (int)tab;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    }
    internal T? Page<T>(PetControlTab tab) where T : UserControl => _pages.GetValueOrDefault(tab) as T;
}
