using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TablePet.App.Services;
using TablePet.Core;

namespace TablePet.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _service;
    private bool _loading = true;
    public SettingsWindow(SettingsService service, ImageSource preview)
    {
        InitializeComponent(); _service = service; Preview.Source = preview;
        LoadFields();
        _service.SaveFailed += OnSaveFailed;
        Closed += (_, _) => _service.SaveFailed -= OnSaveFailed;
    }
    private void LoadFields()
    {
        _loading = true;
        var s = _service.Current;
        AutoSleep.IsChecked = s.AutoSleep;
        SleepSeconds.Text = Seconds(s.SleepAfterMs); BlinkMin.Text = Seconds(s.BlinkMinMs);
        BlinkMax.Text = Seconds(s.BlinkMaxMs); HoldSeconds.Text = Seconds(s.HoldMs);
        CooldownSeconds.Text = Seconds(s.CooldownMs); HoverMs.Text = s.HoverDwellMs.ToString();
        PetSize.Value = s.PetHeightDip; SizeLabel.Text = $"{s.PetHeightDip:0} DIP";
        AlwaysTop.IsChecked = s.Topmost; PassThrough.IsChecked = s.ClickThrough;
        Startup.IsChecked = s.StartWithWindows;
        _loading = false;
    }
    private static string Seconds(int ms) => (ms / 1000d).ToString("0.###", CultureInfo.CurrentCulture);
    private static int Parse(TextBox box, double factor)
    {
        if (!double.TryParse(box.Text, out var number) || !double.IsFinite(number) || number * factor > int.MaxValue || number * factor < 0)
            throw new ArgumentException("请输入有效的非负数字。");
        return checked((int)Math.Round(number * factor));
    }
    private bool Apply()
    {
        if (_loading) return true;
        try
        {
            var old = _service.Current;
            var updated = old with
            {
                AutoSleep = AutoSleep.IsChecked == true,
                SleepAfterMs = Parse(SleepSeconds, 1000), BlinkMinMs = Parse(BlinkMin, 1000),
                BlinkMaxMs = Parse(BlinkMax, 1000), HoldMs = Parse(HoldSeconds, 1000),
                CooldownMs = Parse(CooldownSeconds, 1000), HoverDwellMs = Parse(HoverMs, 1),
                PetHeightDip = PetSize.Value, Topmost = AlwaysTop.IsChecked == true,
                ClickThrough = PassThrough.IsChecked == true, StartWithWindows = Startup.IsChecked == true
            };
            updated.EnsureValid();
            if (updated.StartWithWindows != old.StartWithWindows) PlatformService.SetStartup(updated.StartWithWindows);
            _service.Update(updated);
            SizeLabel.Text = $"{updated.PetHeightDip:0} DIP";
            Feedback.Text = "已应用 · 设置会自动保存在这台电脑上。";
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
