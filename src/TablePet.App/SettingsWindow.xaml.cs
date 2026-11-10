using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TablePet.App.Services;
using TablePet.Core;

namespace TablePet.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _service;
    private readonly bool _canWork;
    private bool _loading = true;
    private bool _applying;
    private bool _closed;
    private bool _modeAppliedBeforeClick;
    private readonly Action? _openCompanion;
    public event Action? WorkModeChosen;
    public SettingsWindow(SettingsService service, ImageSource preview, bool canWork = true, Action? openCompanion = null)
    {
        InitializeComponent(); _service = service; _canWork = canWork; _openCompanion = openCompanion; Preview.Source = preview;
        OpenCompanionButton.IsEnabled = openCompanion is not null;
        LoadFields();
        _service.SaveFailed += OnSaveFailed;
        _service.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _service.SaveFailed -= OnSaveFailed;
            _service.Changed -= OnSettingsChanged;
        };
    }
    private void LoadFields()
    {
        _loading = true;
        var s = _service.Current;
        LoadMode(s.Mode);
        WorkMinutes.Text = Minutes(s.WorkDurationMs);
        BreakMinutes.Text = Minutes(s.BreakDurationMs);
        AutoSleep.IsChecked = s.AutoSleep;
        SleepSeconds.Text = Seconds(s.SleepAfterMs); BlinkMin.Text = Seconds(s.BlinkMinMs);
        BlinkMax.Text = Seconds(s.BlinkMaxMs); HoldSeconds.Text = Seconds(s.HoldMs);
        CooldownSeconds.Text = Seconds(s.CooldownMs); HoverMs.Text = s.HoverDwellMs.ToString();
        PetSize.Value = s.PetHeightDip; SizeLabel.Text = $"{s.PetHeightDip:0} DIP";
        AlwaysTop.IsChecked = s.Topmost; PassThrough.IsChecked = s.ClickThrough;
        Startup.IsChecked = s.StartWithWindows;
        RecordCompanionship.IsChecked = s.CompanionshipEnabled;
        CompanionText.IsChecked = s.CompanionTextEnabled;
        _loading = false;
    }
    private void LoadMode(CompanionMode mode)
    {
        DailyMode.IsChecked = mode == CompanionMode.Daily || !_canWork;
        WorkMode.IsChecked = mode == CompanionMode.Work && _canWork;
        WorkMode.IsEnabled = _canWork;
        if (!_canWork) Feedback.Text = "工作模式素材不完整，当前使用日常陪伴。";
    }
    private void OnSettingsChanged(PetSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSettingsChanged(_service.Current)));
            return;
        }
        if (_closed || _applying || _loading) return;
        // Tray mode changes must stay visible here. Refresh only mode, leaving
        // any text currently being edited intact until the user applies it.
        _loading = true;
        try
        {
            LoadMode(settings.Mode);
            RecordCompanionship.IsChecked = settings.CompanionshipEnabled;
            CompanionText.IsChecked = settings.CompanionTextEnabled;
        }
        finally { _loading = false; }
    }
    private static string Seconds(int ms) => (ms / 1000d).ToString("0.###", CultureInfo.CurrentCulture);
    // Preserve even a millisecond duration when a user simply opens and closes settings.
    private static string Minutes(int ms) => (ms / 60000d).ToString("0.########", CultureInfo.CurrentCulture);
    private static int Parse(TextBox box, double factor)
    {
        if (!double.TryParse(box.Text, out var number) || !double.IsFinite(number) || number * factor > int.MaxValue || number * factor < 0)
            throw new ArgumentException("请输入有效的非负数字。");
        return checked((int)Math.Round(number * factor));
    }
    private bool Apply(Action? beforePublish = null)
    {
        if (_loading) return true;
        try
        {
            var old = _service.Current;
            var updated = old with
            {
                Mode = _canWork && WorkMode.IsChecked == true ? CompanionMode.Work : CompanionMode.Daily,
                WorkDurationMs = Parse(WorkMinutes, 60000), BreakDurationMs = Parse(BreakMinutes, 60000),
                AutoSleep = AutoSleep.IsChecked == true,
                SleepAfterMs = Parse(SleepSeconds, 1000), BlinkMinMs = Parse(BlinkMin, 1000),
                BlinkMaxMs = Parse(BlinkMax, 1000), HoldMs = Parse(HoldSeconds, 1000),
                CooldownMs = Parse(CooldownSeconds, 1000), HoverDwellMs = Parse(HoverMs, 1),
                PetHeightDip = PetSize.Value, Topmost = AlwaysTop.IsChecked == true,
                ClickThrough = PassThrough.IsChecked == true, StartWithWindows = Startup.IsChecked == true,
                CompanionshipEnabled = RecordCompanionship.IsChecked == true,
                CompanionTextEnabled = CompanionText.IsChecked == true
            };
            updated.EnsureValid();
            if (updated.StartWithWindows != old.StartWithWindows) PlatformService.SetStartup(updated.StartWithWindows);
            _applying = true;
            try { beforePublish?.Invoke(); _service.Update(updated); }
            finally { _applying = false; }
            SizeLabel.Text = $"{updated.PetHeightDip:0} DIP";
            Feedback.Text = _canWork ? "已应用 · 设置会自动保存在这台电脑上。"
                : "已应用日常陪伴 · 工作模式素材不完整。";
            Feedback.Foreground = new SolidColorBrush(Color.FromRgb(104, 124, 100));
            return true;
        }
        catch (Exception error)
        {
            Feedback.Text = error.Message;
            Feedback.Foreground = Brushes.IndianRed;
            return false;
        }
    }
    private void OnSaveFailed(string message) => Dispatcher.BeginInvoke(new Action(() =>
    { Feedback.Text = message; Feedback.Foreground = Brushes.IndianRed; }));
    private void Option_Changed(object sender, RoutedEventArgs e) => Apply();
    private void ApplyModeChoice() => Apply(
        !_loading && _canWork && WorkMode.IsChecked == true ? () => WorkModeChosen?.Invoke() : null);
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Direction-key selection can raise Checked without Click. Mouse selection raises both.
        _modeAppliedBeforeClick = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _modeAppliedBeforeClick = false));
        ApplyModeChoice();
    }
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_modeAppliedBeforeClick) { _modeAppliedBeforeClick = false; return; }
        ApplyModeChoice();
    }
    private void Companion_Click(object sender, RoutedEventArgs e) => _openCompanion?.Invoke();
    private void Number_Changed(object sender, RoutedEventArgs e) => Apply();
    private void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();
    private void Done_Click(object sender, RoutedEventArgs e) { if (Apply()) Close(); }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_service.Current.StartWithWindows) PlatformService.SetStartup(false);
            _service.Update(new PetSettings { Position = _service.Current.Position }, saveImmediately: true);
            LoadFields(); Feedback.Text = "已恢复默认设置。";
        }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
}
