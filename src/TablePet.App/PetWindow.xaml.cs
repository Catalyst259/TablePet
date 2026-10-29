using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TablePet.App.Services;
using TablePet.Core;

namespace TablePet.App;

public partial class PetWindow : Window
{
    private readonly SettingsService _settings;
    private readonly AssetCatalog _assets;
    private readonly BehaviorController _behavior;
    private readonly InteractionService _interaction;
    private readonly PlatformService _platform;
    private readonly DispatcherTimer _timer;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private SleepIndicatorWindow? _sleepIndicator;
    private bool _ready, _closing, _hidden, _systemPaused, _autoPaused, _pointerHeld, _menuOpen;
    private bool _renderingSubscribed;
    private TimeSpan? _lastRenderingTime;
    private Point _downPhysical;
    private Point _lastPointer;
    private Rect _downBounds;
    private string _frame = "";
    private PetSettings _applied;

    public PetWindow(SettingsService settings, AssetCatalog assets)
    {
        InitializeComponent();
        _settings = settings; _assets = assets; _applied = settings.Current;
        var clock = new StopwatchClock();
        _behavior = new BehaviorController(clock, assets.Clips, settings.Current, idleActions: assets.IdleActions);
        _behavior.StateChanged += state => ErrorLogger.Write($"状态：{state}");
        _interaction = new InteractionService(clock, settings.Current,
            SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
        _platform = new PlatformService(this);
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick();
        Height = settings.Current.PetHeightDip; Width = Height * assets.CanvasWidth / assets.CanvasHeight;
        Topmost = settings.Current.Topmost;
        _assets.CacheForSize(Height, 1);
        _behavior.Player.FrameChanged += f => Render(f.File);
        Render(_behavior.Player.CurrentFrame!.File);
        SourceInitialized += (_, _) => _platform.Attach();
        Loaded += (_, _) => InitializeDesktop();
        MouseLeftButtonDown += OnPointerDown;
        MouseLeftButtonUp += OnPointerUp;
        LostMouseCapture += (_, _) => { if (_pointerHeld) EndPointer(cancel: true); };
        MouseRightButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        _settings.Changed += ApplySettings;
        _settings.SaveFailed += message => Dispatcher.BeginInvoke(new Action(() => _tray?.ShowMessage(message)));
        _platform.SuspensionChanged += paused => { _systemPaused = paused; UpdateSuspension(); };
        _platform.DpiChanged += () => { ResizeAssets(); if (!_behavior.IsDragging) _platform.KeepVisible(); _sleepIndicator?.SyncBounds(); ResetBaseline(); };
        _platform.DisplayChanged += () => { ResetBaseline(); SavePosition(); };
        LocationChanged += (_, _) => _sleepIndicator?.SyncBounds();
        SizeChanged += (_, _) => _sleepIndicator?.SyncBounds();
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; ToggleVisible(); } };
    }

    private void InitializeDesktop()
    {
        if (_ready) return;
        _ready = true;
        _platform.SetClickThrough(_settings.Current.ClickThrough);
        _platform.RestorePlacement(_settings.Current.Position);
        ResizeAssets(); ResetBaseline();
        _tray = new TrayService(new TrayActions(ToggleVisible, OpenSettings, TogglePause,
            () => _settings.Update(_settings.Current with { Topmost = !_settings.Current.Topmost }),
            () => _settings.Update(_settings.Current with { ClickThrough = !_settings.Current.ClickThrough }),
            RestorePosition, Quit, () => SetMenuOpen(true), () => SetMenuOpen(false)),
            Path.Combine(AppContext.BaseDirectory, "Assets", "strawberry-cat", "tray.ico"));
        UpdateTray(); _timer.Start(); UpdateRenderingSubscription();
        if (_settings.LastError is { } error) _tray.ShowMessage(error);
        if (_assets.IsFallback) _tray.ShowMessage("动画素材暂不可用，已显示原型图。请检查 Assets 文件夹。");
    }

    private void Tick()
    {
        if (!_ready || _hidden || _systemPaused) return;
        if (PlatformService.TryGetCursorPhysical(out var pointer))
        {
            _lastPointer = pointer;
            var dip = PointerDip(pointer);
            if (!_settings.Current.ClickThrough && !_menuOpen)
            {
                Handle(_interaction.UpdatePointer(dip.X, dip.Y, Hit(pointer)));
                Handle(_interaction.Tick());
                if (_pointerHeld && !PlatformService.IsLeftButtonDown()) EndPointer(cancel: false);
            }
            if (_behavior.IsDragging)
                _platform.MovePhysical(_downBounds.X + pointer.X - _downPhysical.X, _downBounds.Y + pointer.Y - _downPhysical.Y);
        }
        // The low-frequency timer samples input and wakes idle deadlines. Moving clips
        // advance only on the compositor's rendering callback, without a 30 ms FPS cap.
        if (!_behavior.Player.NeedsContinuousRendering) _behavior.Tick();
        UpdateRenderingSubscription();
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (_lastRenderingTime == args.RenderingTime) return;
            _lastRenderingTime = args.RenderingTime;
        }
        _behavior.Tick();
        UpdateRenderingSubscription();
    }
    private void UpdateRenderingSubscription()
    {
        UpdateSleepIndicator();
        // A static fallback sleep pose still has moving Zs. Dragging pauses the same
        // player clock, so both the body and decoration freeze together.
        var needed = _ready && !_closing && !_hidden && !_systemPaused &&
            (_behavior.Player.NeedsContinuousRendering || (IsSleepIndicatorActive && !_behavior.Player.IsPaused));
        if (needed == _renderingSubscribed) return;
        _renderingSubscribed = needed;
        _lastRenderingTime = null;
        if (needed) CompositionTarget.Rendering += OnRendering;
        else { CompositionTarget.Rendering -= OnRendering; _assets.StopPrefetch(); }
    }
    private bool IsSleepIndicatorActive => _behavior.State == PetState.Sleep &&
        _behavior.Player.CurrentClip?.Name == "sleep_loop";

    private void UpdateSleepIndicator()
    {
        if (!_ready || _closing || _hidden || _systemPaused || !IsVisible || !IsSleepIndicatorActive)
        {
            _sleepIndicator?.Hide();
            return;
        }
        _sleepIndicator ??= new SleepIndicatorWindow(this);
        _sleepIndicator.Update(_behavior.Player.ElapsedMs);
    }
    private Point PointerDip(Point physical)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(physical.X / dpi.DpiScaleX, physical.Y / dpi.DpiScaleY);
    }
    private bool Hit(Point physical)
    {
        if (_frame.Length == 0) return false;
        if (!IsMouseCaptured && !_platform.IsPointerOverPet(physical)) return false;
        var bounds = _platform.GetWindowBoundsPhysical();
        return _assets.Hit(_frame, (physical.X - bounds.X) / Math.Max(1, bounds.Width),
            (physical.Y - bounds.Y) / Math.Max(1, bounds.Height));
    }
    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Current.ClickThrough || _hidden || _systemPaused || _menuOpen) return;
        if (!PlatformService.TryGetCursorPhysical(out _downPhysical)) return;
        _lastPointer = _downPhysical;
        if (!Hit(_downPhysical)) return;
        _downBounds = _platform.GetWindowBoundsPhysical();
        var point = PointerDip(_downPhysical);
        _pointerHeld = true; UpdateBusy();
        Handle(_interaction.PointerDown(point.X, point.Y, true)); e.Handled = true;
    }
    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    { if (_pointerHeld) { EndPointer(cancel: false); e.Handled = true; } }
    private void EndPointer(bool cancel)
    {
        if (!_pointerHeld) return;
        _pointerHeld = false;
        var available = PlatformService.TryGetCursorPhysical(out var pointer);
        if (!available) { pointer = _lastPointer; cancel = true; }
        var dip = PointerDip(pointer);
        var signal = cancel ? _interaction.Cancel() : _interaction.PointerUp(dip.X, dip.Y, Hit(pointer));
        Handle(signal);
        if (_behavior.IsDragging) _behavior.EndDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateBusy(); _platform.KeepVisible(); SavePosition(); ResetBaseline();
        UpdateRenderingSubscription();
    }
    private void Handle(InteractionSignal signal)
    {
        if (signal.HasFlag(InteractionSignal.BeginDrag)) { _behavior.BeginDrag(); CaptureMouse(); }
        if (signal.HasFlag(InteractionSignal.EndDrag)) _behavior.EndDrag();
        if (signal.HasFlag(InteractionSignal.Hover)) _behavior.Hover();
        if (signal.HasFlag(InteractionSignal.Click)) _behavior.Click();
        UpdateRenderingSubscription();
    }
    private void Render(string file)
    {
        // Clip boundaries intentionally share their first PNG. Still queue the new
        // timeline and update scheduling even when the displayed pixels stay identical.
        if (_behavior.Player.CurrentClip is { } clip) _assets.PreparePlayback(clip, _behavior.Player.FrameIndex);
        if (_frame != file || PetImage.Source is null) { _frame = file; PetImage.Source = _assets.Image(file); }
        UpdateRenderingSubscription();
    }
    private void ResizeAssets()
    {
        _assets.CacheForSize(Height, VisualTreeHelper.GetDpi(this).DpiScaleY);
        PetImage.Source = _assets.Image(_frame);
        if (_behavior.Player.CurrentClip is { } clip) _assets.PreparePlayback(clip, _behavior.Player.FrameIndex);
    }
    private void ResetBaseline()
    {
        if (!_ready) return;
        if (!PlatformService.TryGetCursorPhysical(out var pointer)) { _interaction.Cancel(); return; }
        _lastPointer = pointer;
        var dip = PointerDip(pointer);
        _interaction.ResetBaseline(dip.X, dip.Y, Hit(pointer));
    }
    private void UpdateBusy() => _behavior.SetInputBusy(_pointerHeld || _menuOpen || _settingsWindow is not null);
    private void SetMenuOpen(bool open) { _menuOpen = open; UpdateBusy(); ResetBaseline(); }
    private void ApplySettings(PetSettings settings)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ApplySettings(settings))); return; }
        // Saving a position must not restart deadlines, cancel input, or re-decode images.
        if (settings with { Position = null } == _applied with { Position = null }) { _applied = settings; return; }
        if (_pointerHeld) EndPointer(cancel: true);
        _behavior.ApplySettings(settings);
        Height = settings.PetHeightDip; Width = Height * _assets.CanvasWidth / _assets.CanvasHeight;
        Topmost = settings.Topmost;
        if (_ready)
        {
            _platform.SetClickThrough(settings.ClickThrough); ResizeAssets();
            if (!PlatformService.TryGetCursorPhysical(out var pointer)) pointer = _lastPointer;
            var dip = PointerDip(pointer);
            _interaction.ApplySettings(settings, dip.X, dip.Y, Hit(pointer));
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _platform.KeepVisible(); ResetBaseline(); }));
        }
        _applied = settings; _sleepIndicator?.SyncBounds(); UpdateTray();
    }
    private void SavePosition()
    { if (_ready) _settings.Update(_settings.Current with { Position = _platform.CapturePlacement() }); }
    private void TogglePause()
    { _autoPaused = !_autoPaused; _behavior.SetAutomaticActionsPaused(_autoPaused); ResetBaseline(); UpdateTray(); }
    private void ToggleVisible()
    {
        _hidden = !_hidden;
        UpdateSuspension();
        if (_hidden) Hide(); else { Show(); _platform.KeepVisible(); ResetBaseline(); }
        UpdateSleepIndicator();
        UpdateTray();
    }
    private void UpdateSuspension()
    {
        var suspend = _hidden || _systemPaused;
        _behavior.SetSuspended(suspend);
        if (suspend) { EndPointer(cancel: true); _interaction.Cancel(); _timer.Stop(); }
        if (!suspend) { UpdateBusy(); ResetBaseline(); _timer.Start(); }
        UpdateRenderingSubscription();
    }
    private void RestorePosition()
    {
        if (_hidden) ToggleVisible();
        _platform.RestorePlacement();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { SavePosition(); ResetBaseline(); }));
    }
    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate(); return;
        }
        _settingsWindow = new SettingsWindow(_settings, _assets.Image(_assets.Clips["idle"].Frames[0].File));
        UpdateBusy(); ResetBaseline();
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; UpdateBusy(); ResetBaseline(); _settings.Flush(); };
        _settingsWindow.Show();
    }
    private void OpenMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("设置…", OpenSettings); Add(_autoPaused ? "恢复自动动作" : "暂停自动动作", TogglePause);
        Add("隐藏桌宠", ToggleVisible); Add("恢复位置", RestorePosition);
        menu.Items.Add(new Separator()); Add("退出", Quit);
        menu.Opened += (_, _) => SetMenuOpen(true); menu.Closed += (_, _) => SetMenuOpen(false);
        ContextMenu = menu; menu.IsOpen = true;
    }
    private void UpdateTray() => _tray?.Update(!_hidden, _autoPaused, _settings.Current.Topmost, _settings.Current.ClickThrough);
    public void Quit()
    {
        if (_closing) return;
        _closing = true; _timer.Stop(); UpdateRenderingSubscription(); EndPointer(cancel: true); SavePosition();
        _sleepIndicator?.Close(); _sleepIndicator = null;
        _settingsWindow?.Close(); _settings.Changed -= ApplySettings;
        _settings.Dispose(); _tray?.Dispose(); _platform.Dispose();
        Close(); System.Windows.Application.Current.Shutdown();
    }
}
