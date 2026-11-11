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
    private static readonly (int Work, int Break)[] WorkPresets =
        [(25 * 60000, 5 * 60000), (50 * 60000, 10 * 60000), (45 * 60000, 15 * 60000)];
    private static readonly int[] SleepPresets = [60000, 5 * 60000, 15 * 60000];
    public event Action? WorkModeChosen;
    public SettingsWindow(SettingsService service, ImageSource preview, bool canWork = true, Action? openCompanion = null)
    {
        InitializeComponent(); _service = service; _canWork = canWork; _openCompanion = openCompanion; Preview.Source = preview;
        OpenCompanionButton.IsEnabled = openCompanion is not null;
        LoadFields();
        _service.SaveFailed += OnSaveFailed;
        _service.Changed += OnSettingsChanged;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(460, area.Width); MinHeight = Math.Min(480, area.Height);
            Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        };
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
        var workPreset = Array.FindIndex(WorkPresets, preset => preset.Work == s.WorkDurationMs && preset.Break == s.BreakDurationMs);
        WorkPreset.SelectedIndex = workPreset < 0 ? WorkPresets.Length : workPreset;
        AutoSleep.IsChecked = s.AutoSleep;
        SleepSeconds.Text = Seconds(s.SleepAfterMs);
        var sleepPreset = Array.IndexOf(SleepPresets, s.SleepAfterMs);
        SleepPreset.SelectedIndex = sleepPreset < 0 ? SleepPresets.Length : sleepPreset;
        PetSize.Value = s.PetHeightDip; SizeLabel.Text = SizeText(s.PetHeightDip);
        AlwaysTop.IsChecked = s.Topmost; PassThrough.IsChecked = s.ClickThrough;
        Startup.IsChecked = s.StartWithWindows;
        RecordCompanionship.IsChecked = s.CompanionshipEnabled;
        CompanionText.IsChecked = s.CompanionTextEnabled;
        UpdateSections();
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
            UpdateSections();
        }
        finally { _loading = false; }
    }
    private static string Seconds(int ms) => (ms / 1000d).ToString("0.###", CultureInfo.CurrentCulture);
    private static string SizeText(double height) => $"{height / 240:0%}";
    // Preserve even a millisecond duration when a user simply opens and closes settings.
    private static string Minutes(int ms) => (ms / 60000d).ToString("0.########", CultureInfo.CurrentCulture);
    private static int Parse(TextBox box, double factor, string label, int maximum)
    {
        if (!double.TryParse(box.Text, out var number) || !double.IsFinite(number)
            || number * factor > maximum || number * factor < 0.5)
            throw new ArgumentException($"{label}请输入大于 0、且不超过 {maximum / factor:0} 的数字。");
        return (int)Math.Round(number * factor);
    }
    private bool Apply(Action? beforePublish = null, bool applyWorkDuration = false, bool applySleepDelay = false)
    {
        if (_loading) return true;
        try
        {
            var old = _service.Current;
            var workMode = _canWork && WorkMode.IsChecked == true;
            // Hidden timing fields never block unrelated choices. Keep the stored values
            // until their mode is selected or the user explicitly edits that duration.
            var saveWork = workMode || applyWorkDuration;
            var saveSleep = (!workMode && AutoSleep.IsChecked == true) || applySleepDelay;
            var updated = old with
            {
                Mode = workMode ? CompanionMode.Work : CompanionMode.Daily,
                WorkDurationMs = saveWork ? Parse(WorkMinutes, 60000, "工作分钟数", 28800000) : old.WorkDurationMs,
                BreakDurationMs = saveWork ? Parse(BreakMinutes, 60000, "休息分钟数", 28800000) : old.BreakDurationMs,
                AutoSleep = AutoSleep.IsChecked == true,
                SleepAfterMs = saveSleep ? Parse(SleepSeconds, 1000, "入睡等待秒数", 604800000) : old.SleepAfterMs,
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
            SizeLabel.Text = SizeText(updated.PetHeightDip);
            UpdateSections();
            Feedback.Text = _canWork ? "已应用 · 设置会自动保存。"
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
    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSections();
        Apply();
    }
    private void ApplyModeChoice() => Apply(
        !_loading && _canWork && WorkMode.IsChecked == true ? () => WorkModeChosen?.Invoke() : null);
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Direction-key selection can raise Checked without Click. Mouse selection raises both.
        _modeAppliedBeforeClick = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _modeAppliedBeforeClick = false));
        UpdateSections();
        ApplyModeChoice();
    }
    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_modeAppliedBeforeClick) { _modeAppliedBeforeClick = false; return; }
        ApplyModeChoice();
    }
    private void Companion_Click(object sender, RoutedEventArgs e) => _openCompanion?.Invoke();
    private void Number_Changed(object sender, RoutedEventArgs e) => Apply(
        applyWorkDuration: ReferenceEquals(sender, WorkMinutes) || ReferenceEquals(sender, BreakMinutes),
        applySleepDelay: ReferenceEquals(sender, SleepSeconds));
    private void WorkPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (WorkPreset.SelectedIndex >= 0 && WorkPreset.SelectedIndex < WorkPresets.Length)
        {
            var preset = WorkPresets[WorkPreset.SelectedIndex];
            _loading = true;
            try { WorkMinutes.Text = Minutes(preset.Work); BreakMinutes.Text = Minutes(preset.Break); }
            finally { _loading = false; }
            Apply(applyWorkDuration: true);
        }
        UpdateSections();
    }
    private void SleepPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SleepPreset.SelectedIndex >= 0 && SleepPreset.SelectedIndex < SleepPresets.Length)
        {
            _loading = true;
            try { SleepSeconds.Text = Seconds(SleepPresets[SleepPreset.SelectedIndex]); }
            finally { _loading = false; }
            Apply(applySleepDelay: true);
        }
        UpdateSections();
    }
    private void UpdateSections()
    {
        var workMode = _canWork && WorkMode.IsChecked == true;
        DailyOptions.Visibility = workMode ? Visibility.Collapsed : Visibility.Visible;
        WorkOptions.Visibility = workMode ? Visibility.Visible : Visibility.Collapsed;
        SleepOptions.Visibility = AutoSleep.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CustomWork.Visibility = WorkPreset.SelectedIndex == WorkPresets.Length ? Visibility.Visible : Visibility.Collapsed;
        CustomSleep.Visibility = SleepPreset.SelectedIndex == SleepPresets.Length ? Visibility.Visible : Visibility.Collapsed;
        CompanionText.IsEnabled = RecordCompanionship.IsChecked == true;
        TextPreferenceHint.Text = RecordCompanionship.IsChecked == true
            ? "关闭后仍可成长，工作和休息提醒照常显示。"
            : "开启成长记录后可显示短句，基础工作和休息提醒不受影响。";
    }
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
