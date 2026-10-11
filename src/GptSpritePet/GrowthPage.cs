using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GptSpritePet.Core;
using static GptSpritePet.PetTheme;

namespace GptSpritePet;

internal sealed class GrowthPage : UserControl, IDisposable
{
    private readonly GrowthEngine _engine;
    private readonly TextBlock _level = Text("", 24, true), _progress = Text("", 12), _history = Text("", 12), _focus = Text("", 14), _warning = Text("", 11);
    private readonly ProgressBar _bar = new() { Height = 6, Maximum = 1, Background = PetTheme.Brush("#F5EFFB"), Foreground = PetTheme.Brush("#8063AC"), BorderThickness = new Thickness(0) };
    private readonly StackPanel _daily = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal GrowthSnapshot? Displayed { get; private set; }

    internal bool CountdownRunning => _timer.IsEnabled;
    public GrowthPage(GrowthEngine engine)
    {
        _engine = engine;
        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
        Content = root;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        var panel = new StackPanel(); scroll.Content = panel;
        var level = Card(panel, "陪伴等级"); level.Children.Add(_level); level.Children.Add(_progress);
        _progress.Margin = new Thickness(0, 6, 0, 10); level.Children.Add(_bar);
        _history.Margin = new Thickness(0, 10, 0, 0); level.Children.Add(_history);
        Card(panel, "今日经验 · 上限 100 XP").Children.Add(_daily);
        var focus = Card(panel, "专注陪伴"); focus.Children.Add(_focus);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; focus.Children.Add(buttons);
        foreach (var minutes in new[] { 25, 50 })
        {
            var button = Button(minutes + " 分钟"); button.Click += (_, _) => { _engine.Focus(minutes); Update(_engine.Snapshot(DateTimeOffset.Now)); }; buttons.Children.Add(button);
        }
        var stop = Button("结束专注"); stop.Click += (_, _) => { _engine.Focus(0); Update(_engine.Snapshot(DateTimeOffset.Now)); }; buttons.Children.Add(stop);
        panel.Children.Add(_warning);
        _timer.Tick += (_, _) => Update(_engine.Snapshot(DateTimeOffset.Now));
        Loaded += (_, _) => { Update(_engine.Snapshot(DateTimeOffset.Now)); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        Update(_engine.Snapshot(DateTimeOffset.Now));
    }
    public void Dispose() => _timer.Stop();

    public void Update(GrowthSnapshot snapshot)
    {
        Displayed = snapshot;
        _level.Text = "Lv." + snapshot.Level;
        _progress.Text = $"{snapshot.LevelXp:0.#} / {snapshot.NeedXp:0} XP · 今日 +{snapshot.Today.Xp:0.#}";
        _bar.Value = Math.Clamp(snapshot.LevelXp / snapshot.NeedXp, 0, 1);
        _history.Text = $"已陪伴 {snapshot.Days} 天 · {snapshot.Hours:0.#} 小时";
        _daily.Children.Clear();
        foreach (var row in new[] { $"每日使用：{snapshot.Today.PresenceXp} / 10 XP", $"陪伴时长：{snapshot.Today.TimeXp:0.#} / 60 XP",
            $"有效 Token：{snapshot.Today.TokenXp} / 20 XP", $"日常互动：{snapshot.Today.InteractionXp} / 10 XP" })
        { var value = Text(row, 12); value.Margin = new Thickness(0, 5, 0, 5); _daily.Children.Add(value); }
        _focus.Text = snapshot.FocusSeconds > 0 ? $"剩余 {(int)snapshot.FocusSeconds / 60:00}:{(int)snapshot.FocusSeconds % 60:00}" : "未开始专注";
        _warning.Text = snapshot.Warning ?? "";
    }

    private static StackPanel Card(StackPanel parent, string title) =>
        PetTheme.Card(parent, title, 16, new Thickness(0, 0, 0, 12));
}
