using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class CompanionWindowTests
{
    [Fact]
    public void EmptyPanelShowsNoInventedHistoryOrUnlockedMemories() => Sta.Run(() =>
    {
        using var fixture = new PanelFixture();
        var window = fixture.CreateWindow();
        try
        {
            Assert.Equal("初见", Find<TextBlock>(window, "StageName").Text);
            Assert.Equal("已相伴 0 天", Find<TextBlock>(window, "DayCount").Text);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "EmptyNotice").Visibility);
            Assert.Empty(Find<StackPanel>(window, "MemoryItems").Children);
            Assert.Empty(Find<TextBlock>(window, "RecordingDate").Text);
            Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StatusBanner").Visibility);
            Assert.False(window.Topmost);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MemoriesShowOnlyUnlockedEntriesInRecordedOrder() => Sta.Run(() =>
    {
        var state = SeedState(3, "first_day", "first_firefly_visit");
        using var fixture = new PanelFixture(state);
        var window = fixture.CreateWindow();
        try
        {
            var rows = Find<StackPanel>(window, "MemoryItems").Children.OfType<Border>().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Contains("桌边微光", TextWithin(rows[0]));
            Assert.Contains("第一个相伴日", TextWithin(rows[1]));
            var memories = TextWithin(Find<StackPanel>(window, "MemoryItems"));
            Assert.DoesNotContain("一起忙一会儿", memories);
            Assert.DoesNotContain("六十个桌边日常", memories);
            Assert.DoesNotContain("未解锁", memories);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "EmptyNotice").Visibility);
            // Constructing a panel is not evidence that its offscreen entries were read.
            Assert.All(fixture.Session.State.Memories, memory => Assert.False(memory.IsRead));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(320, 96)]
    [InlineData(380, 192)]
    public void MemoryPicturesAndCopyFitNarrowCardsWithoutChangingReadState(int cardWidth, int dpi) => Sta.Run(() =>
    {
        using var fixture = new PanelFixture(SeedState(60, CompanionContent.Default.Memories.Select(m => m.Id).ToArray()));
        var window = fixture.CreateWindow();
        try
        {
            var list = Find<StackPanel>(window, "MemoryItems");
            list.Measure(new Size(cardWidth, double.PositiveInfinity));
            list.Arrange(new Rect(0, 0, cardWidth, list.DesiredSize.Height));
            list.UpdateLayout();
            Assert.Equal(CompanionContent.Default.Memories.Count, list.Children.Count);
            foreach (var card in list.Children.OfType<Border>())
            {
                var row = Assert.IsType<Grid>(card.Child);
                var image = Assert.Single(row.Children.OfType<Image>());
                var copy = Assert.Single(row.Children.OfType<StackPanel>());
                var imageBounds = image.TransformToAncestor(card).TransformBounds(new Rect(image.RenderSize));
                var copyBounds = copy.TransformToAncestor(card).TransformBounds(new Rect(copy.RenderSize));
                Assert.NotNull(image.Source);
                Assert.Equal(80, image.ActualWidth);
                Assert.True(imageBounds.Right <= copyBounds.Left);
                Assert.True(copyBounds.Right < card.ActualWidth);
                Assert.True(copy.ActualWidth >= 180);
                Assert.All(copy.Children.OfType<TextBlock>(), text => Assert.True(text.ActualWidth <= copy.ActualWidth));
                Assert.NotEmpty(AutomationProperties.GetName(card));
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(image));
            }
            Assert.All(fixture.Session.State.Memories, memory => Assert.False(memory.IsRead));

            if (Environment.GetEnvironmentVariable("TABLEPET_MEMORY_RENDER_DIR") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(cardWidth * dpi / 96d),
                    (int)Math.Ceiling(list.ActualHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(list);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(folder, $"memories-{cardWidth}dip-{dpi}dpi.png"));
                encoder.Save(output);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(0, "初见", "熟悉", 12, 3)]
    [InlineData(3, "熟悉", "亲近", 40, 8)]
    [InlineData(8, "亲近", "默契", 100, 21)]
    public void PanelShowsTheCurrentStageAndBothNextStageRequirements(int days, string label,
        string nextLabel, int nextPoints, int nextDays) => Sta.Run(() =>
    {
        using var fixture = new PanelFixture(SeedState(days));
        var window = fixture.CreateWindow();
        try
        {
            Assert.Equal(label, Find<TextBlock>(window, "StageName").Text);
            Assert.Equal($"已相伴 {days} 天", Find<TextBlock>(window, "DayCount").Text);
            Assert.Equal(Visibility.Visible, Find<ProgressBar>(window, "StageProgress").Visibility);
            Assert.Equal($"到“{nextLabel}”需要 {nextPoints} 熟悉度和 {nextDays} 个相伴日。",
                Find<TextBlock>(window, "NextRequirement").Text);
            Assert.Equal($"熟悉度 {days * 6} · 已相伴 {days} 天", Find<TextBlock>(window, "ScoreDetail").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void HighestStageKeepsTheHistoryAndHidesTheNextLevelProgress() => Sta.Run(() =>
    {
        using var fixture = new PanelFixture(SeedState(21, "stage_in_sync"));
        var window = fixture.CreateWindow();
        try
        {
            Assert.Equal("默契", Find<TextBlock>(window, "StageName").Text);
            Assert.Equal(Visibility.Collapsed, Find<ProgressBar>(window, "StageProgress").Visibility);
            Assert.Empty(Find<TextBlock>(window, "NextRequirement").Text);
            Assert.Equal("日常还在继续。", Find<TextBlock>(window, "ProgressDescription").Text);
            Assert.Contains("不用多说", TextWithin(Find<StackPanel>(window, "MemoryItems")));
            Assert.Equal("已相伴 21 天", Find<TextBlock>(window, "DayCount").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TurningOffGrowthKeepsTheStageAndMemoriesVisibleAndDurable() => Sta.Run(() =>
    {
        using var fixture = new PanelFixture(SeedState(3, "first_day", "stage_familiar"));
        var window = fixture.CreateWindow();
        try
        {
            fixture.Session.Activate();
            var points = fixture.Session.State.TotalPoints;
            fixture.Settings.Update(fixture.Settings.Current with { CompanionshipEnabled = false }, saveImmediately: true);
            Assert.Equal(Visibility.Visible, Find<TextBlock>(window, "DisabledNotice").Visibility);
            Assert.Equal("熟悉", Find<TextBlock>(window, "StageName").Text);
            Assert.Equal(2, Find<StackPanel>(window, "MemoryItems").Children.Count);
            Assert.Equal(points, fixture.Session.State.TotalPoints);
            fixture.Session.Store.WaitForPendingWritesAsync().GetAwaiter().GetResult();
            var reloaded = new CompanionStateService(fixture.StatePath).Load();
            Assert.Equal("familiar", reloaded.Stage);
            Assert.Equal(points, reloaded.TotalPoints);
            Assert.Equal(new[] { "first_day", "stage_familiar" }, reloaded.Memories.Select(x => x.Id));

            fixture.Settings.Update(fixture.Settings.Current with { CompanionshipEnabled = true }, saveImmediately: true);
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "DisabledNotice").Visibility);
            Assert.Equal("熟悉", Find<TextBlock>(window, "StageName").Text);
            Assert.Equal(2, Find<StackPanel>(window, "MemoryItems").Children.Count);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SettingsCheckboxesPersistIndependentlyAndReopenWithTheSavedValues() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var settings = new SettingsService(directory.FilePath("settings.json"));
        var window = new SettingsWindow(settings, new DrawingImage());
        try
        {
            var record = Find<CheckBox>(window, "RecordCompanionship");
            var text = Find<CheckBox>(window, "CompanionText");
            Assert.True(record.IsChecked);
            Assert.True(text.IsChecked);
            SetCheckbox(text, false);
            Assert.True(settings.Current.CompanionshipEnabled);
            Assert.False(settings.Current.CompanionTextEnabled);
            SetCheckbox(record, false);
            Assert.False(settings.Current.CompanionshipEnabled);
            Assert.False(settings.Current.CompanionTextEnabled);
            Assert.True(settings.Flush());
        }
        finally { window.Close(); }

        using var reloaded = new SettingsService(settings.SettingsPath);
        var saved = reloaded.Load();
        Assert.False(saved.CompanionshipEnabled);
        Assert.False(saved.CompanionTextEnabled);
        var reopened = new SettingsWindow(reloaded, new DrawingImage());
        try
        {
            Assert.False(Find<CheckBox>(reopened, "RecordCompanionship").IsChecked);
            Assert.False(Find<CheckBox>(reopened, "CompanionText").IsChecked);
            SetCheckbox(Find<CheckBox>(reopened, "CompanionText"), true);
            Assert.False(reloaded.Current.CompanionshipEnabled);
            Assert.True(reloaded.Current.CompanionTextEnabled);
        }
        finally { reopened.Close(); }
    });

    [Fact]
    public void ResetDefaultsRestoresBothOptionsWithoutWritingTheIndependentGrowthFile() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        var statePath = directory.FilePath("companion-state.json");
        WriteState(statePath, SeedState(8, "first_day", "stage_close"));
        var originalStateBytes = File.ReadAllBytes(statePath);
        using var settings = new SettingsService(directory.FilePath("settings.json"));
        var position = new WindowPlacement { MonitorId = "TEST_DISPLAY", RelativeX = 0.25, RelativeY = 0.75 };
        settings.Update(new PetSettings
        {
            CompanionshipEnabled = false, CompanionTextEnabled = false, Position = position
        }, saveImmediately: true);
        var window = new SettingsWindow(settings, new DrawingImage());
        try
        {
            typeof(SettingsWindow).GetMethod("Reset_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            Assert.True(settings.Current.CompanionshipEnabled);
            Assert.True(settings.Current.CompanionTextEnabled);
            Assert.True(Find<CheckBox>(window, "RecordCompanionship").IsChecked);
            Assert.True(Find<CheckBox>(window, "CompanionText").IsChecked);
            Assert.Equal(position, settings.Current.Position);
            Assert.Equal(originalStateBytes, File.ReadAllBytes(statePath));
            Assert.False(File.Exists(statePath + ".bak"));
            var reloaded = new CompanionStateService(statePath).Load();
            Assert.Equal("close", reloaded.Stage);
            Assert.Equal(8, reloaded.CompanionDays);
            Assert.Equal(2, reloaded.Memories.Count);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OlderSettingsWithoutCompanionshipFieldsUseEnabledDefaults()
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"petHeightDip":180}""");
        using var settings = new SettingsService(path);
        var loaded = settings.Load();
        Assert.True(loaded.CompanionshipEnabled);
        Assert.True(loaded.CompanionTextEnabled);
        Assert.Equal(180, loaded.PetHeightDip);
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));

    private static void SetCheckbox(CheckBox checkbox, bool value)
    {
        checkbox.IsChecked = value;
        checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static string TextWithin(DependencyObject root)
    {
        if (root is TextBlock text) return text.Text;
        return string.Join("\n", LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Select(TextWithin));
    }

    private static CompanionState SeedState(int days, params string[] memoryIds)
    {
        var today = new DateOnly(2026, 11, 10);
        var state = new CompanionState { StateRevision = 10 };
        if (days > 0)
        {
            state.RecordingStartedLocalDate = today.AddDays(1 - days).ToString("yyyy-MM-dd");
            state.RecordingStartedAtUtc = new DateTimeOffset(today.AddDays(1 - days).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            state.LastActiveLocalDate = today.ToString("yyyy-MM-dd");
        }
        for (var i = 0; i < days; i++)
            state.Days[today.AddDays(-i).ToString("yyyy-MM-dd")] = new CompanionDay
            {
                QualifiedPresenceMs = 1_200_000, PointsGranted = 6, CompanionDayGranted = true, ReturnEligible = false
            };
        for (var i = 0; i < memoryIds.Length; i++)
            state.Memories.Add(new CompanionMemory
            {
                Id = memoryIds[i], Sequence = i + 1, LocalDate = "2026-11-10",
                UnlockedAtUtc = new DateTimeOffset(2026, 11, 10, 0, i, 0, TimeSpan.Zero)
            });
        Assert.Empty(state.Validate());
        return state;
    }

    private static void WriteState(string path, CompanionState state) => File.WriteAllText(path,
        JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private sealed class PanelFixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public SettingsService Settings { get; }
        public CompanionSession Session { get; }
        public string StatePath => _directory.FilePath("companion-state.json");

        public PanelFixture(CompanionState? state = null)
        {
            if (state is not null) WriteState(StatePath, state);
            Settings = new SettingsService(_directory.FilePath("settings.json"));
            var clock = new StationaryClock();
            var clips = new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" }
                .ToDictionary(name => name, name => new AnimationClip(name,
                    [new AnimationFrame("rest.png", 1000)], name is "idle" or "sleep_loop"));
            var behavior = new BehaviorController(clock, clips, Settings.Current, (min, _) => min);
            Session = new CompanionSession(clock, behavior, Settings.Current,
                () => new CompanionHostContext(true, true, false, false, false, false, false, 240),
                StatePath, new FixedCalendar());
            Settings.Changed += Session.ApplySettings;
        }

        public CompanionWindow CreateWindow() => new(Session, Settings, new DrawingImage());

        public void Dispose()
        {
            Settings.Changed -= Session.ApplySettings;
            Session.Dispose();
            Settings.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class StationaryClock : IClock { public long NowMilliseconds => 0; }
    private sealed class FixedCalendar : ICalendarClock
    {
        public CompanionCalendarTime GetCurrent() => new(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
    }
}
