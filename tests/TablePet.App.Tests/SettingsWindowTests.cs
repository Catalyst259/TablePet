using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class SettingsWindowTests
{
    private static void Done(SettingsWindow window) => typeof(SettingsWindow)
        .GetMethod("Done_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(window, [window, new RoutedEventArgs()]);

    [Fact]
    public void ChoosingWorkModeAppliesImmediatelyAndKeepsDailyPreferences() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        var daily = new PetSettings { BlinkMinMs = 4000, BlinkMaxMs = 9000, SleepAfterMs = 90000 };
        service.Update(daily, saveImmediately: true);
        var window = new SettingsWindow(service, new DrawingImage());
        try
        {
            Assert.True(((RadioButton)window.FindName("DailyMode")).IsChecked);
            Assert.Equal("25", ((TextBox)window.FindName("WorkMinutes")).Text);
            Assert.Equal("5", ((TextBox)window.FindName("BreakMinutes")).Text);
            ((RadioButton)window.FindName("WorkMode")).IsChecked = true;
            Assert.Equal(daily with { Mode = CompanionMode.Work }, service.Current);
            Assert.True(service.Flush());
            using var restarted = new SettingsService(service.SettingsPath);
            Assert.Equal(service.Current, restarted.Load());

            ((RadioButton)window.FindName("DailyMode")).IsChecked = true;
            Assert.Equal(daily, service.Current);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MinuteEditsSaveFractionsAndRejectInvalidIntervals() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        var window = new SettingsWindow(service, new DrawingImage());
        try
        {
            var work = (TextBox)window.FindName("WorkMinutes");
            var rest = (TextBox)window.FindName("BreakMinutes");
            work.Text = 0.5.ToString(System.Globalization.CultureInfo.CurrentCulture);
            rest.Text = 0.25.ToString(System.Globalization.CultureInfo.CurrentCulture);
            work.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(30000, service.Current.WorkDurationMs);
            Assert.Equal(15000, service.Current.BreakDurationMs);
            var valid = service.Current;
            work.Text = "0";
            work.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(valid, service.Current);
            Assert.NotEmpty(((TextBlock)window.FindName("Feedback")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OpeningSettingsPreservesMillisecondDurations() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        service.Update(new PetSettings { WorkDurationMs = 1, BreakDurationMs = 1234567 });
        var window = new SettingsWindow(service, new DrawingImage());
        try
        {
            // Applying another field must not round minute displays down to zero.
            ((RadioButton)window.FindName("WorkMode")).IsChecked = true;
            Assert.Equal(CompanionMode.Work, service.Current.Mode);
            Assert.Equal(1, service.Current.WorkDurationMs);
            Assert.Equal(1234567, service.Current.BreakDurationMs);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ExternalWorkModeChangeKeepsPendingTextAndDoneDoesNotSwitchBackToDaily() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        var window = new SettingsWindow(service, new DrawingImage());
        try
        {
            var minutes = (TextBox)window.FindName("WorkMinutes");
            minutes.Text = "30";
            service.Update(service.Current with { Mode = CompanionMode.Work });
            Assert.True(((RadioButton)window.FindName("WorkMode")).IsChecked);
            Assert.False(((RadioButton)window.FindName("DailyMode")).IsChecked);
            Assert.Equal("30", minutes.Text);
            Assert.Equal(1500000, service.Current.WorkDurationMs);
            Done(window);
            Assert.Equal(CompanionMode.Work, service.Current.Mode);
            Assert.Equal(1800000, service.Current.WorkDurationMs);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ExternalDailyModeChangeIsNotOverwrittenByTheOldWorkSelection() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        service.Update(new PetSettings { Mode = CompanionMode.Work });
        var window = new SettingsWindow(service, new DrawingImage());
        try
        {
            service.Update(service.Current with { Mode = CompanionMode.Daily });
            Assert.True(((RadioButton)window.FindName("DailyMode")).IsChecked);
            Done(window);
            Assert.Equal(CompanionMode.Daily, service.Current.Mode);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AModeEditPublishesOnceWithoutChangedEventReentry() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        var window = new SettingsWindow(service, new DrawingImage());
        try
        {
            var updates = 0;
            service.Changed += _ => updates++;
            ((RadioButton)window.FindName("WorkMode")).IsChecked = true;
            Assert.Equal(1, updates);
            Assert.Equal(CompanionMode.Work, service.Current.Mode);
            ((TextBox)window.FindName("WorkMinutes")).Text = "unfinished";
            service.Update(service.Current with { Mode = CompanionMode.Daily });
            Assert.Equal(2, updates);
            Assert.Equal("unfinished", ((TextBox)window.FindName("WorkMinutes")).Text);
            Assert.True(((RadioButton)window.FindName("DailyMode")).IsChecked);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(CompanionMode.Daily)]
    [InlineData(CompanionMode.Work)]
    public void MissingWorkAssetsDisableWorkAndShowTheEffectiveDailyMode(CompanionMode savedMode) => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var service = new SettingsService(directory.FilePath("settings.json"));
        service.Update(new PetSettings { Mode = savedMode });
        var window = new SettingsWindow(service, new DrawingImage(), canWork: false);
        try
        {
            Assert.False(((RadioButton)window.FindName("WorkMode")).IsEnabled);
            Assert.False(((RadioButton)window.FindName("WorkMode")).IsChecked);
            Assert.True(((RadioButton)window.FindName("DailyMode")).IsChecked);
            Assert.Contains("素材不完整", ((TextBlock)window.FindName("Feedback")).Text);
            service.Update(service.Current with { Mode = CompanionMode.Work });
            Assert.True(((RadioButton)window.FindName("DailyMode")).IsChecked);
            Assert.False(((RadioButton)window.FindName("WorkMode")).IsChecked);
            Done(window);
            Assert.Equal(CompanionMode.Daily, service.Current.Mode);
        }
        finally { window.Close(); }
    });
}
