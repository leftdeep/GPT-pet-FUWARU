using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace GptSpritePet;

internal static class PetTheme
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Palette = new(StringComparer.OrdinalIgnoreCase);
    internal static SolidColorBrush Brush(string color) => Palette.GetOrAdd(color, value =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    });
    internal static TextBlock Text(string value, double size, bool bold = false) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#36303F"),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };
    internal static StackPanel Card(StackPanel parent, string title, double padding = 15, Thickness? margin = null)
    {
        var panel = new StackPanel(); var heading = Text(title, 14, true);
        heading.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(heading);
        parent.Children.Add(new Border { Child = panel, Background = Brushes.White, CornerRadius = new CornerRadius(15),
            BorderBrush = Brush("#ECE5F4"), BorderThickness = new Thickness(1), Padding = new Thickness(padding),
            Margin = margin ?? new Thickness(0, 12, 0, 0) }); return panel;
    }
    internal static Button Button(string label, RoutedEventHandler? click = null, double horizontalPadding = 12)
    {
        var button = new Button { Content = label, Padding = new Thickness(horizontalPadding, 7, horizontalPadding, 7),
            Margin = new Thickness(0, 0, 8, 0), Background = Brush("#F5EFFB"), Foreground = Brush("#36303F"), BorderBrush = Brush("#ECE5F4") };
        if (click is not null) button.Click += click;
        return button;
    }
    internal static void Apply(FrameworkElement element) => element.Resources.MergedDictionaries.Add(
        new ResourceDictionary { Source = new Uri("/" + typeof(PetTheme).Assembly.GetName().Name + ";component/PetTheme.xaml", UriKind.Relative) });

    internal static DropShadowEffect Shadow() => new() { Color = Color.FromRgb(92, 61, 119), BlurRadius = 14, ShadowDepth = 3, Opacity = 0.12 };

    internal static TextBlock MenuIcon(string glyph) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
        Foreground = Brush("#8063AC"), VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    internal static void Frame(Window window, UIElement content, string title, string subtitle)
    {
        Apply(window);
        window.WindowStyle = WindowStyle.None;
        window.Background = Brush("#FAF8FD");
        window.Foreground = Brush("#36303F");
        window.FontFamily = new FontFamily("Microsoft YaHei UI");
        window.UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });
        var root = new DockPanel();
        var header = new Grid { Background = Brushes.White, Margin = new Thickness(1), Height = 72 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var emblem = new Border { Width = 34, Height = 34, Background = Brush("#F5EFFB"), CornerRadius = new CornerRadius(12), Margin = new Thickness(22, 0, 12, 0),
            Child = new TextBlock { Text = "✦", FontSize = 20, Foreground = Brush("#8063AC"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        header.Children.Add(emblem);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Medium });
        if (!string.IsNullOrWhiteSpace(subtitle))
            words.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Brush("#766D83"), Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(words, 1); header.Children.Add(words);
        var close = new Button { Content = "×", FontSize = 22, Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(12, 0, 18, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "关闭" };
        close.Click += (_, _) => window.Close(); Grid.SetColumn(close, 2); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.Source is not System.Windows.Controls.Button && e.LeftButton == MouseButtonState.Pressed) window.DragMove(); };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); root.Children.Add(content);
        window.Content = new Border { BorderBrush = Brush("#ECE5F4"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = root };
    }
}
