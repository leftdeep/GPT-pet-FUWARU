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

public sealed partial class PetWindow : Window
{
    private readonly SettingsStore _store;
    private PetSettings _settings;
    private readonly PetBehavior _behavior = new();
    private readonly SpriteView _sprite = new();
    private readonly Canvas _canvas = new();
    private readonly Border _usageBadge;
    private const double UsageBubbleBaseWidth = 256;
    private readonly TextBlock[] _usageValues;
    private readonly TextBlock[] _quotaTimes = new TextBlock[2];
    private readonly QuotaWaveBar[] _quotaBars = new QuotaWaveBar[2];
    private readonly TextBlock _outputRateValue = new() { Text = "等待样本", FontSize = 11, FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis, Foreground = PetTheme.Brush("#8063AC") };
    private readonly AccountUsageService _usageService;
    private readonly GrowthEngine _growth;
    private readonly TextBlock _growthLabel = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = PetTheme.Brush("#36303F"), Margin = new Thickness(3, 0, 0, 5) };
    private readonly ProgressBar _growthBar = new() { Height = 3, Maximum = 1, Foreground = PetTheme.Brush("#8063AC"), Background = PetTheme.Brush("#F5EFFB"), BorderThickness = new Thickness(0), Margin = new Thickness(3, 0, 3, 4) };
    private readonly DispatcherTimer _growthTimer;
    private double _lastGrowthTick;
    private long _lastCodexProgressTicks;
    private readonly ResetWatchService _resetService;
    private ResetAlertWindow? _resetAlert;
    private string? _lastPublicResetKey;
    private readonly DispatcherTimer _tick, _saveTimer, _clickTimer, _previewTimer;
    private readonly DispatcherTimer _hoverTimer;
    private bool _usageHoverVisible;
    private double? _usageHeadTop;
    private double _lastUsageHover = double.NegativeInfinity;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick, _lastMotionSave;
    private double? _walkingPhysicalX;
    private WalkingPlayback? _walkingPreview;
    private string? _pendingPreviewKey;
    private IntPtr _handle;
    private HwndSource? _source;
    private WindowTopmostGuard? _topmostGuard;
    private bool _nativeMinimized;
    private TrayController? _tray;
    private PetControlWindow? _controlWindow;
    private NativeDesktop.Point _pointerStart;
    private DesktopRect _dragStart;
    private bool _pointerDown, _dragging, _exiting;
    private readonly Random _random = new();
    private bool _skinInitialized, _loadedOnce;
    private string? _loadedSkin;
    private string? _skinLoadWarning;
    private DateTime _skinManifestStamp;
    private string SkinManifest => Path.Combine(_settings.SkinDirectory ?? ProjectPaths.DefaultSkin, "skin.json");

    public PetWindow(SettingsStore store)
    {
        _store = store; _settings = store.Load(); _peekEdge = _settings.EdgePeek;
        _growth = new GrowthEngine(store.DirectoryPath);
        _resetService = new ResetWatchService(PetApplication.SmokeTest ? Path.Combine(store.DirectoryPath, "reset-watch")
            : Path.Combine(ProjectPaths.Root, "local-data"), schedule: !PetApplication.SmokeTest);
        _lastPublicResetKey = _resetService.Snapshot.LastReset?.Key;
        _resetService.Changed += snapshot =>
        {
            if (!_exiting && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => UpdateReset(snapshot)));
        };
        Title = "芙哇噜 " + ProjectPaths.Version;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        Topmost = _settings.Topmost;
        PetTheme.Apply(this); UseLayoutRounding = true;
        Content = _canvas; _canvas.Children.Add(_sprite);
        PreviewMouseDown += (_, _) => RegisterPetInteraction();
        PreviewMouseWheel += (_, _) => RegisterPetInteraction();
        var usagePanel = new StackPanel();
        usagePanel.Children.Add(_growthLabel); usagePanel.Children.Add(_growthBar);
        var usageGrid = new UniformGrid { Columns = 2 }; usagePanel.Children.Add(usageGrid);
        _usageValues = new TextBlock[4];
        var labels = new[] { "5小时余额", "7日余额", "当日Token", "任务Token" };
        for (var i = 0; i < 4; i++)
        {
            var cell = new Grid { Margin = new Thickness(6, 3, 4, 3) };
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition());
            var heading = new WrapPanel();
            heading.Children.Add(new TextBlock { Text = labels[i], FontSize = 9, Foreground = PetTheme.Brush("#766D83") });
            if (i < 2)
            {
                _quotaTimes[i] = new TextBlock { FontSize = 8, Foreground = PetTheme.Brush("#94899F"), Margin = new Thickness(4, 1, 0, 0) };
                heading.Children.Add(_quotaTimes[i]);
            }
            cell.Children.Add(heading);
            _usageValues[i] = new TextBlock { Text = "—", FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Color.FromRgb(54, 48, 63)) };
            var lower = new Grid { MinHeight = 28, Margin = new Thickness(-6, 2, -4, -3) };
            Grid.SetRow(lower, 1);
            if (i < 2)
            {
                _quotaBars[i] = new QuotaWaveBar(i == 0 ? QuotaWaveStyle.FiveHours : QuotaWaveStyle.SevenDays);
                lower.Children.Add(_quotaBars[i]);
            }
            _usageValues[i].Margin = new Thickness(6, 3, 4, 3);
            _usageValues[i].VerticalAlignment = VerticalAlignment.Center;
            lower.Children.Add(_usageValues[i]); cell.Children.Add(lower);
            usageGrid.Children.Add(new Border { Child = cell, Background = PetTheme.Brush("#F5EFFB"),
                CornerRadius = new CornerRadius(7), Margin = new Thickness(2) });
        }
        var rateRow = new Grid { Margin = new Thickness(8, 6, 6, 2) };
        rateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rateRow.ColumnDefinitions.Add(new ColumnDefinition());
        rateRow.Children.Add(new TextBlock { Text = "输出（估算）", FontSize = 9, VerticalAlignment = VerticalAlignment.Center,
            Foreground = PetTheme.Brush("#766D83"), Margin = new Thickness(0, 0, 8, 0) });
        Grid.SetColumn(_outputRateValue, 1); rateRow.Children.Add(_outputRateValue);
        usagePanel.Children.Add(rateRow);
        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resizeButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var detailButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(detailButtons, 1);
        footer.Children.Add(resizeButtons); footer.Children.Add(detailButtons);
        foreach (var (label, delta) in new[] { ("−", -.25), ("＋", .25) })
        {
            var resize = new Button { Content = label, FontSize = 12, Padding = new Thickness(5, 1, 5, 1),
                Background = PetTheme.Brush("#F5EFFB"), Foreground = PetTheme.Brush("#36303F"),
                BorderBrush = PetTheme.Brush("#ECE5F4"), Margin = new Thickness(0, 0, 4, 0),
                ToolTip = delta > 0 ? "放大 Token 气泡" : "缩小 Token 气泡" };
            resize.Click += (_, e) => { ResizeUsageBubble(delta); e.Handled = true; };
            resizeButtons.Children.Add(resize);
        }
        var growthButton = new Button { Content = "成长 ›", FontSize = 9, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 5, 0), BorderThickness = new Thickness(0), Background = PetTheme.Brush("#F5EFFB"), Foreground = PetTheme.Brush("#36303F") };
        growthButton.Click += (_, e) => { OpenGrowth(); e.Handled = true; }; detailButtons.Children.Add(growthButton);
        var usageButton = new Button { Content = "用量详情 ›", FontSize = 9, Padding = new Thickness(0, 2, 0, 2),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = PetTheme.Brush("#766D83") };
        usageButton.Click += (_, e) => { OpenUsage(); e.Handled = true; }; detailButtons.Children.Add(usageButton);
        usagePanel.Children.Add(footer);
        System.Windows.Documents.TextElement.SetFontFamily(usagePanel, new FontFamily("Microsoft YaHei UI"));
        _usageBadge = new Border { Child = usagePanel, Padding = new Thickness(9), CornerRadius = new CornerRadius(15),
            Background = PetTheme.Brush("#FFFFFF"), BorderBrush = PetTheme.Brush("#ECE5F4"), Effect = PetTheme.Shadow(),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Visibility = Visibility.Hidden };
        _usageBadge.MouseLeftButtonUp += (_, e) => { OpenUsage(); e.Handled = true; };
        _canvas.Children.Add(_usageBadge); Canvas.SetLeft(_usageBadge, 12); Canvas.SetTop(_usageBadge, 12);
        _sprite.SizeChanged += (_, _) => { _usageHeadTop = null; PositionHeadBubbles(); };
        _usageBadge.SizeChanged += (_, _) => PositionHeadBubbles();
        _usageService = new AccountUsageService(_store.DirectoryPath);
        _usageService.TokenObserved += usage =>
        {
            var now = DateTimeOffset.Now;
            if (usage.At <= now && now - usage.At < TimeSpan.FromMinutes(2)) System.Threading.Interlocked.Exchange(ref _lastCodexProgressTicks, usage.At.UtcTicks);
            _growth.ObserveToken(usage, now);
        };
        _growth.Changed += () => { if (!_exiting && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(UpdateGrowth)); };
        _growthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _growthTimer.Tick += (_, _) => GrowthTick();
        _usageService.Changed += OnAccountUsageChanged;
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000d / 24) };
        _tick.Tick += (_, _) => Tick();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); ClickReaction(); };
        _previewTimer = new DispatcherTimer();
        _previewTimer.Tick += (_, _) => StopPreview();
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick += (_, _) =>
        {
            if (NativeDesktop.GetCursorPos(out var cursor)) UpdateUsageHover(new Point(cursor.X, cursor.Y), _clock.Elapsed.TotalSeconds,
                NativeDesktop.WindowFromPoint(cursor) == _handle);
        };
        _behavior.StateChanged += state =>
        {
            if (_sprite.PreviewKey is null)
            { _sprite.SetBehaviorState(state, state == PetState.Walking ? _behavior.WalkingPlayback : null, _behavior.Direction); }
            _sprite.AutomaticMovementKey = _behavior.SelectedMovementKey;
            _tick.Interval = TimeSpan.FromMilliseconds(state == PetState.Sleeping && _sprite.FrameCount(PetState.Sleeping) <= 1 ? 250
                : state == PetState.Walking && _sprite.HasMeasuredWalkingContacts ? _sprite.WalkingRenderMilliseconds : 1000d / 24);
            if (state == PetState.Idle && _pendingPreviewKey is { } pending)
            { _pendingPreviewKey = null; PreviewAnimation(pending); }
        };
        _sprite.MouseLeftButtonDown += PointerDown;
        _sprite.MouseMove += PointerMove;
        _sprite.MouseLeftButtonUp += PointerUp;
        _sprite.LostMouseCapture += (_, _) => { if (_dragging) FinishDrag(); _pointerDown = false; };
        _sprite.MouseRightButtonUp += (_, e) =>
        {
            _clickTimer.Stop();
            var menu = CreateContextMenu(); menu.PlacementTarget = _sprite; menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true;
            e.Handled = true;
        };
        SourceInitialized += (_, _) => InitializeNative();
        Loaded += (_, _) => OnLoaded();
        IsVisibleChanged += (_, _) => { UpdateTimer(); _topmostGuard?.Synchronize(); };
        Closing += (_, e) => { if (!_exiting) { e.Cancel = true; HidePet(); } };
        ApplyView();
        UpdateGrowth();
    }

    internal PetSettings Settings => _settings;
    internal string SkinName => _sprite.SkinName;

    private void InitializeNative()
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle); _source?.AddHook(WindowMessage);
        NativeDesktop.ApplyStyles(_handle, _settings.ClickThrough);
        _topmostGuard = new WindowTopmostGuard(this, _handle, () => _settings.Topmost && !_exiting);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }

    private void OnLoaded()
    {
        if (_loadedOnce) return; _loadedOnce = true;
        _tray = new TrayController(this);
        if (_settings.X.HasValue && _settings.Y.HasValue)
            MoveClamped(_settings.X.Value, _settings.Y.Value, CharacterWorkArea(_settings.X.Value, _settings.Y.Value));
        else ResetPosition();
        UpdateTimer();
        if (!PetApplication.SmokeTest) NativeDesktop.ShowWithoutActivation(_handle);
        _topmostGuard?.Synchronize();
        if (!PetApplication.SmokeTest) { _growth.StartRun(DateTimeOffset.Now); _lastGrowthTick = _clock.Elapsed.TotalSeconds; _growthTimer.Start(); }
        if (!PetApplication.SmokeTest) { _usageService.Configure(_settings); _resetService.Configure(_settings.ResetMonitoringEnabled); }
#if NATIVE_CHECKS
        if (PetApplication.SmokeTest)
        {
            var smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            smokeTimer.Tick += (_, _) => { smokeTimer.Stop(); if (PetApplication.MenuSmokeTest) RunMenuSmokeTest(); else RunSmokeTest(); };
            smokeTimer.Start();
        }
#endif
        if (PetApplication.StartWithSettings) OpenSettings();
        if (PetApplication.StartWithUsage && !PetApplication.SmokeTest) OpenUsage();
        if (PetApplication.StartWithGrowth && !PetApplication.SmokeTest) OpenGrowth();
        if (PetApplication.StartWithResetHistory && !PetApplication.SmokeTest) OpenResetHistory();
        if (PetApplication.StartWithResetCheck && !PetApplication.SmokeTest) CheckResetNow();
        if (PetApplication.ResetPositionRequested && !PetApplication.SmokeTest) ResetPosition();
        if (_peekEdge != ScreenEdge.None) ClampCurrent();
        if (_peekEdge == ScreenEdge.None && _settings.AutoWalk && !_settings.Paused && !PetApplication.SmokeTest) _behavior.React(PetState.Walking, 5);
        if (PetApplication.StatusRequested && !PetApplication.SmokeTest) WriteRuntimeStatus();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        if (message == 0x0084) // WM_NCHITTEST
        {
            if (_settings.ClickThrough) { handled = true; return new IntPtr(-1); }
            var packed = lParam.ToInt64();
            var screen = new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff));
            if (_usageBadge.IsVisible)
            {
                var badgePoint = _usageBadge.PointFromScreen(screen);
                if (badgePoint.X >= 0 && badgePoint.Y >= 0 && badgePoint.X < _usageBadge.ActualWidth && badgePoint.Y < _usageBadge.ActualHeight)
                    return IntPtr.Zero;
            }
            var local = _sprite.PointFromScreen(screen);
            if (!_sprite.IsOpaqueHit(local)) { handled = true; return new IntPtr(-1); }
        }
        if (message is 0x0047 or 0x0018 or 0x0005) // Native Z order, visibility or minimization changed.
            _topmostGuard?.NativeStateChanged();
        var minimizedChanged = message == 0x0005 && _nativeMinimized != (wParam.ToInt64() == 1);
        if (message == 0x0005) _nativeMinimized = wParam.ToInt64() == 1;
        if (message == 0x0018 || minimizedChanged)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateTimer));
        if (message is 0x02E0 or 0x007E or 0x001A) // DPI, display or work-area settings
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { ClampCurrent(); _topmostGuard?.RequestRestore(); }));
        if (message == NativeDesktop.TaskbarCreatedMessage || message == 0x031E) // Explorer / DWM restarted.
            _topmostGuard?.RequestRestore();
        return IntPtr.Zero;
    }

    internal void ExitPet()
    {
        if (_exiting) return; _exiting = true; SaveNow();
        _tick.Stop(); _saveTimer.Stop(); _clickTimer.Stop(); _hoverTimer.Stop(); _previewTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        _topmostGuard?.Dispose();
        _source?.RemoveHook(WindowMessage); _tray?.Dispose(); _controlWindow?.Close();
        _usageService.Dispose(); _sprite.Dispose();
        _resetAlert?.Close(); _resetService.Dispose();
        _growthTimer.Stop(); _growth.EndRun(DateTimeOffset.Now);
        Close(); Application.Current.Shutdown();
    }

}
