using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GptSpritePet.Core;
using Microsoft.Win32;

namespace GptSpritePet;

internal sealed class SettingsPage : UserControl
{
    private PetSettings _draft;
    private bool _usesStandingHeight;
    private readonly Slider _size = new() { Minimum = 160, Maximum = 360, TickFrequency = 10, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _sizeValue = new();
    private readonly CheckBox _topmost = new() { Content = "置顶显示", Margin = new Thickness(0, 5, 0, 5) };
    private readonly CheckBox _walk = new() { Content = "自动走动", Margin = new Thickness(0, 5, 0, 5) };
    private readonly CheckBox _startup = new() { Content = "开机自启动（登录 Windows 后）", Margin = new Thickness(0, 5, 0, 5) };
    private readonly WindowsStartup _windowsStartup;
    private readonly ComboBox _bubbleSize = new() { Height = 32, Margin = new Thickness(0, 7, 0, 0) };
    private readonly CheckBox _through = new() { Content = "鼠标完全穿透（从托盘恢复）", Margin = new Thickness(0, 5, 0, 5) };
    private readonly ComboBox _frequency = new() { Margin = new Thickness(0, 8, 0, 0), Height = 32 };
    private readonly TextBlock _skin = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly CheckBox _usageEnabled = new() { Content = "连接账号并自动更新用量", Margin = new Thickness(0, 5, 0, 5) };
    private readonly CheckBox _usageCard = new() { Content = "悬停人物时显示用量气泡", Margin = new Thickness(0, 5, 0, 5) };
    private readonly CheckBox _resetEnabled = new() { Content = "每小时监测 Tibo 重置公告", Margin = new Thickness(0, 5, 0, 5) };
    private readonly ComboBox _usageRefresh = new() { Margin = new Thickness(0, 7, 0, 7), Height = 30 };
    private readonly TextBlock _connector = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _saved = PetTheme.Text("", 11);

    public SettingsPage(Func<PetSettings> settings, string skinName, string dataDirectory, Action<PetSettings> apply, bool usesStandingHeight = false, WindowsStartup? startup = null)
    {
        _windowsStartup = startup ?? new WindowsStartup(ProjectPaths.Root);
        _draft = settings().Normalize();
        _usesStandingHeight = usesStandingHeight;
        FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 20) };
        Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(Button("恢复默认值", (_, _) => { _draft = new PetSettings { X = _draft.X, Y = _draft.Y }; _startup.IsChecked = false; SetSizeMeaning(ProjectPaths.DefaultSkin); Populate("内置角色"); }));
        var save = Button("应用设置", (_, _) =>
        {
            try { if (_startup.IsEnabled) _windowsStartup.SetEnabled(_startup.IsChecked == true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { _saved.Text = "自启动设置未保存：" + ex.Message; return; }
            // Keep current placement, pause and runtime fields when applying a retained draft.
            _draft = settings() with { SkinDirectory = _draft.SkinDirectory, CodexExecutablePath = _draft.CodexExecutablePath,
                Size = (int)_size.Value, Topmost = _topmost.IsChecked == true,
                AutoWalk = _walk.IsChecked == true, ClickThrough = _through.IsChecked == true,
                UsageBubbleScale = 1 + Math.Max(0, _bubbleSize.SelectedIndex) * .25,
                Frequency = (InteractionFrequency)Math.Max(0, _frequency.SelectedIndex), UsageEnabled = _usageEnabled.IsChecked == true,
                UsageCardVisible = _usageCard.IsChecked == true,
                ResetMonitoringEnabled = _resetEnabled.IsChecked == true,
                UsageRefreshSeconds = _usageRefresh.SelectedIndex switch { 0 => 30, 2 => 120, 3 => 300, _ => 60 } };
            apply(_draft); _saved.Text = "设置已应用";
        }); save.Background = Brush("#8063AC"); save.Foreground = Brushes.White;
        footer.Children.Add(save);
        _saved.VerticalAlignment = VerticalAlignment.Center; _saved.Margin = new Thickness(8, 0, 0, 0); footer.Children.Add(_saved);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        var panel = new StackPanel(); scroll.Content = panel;
        var sizePanel = Card(panel, "显示大小"); sizePanel.Children.Add(_sizeValue); sizePanel.Children.Add(_size);
        _size.ValueChanged += (_, _) => UpdateSizeLabel();
        sizePanel.Children.Add(new TextBlock { Text = "Token 气泡大小", Margin = new Thickness(0, 12, 0, 0) });
        foreach (var percent in new[] { 100, 125, 150, 175, 200 }) _bubbleSize.Items.Add(percent + "%");
        sizePanel.Children.Add(_bubbleSize);
        var interaction = Card(panel, "桌面行为");
        interaction.Children.Add(_startup); interaction.Children.Add(_topmost); interaction.Children.Add(_walk); interaction.Children.Add(_through);
        interaction.Children.Add(new TextBlock { Text = "自主互动频率", Margin = new Thickness(0, 10, 0, 0), FontSize = 12 });
        _frequency.Items.Add("低"); _frequency.Items.Add("正常"); _frequency.Items.Add("高"); interaction.Children.Add(_frequency);
        var usage = Card(panel, "用量气泡"); usage.Children.Add(_usageEnabled); usage.Children.Add(_usageCard);
        usage.Children.Add(new TextBlock { Text = "移到人物上显示；离开后收起。点击气泡查看详情。", FontSize = 11, Foreground = Brush("#766D83"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 3) });
        usage.Children.Add(new TextBlock { Text = "自动刷新间隔", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });
        foreach (var choice in new[] { "30 秒", "60 秒", "2 分钟", "5 分钟" }) _usageRefresh.Items.Add(choice);
        usage.Children.Add(_usageRefresh); usage.Children.Add(_connector);
        var connectionButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        usage.Children.Add(connectionButtons);
        connectionButtons.Children.Add(Button("选择连接程序…", (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = "选择本机 Codex 连接程序", Filter = "Codex 程序 (codex.exe)|codex.exe|应用程序 (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) { _draft = _draft with { CodexExecutablePath = dialog.FileName }; _connector.Text = dialog.FileName; }
        }));
        connectionButtons.Children.Add(Button("自动检测", (_, _) => { _draft = _draft with { CodexExecutablePath = null }; _connector.Text = "自动使用本机 Codex 登录"; }));
        var reset = Card(panel, "重置预警"); reset.Children.Add(_resetEnabled);
        reset.Children.Add(new TextBlock { Text = "重置预告、完成公告和赠送重置卡均会提醒，点击“我知道了”才关闭。可在重置历史中手动检查。", FontSize = 11, Foreground = Brush("#766D83"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var skin = Card(panel, "角色素材"); skin.Children.Add(_skin);
        var assetButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) }; skin.Children.Add(assetButtons);
        assetButtons.Children.Add(Button("选择素材文件夹…", (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "选择包含 skin.json 与 WebM / PNG 的文件夹" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                if (!File.Exists(Path.Combine(dialog.FolderName, "skin.json")))
                { MessageBox.Show(Window.GetWindow(this), "所选文件夹中需要有 skin.json。请参考项目中的素材接入说明。", "选择素材"); return; }
                _draft = _draft with { SkinDirectory = dialog.FolderName }; _skin.Text = dialog.FolderName; SetSizeMeaning(dialog.FolderName);
            }
        }));
        assetButtons.Children.Add(Button("恢复内置角色", (_, _) => { _draft = _draft with { SkinDirectory = null }; _skin.Text = "内置角色"; SetSizeMeaning(ProjectPaths.DefaultSkin); }));
        panel.Children.Add(new TextBlock { Text = "单击播放互动动作，拖动移动，右键菜单打开设置。隐藏或穿透时，可从任务栏通知区域的紫色托盘图标恢复。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#766D83"), Margin = new Thickness(2, 2, 2, 12) });
        panel.Children.Add(new TextBlock { Text = "设置保存位置：" + dataDirectory, TextWrapping = TextWrapping.Wrap,
            FontSize = 11, Foreground = Brush("#766D83") });
        Populate(skinName);
        try { _startup.IsChecked = _windowsStartup.Enabled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { _startup.IsEnabled = false; _saved.Text = "无法读取自启动设置：" + ex.Message; }
    }

    private void Populate(string skinName)
    {
        _size.Value = _draft.Size; UpdateSizeLabel();
        _topmost.IsChecked = _draft.Topmost; _walk.IsChecked = _draft.AutoWalk;
        _bubbleSize.SelectedIndex = (int)Math.Round((_draft.UsageBubbleScale - 1) / .25);
        _through.IsChecked = _draft.ClickThrough; _frequency.SelectedIndex = (int)_draft.Frequency;
        _skin.Text = _draft.SkinDirectory ?? skinName;
        _usageEnabled.IsChecked = _draft.UsageEnabled; _usageCard.IsChecked = _draft.UsageCardVisible;
        _resetEnabled.IsChecked = _draft.ResetMonitoringEnabled;
        _usageRefresh.SelectedIndex = _draft.UsageRefreshSeconds switch { 30 => 0, 120 => 2, 300 => 3, _ => 1 };
        _connector.Text = _draft.CodexExecutablePath ?? "自动使用本机 Codex 登录";
    }

    private void UpdateSizeLabel() => _sizeValue.Text = $"{(_usesStandingHeight ? "人物站立高度" : "动画画布高度")}：{(int)_size.Value} 像素（随系统缩放）";

    private void SetSizeMeaning(string directory)
    {
        try { _usesStandingHeight = SkinDefinition.Load(directory).StandingHeightRatio.HasValue; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { _usesStandingHeight = false; }
        UpdateSizeLabel();
    }
    private static StackPanel Card(StackPanel parent, string heading)
    {
        var panel = PetTheme.Card(parent, heading, 15, new Thickness(0, 0, 0, 14));
        ((Border)parent.Children[parent.Children.Count - 1]).Padding = new Thickness(18, 15, 18, 15);
        return panel;
    }
    private static Button Button(string text, RoutedEventHandler handler)
    {
        var button = PetTheme.Button(text, handler, 10);
        button.Cursor = System.Windows.Input.Cursors.Hand; button.FontSize = 12;
        return button;
    }
    private static SolidColorBrush Brush(string value) => PetTheme.Brush(value);
}


