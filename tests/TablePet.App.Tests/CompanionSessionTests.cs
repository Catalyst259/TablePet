using System.Windows.Threading;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class CompanionSessionTests
{
    private sealed class TestClock : IClock, ICalendarClock
    {
        private static readonly DateTimeOffset Epoch = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        public long NowMilliseconds { get; private set; }
        public void Advance(long milliseconds) => NowMilliseconds += milliseconds;
        public CompanionCalendarTime GetCurrent() => new(Epoch.AddMilliseconds(NowMilliseconds), TimeZoneInfo.Utc);
    }

    private sealed class SessionHarness : IDisposable
    {
        public TestClock Clock { get; } = new();
        public BehaviorController Behavior { get; }
        public CompanionSession Session { get; }
        public PetSettings Settings { get; private set; }
        public bool Visible { get; private set; } = true;
        public List<BehaviorFact> Facts { get; } = [];

        public SessionHarness(string statePath, PetSettings? settings = null)
        {
            Settings = settings ?? new PetSettings { AutoSleep = false, CompanionTextEnabled = false };
            Behavior = new BehaviorController(Clock, Pack(), Settings, (min, _) => min);
            Session = new CompanionSession(Clock, Behavior, Settings, () => new CompanionHostContext(
                Visible, true, Behavior.IsDragging,
                Behavior.Mode == CompanionMode.Work && Behavior.AutomaticActionsPaused,
                false, false, Behavior.Mode == CompanionMode.Work, 240,
                AllowMilestone: !Behavior.AutomaticActionsPaused), statePath, Clock);
            Behavior.FactOccurred += Facts.Add;
        }

        public void Advance(long milliseconds)
        {
            while (milliseconds > 0)
            {
                var step = Math.Min(1000, milliseconds);
                Clock.Advance(step);
                Behavior.Tick();
                Session.Tick();
                milliseconds -= step;
            }
        }

        public void Finish() => Advance(Math.Max(1, Behavior.Player.CurrentClip!.DurationMs - Behavior.Player.ElapsedMs));

        public void Apply(PetSettings settings)
        {
            Settings = settings;
            Session.ApplySettings(settings);
            Behavior.ApplySettings(settings);
            Session.SynchronizeContext();
        }

        public void StartWork(int workMs = 600000, int breakMs = 60000)
        {
            Session.PrepareManualWorkStart();
            Apply(Settings with { Mode = CompanionMode.Work, WorkDurationMs = workMs, BreakDurationMs = breakMs });
            Finish(); Finish();
            Assert.Equal(WorkPhase.Working, Behavior.WorkPhase);
        }

        public void PauseWork(bool paused)
        {
            Behavior.SetAutomaticActionsPaused(paused);
            Session.SynchronizeContext();
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            Session.SynchronizeContext();
            Behavior.SetSuspended(!visible);
        }

        public void DrainSaves()
        {
            Session.Store.WaitForPendingWritesAsync().GetAwaiter().GetResult();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        public void Dispose()
        {
            Session.Dispose();
            Behavior.FactOccurred -= Facts.Add;
        }
    }

    private static Dictionary<string, AnimationClip> Pack()
    {
        AnimationClip Rest(string name, int duration) => new(name,
            [new("idle.png", 17), new(name + ".png", duration - 34), new("idle.png", 17)]);
        return new()
        {
            ["idle"] = new("idle", [new("idle.png", 1000)], true),
            ["blink"] = Rest("blink", 375), ["meow"] = Rest("meow", 1400),
            ["wink"] = Rest("wink", 1500), ["yawn"] = Rest("yawn", 4400),
            ["fall_asleep"] = new("fall_asleep", [new("idle.png", 100), new("half.png", 100), new("sleep.png", 100)]),
            ["sleep_loop"] = new("sleep_loop", [new("sleep.png", 100), new("breath.png", 100)], true),
            ["wake"] = new("wake", [new("sleep.png", 100), new("half.png", 100), new("idle.png", 100)]),
            ["rise"] = new("rise", [new("idle.png", 100), new("rise.png", 100)]),
            ["hold"] = new("hold", [new("rise.png", 1000)], true),
            ["settle"] = new("settle", [new("rise.png", 100), new("idle.png", 100)]),
            ["work_enter"] = new("work_enter", [new("idle.png", 200), new("focus.png", 200)]),
            ["work_loop"] = new("work_loop", [new("focus.png", 1250), new("blink.png", 375), new("focus.png", 7975)], true),
            ["work_exit"] = new("work_exit", [new("focus.png", 200), new("idle.png", 200)]),
            ["work_props"] = new("work_props", [new("computer.png", 17)], true)
        };
    }

    [Fact]
    public void AutomaticWorkDoesNotActivateGrowthButABusyClickDoes() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"), new PetSettings
        { Mode = CompanionMode.Work, WorkDurationMs = 600000, AutoSleep = false, CompanionTextEnabled = false });
        harness.Advance(60000);
        Assert.False(harness.Session.Engine.HasActiveWindow);
        Assert.Equal(0, harness.Session.State.TotalPoints);
        harness.Behavior.Click();
        Assert.True(harness.Session.Engine.HasActiveWindow);
        Assert.Equal("工作呢，别打扰我", harness.Session.Speech.SpeechText);
        Assert.Equal(0, harness.Session.State.TotalPoints);
        harness.Advance(60000);
        Assert.Equal(3, harness.Session.State.TotalPoints);
        Assert.Equal(1, harness.Session.State.CompanionDays);
    });

    [Fact]
    public void WorkAndBreakFactsMapBackToTheSameQualifiedWorkRound() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"));
        harness.StartWork(); harness.Advance(600000);
        Assert.Contains(harness.Session.State.Memories, memory => memory.Id == "first_work_completed");
        harness.Finish(); harness.Finish(); harness.Finish();
        Assert.Equal(WorkPhase.Resting, harness.Behavior.WorkPhase);
        var work = Assert.Single(harness.Facts, fact => fact.Kind == BehaviorFactKind.WorkRoundStarted);
        var rest = Assert.Single(harness.Facts, fact => fact.Kind == BehaviorFactKind.BreakStarted);
        Assert.Equal(work.Id, rest.RelatedId);
        harness.Advance(60000);
        Assert.Contains(harness.Session.State.Memories, memory => memory.Id == "first_break_completed");
        Assert.Equal(rest.Id, Assert.Single(harness.Facts, fact => fact.Kind == BehaviorFactKind.BreakEnded).RelatedId);
    });

    [Fact]
    public void CancellingAQualifiedWorkRoundDoesNotUnlockCompletionMemories() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"));
        harness.StartWork(workMs: 900000); harness.Advance(600000);
        Assert.Equal(600000, harness.Session.Engine.CurrentWorkQualifiedMs);
        harness.Apply(harness.Settings with { Mode = CompanionMode.Daily });
        harness.Finish();
        Assert.DoesNotContain(harness.Session.State.Memories,
            memory => memory.Id is "first_work_completed" or "first_break_completed");
    });

    [Fact]
    public void PausedWorkStillAccumulatesVisiblePresenceButNotWorkQualification() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"));
        harness.StartWork(); harness.Advance(1000);
        var before = harness.Session.Engine.CurrentWorkQualifiedMs;
        var presenceBefore = harness.Session.State.Days.Values.Sum(day => day.QualifiedPresenceMs);
        harness.PauseWork(true); harness.Advance(10000);
        Assert.Equal(before, harness.Session.Engine.CurrentWorkQualifiedMs);
        Assert.Equal(presenceBefore + 10000, harness.Session.State.Days.Values.Sum(day => day.QualifiedPresenceMs));
        harness.PauseWork(false); harness.Advance(599000);
        Assert.Contains(harness.Session.State.Memories, memory => memory.Id == "first_work_completed");
    });

    [Fact]
    public void HidingDoesNotBackfillPresenceWhenTheCatReturns() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"));
        harness.Behavior.DoubleClick(); harness.Advance(30000);
        harness.SetVisible(false); harness.Advance(60000);
        Assert.Equal(30000, harness.Session.State.Days.Values.Sum(day => day.QualifiedPresenceMs));
        Assert.Null(harness.Session.Speech.SpeechText);
        harness.SetVisible(true); harness.Advance(30000);
        Assert.Equal(60000, harness.Session.State.Days.Values.Sum(day => day.QualifiedPresenceMs));
        Assert.Equal(3, harness.Session.State.TotalPoints);
    });

    [Fact]
    public void ReenablingGrowthDuringWorkCanQualifyTheRemainingVisibleWork() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"));
        harness.StartWork(workMs: 700000); harness.Advance(10000);
        harness.Apply(harness.Settings with { CompanionshipEnabled = false });
        harness.Advance(10000);
        harness.Apply(harness.Settings with { CompanionshipEnabled = true });
        Assert.False(harness.Session.Engine.HasActiveWindow);
        harness.Behavior.Click(); harness.Advance(680000);
        Assert.Contains(harness.Session.State.Memories, memory => memory.Id == "first_work_completed");
    });

    [Fact]
    public void FirstGreetingIsDurableAndDoesNotRepeatAfterRestart() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("companion.json");
        var settings = new PetSettings { AutoSleep = false };
        using (var first = new SessionHarness(path, settings))
        {
            first.Behavior.DoubleClick(); first.DrainSaves();
            Assert.True(Assert.Single(first.Session.State.Days).Value.GreetingHandled);
            Assert.NotNull(first.Session.Speech.SpeechText);
            Assert.NotEqual("喵", first.Session.Speech.SpeechText);
        }
        using var restarted = new SessionHarness(path, settings);
        restarted.Behavior.DoubleClick(); restarted.DrainSaves();
        Assert.Contains(CompanionContent.Default.ForScene("daily_click", "new"), line => line.Text == restarted.Session.Speech.SpeechText);
        Assert.Equal(0, restarted.Session.State.TotalPoints);
    });

    [Fact]
    public void StartupWorkAnnouncementSurvivesAttachingTheExternalSpeechOwner() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"), new PetSettings
        { Mode = CompanionMode.Work, AutoSleep = false, CompanionTextEnabled = false });
        Assert.Equal("开始工作啦~", harness.Session.Speech.SpeechText);
        Assert.False(harness.Session.Engine.HasActiveWindow);
        Assert.Equal(0, harness.Session.State.CompanionDays);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingWorkClearsVisibleAndAwaitingSaveAnnouncements(bool companionshipText) => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"), new PetSettings
        { AutoSleep = false, CompanionTextEnabled = companionshipText });
        harness.Session.PrepareManualWorkStart();
        harness.Apply(harness.Settings with { Mode = CompanionMode.Work });
        Assert.True(harness.Session.Speech.HasPendingWork);
        if (!companionshipText) Assert.Equal("开始工作啦~", harness.Session.Speech.SpeechText);

        harness.Apply(harness.Settings with { Mode = CompanionMode.Daily });
        harness.DrainSaves();
        Assert.Null(harness.Session.Speech.SpeechText);
        Assert.False(harness.Session.Speech.HasPendingWork);
        harness.Advance(500);
        Assert.Null(harness.Session.Speech.SpeechText);
    });

    [Fact]
    public void DailyAutomaticPauseKeepsTheMilestonePendingWhileClicksStillRespond() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        using var harness = new SessionHarness(directory.FilePath("companion.json"), new PetSettings { AutoSleep = false });
        harness.Behavior.DoubleClick(); harness.DrainSaves();
        harness.Advance(60000);
        Assert.Equal("milestone.first_day", harness.Session.State.PendingMilestoneId);
        harness.PauseWork(true);
        harness.Behavior.DoubleClick(); harness.DrainSaves();
        Assert.Contains(CompanionContent.Default.ForScene("daily_click", "new"), line => line.Text == harness.Session.Speech.SpeechText);
        Assert.Equal("milestone.first_day", harness.Session.State.PendingMilestoneId);
        Assert.DoesNotContain("milestone.first_day", harness.Session.State.ConsumedMilestoneIds);
    });
}
