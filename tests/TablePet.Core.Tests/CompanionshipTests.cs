using System.Text.Json;
using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class CompanionshipTests
{
    [Fact]
    public void AutostartCannotGrowAndRepeatedActivationAddsNoDirectPoints()
    {
        var (time, engine) = Create();
        Run(time, engine, 3_600_000);
        Assert.Empty(engine.State.Days);
        Assert.Empty(engine.State.Memories);
        for (var i = 0; i < 100; i++) engine.Activate();
        Assert.Equal(0, engine.State.TotalPoints);
        Run(time, engine, 60_000);
        Assert.Equal(3, engine.State.TotalPoints);
        Assert.Equal(1, engine.State.CompanionDays);
        Assert.Single(engine.State.Memories, memory => memory.Id == "first_day");
    }

    [Theory]
    [InlineData(59_000, 0, 0)]
    [InlineData(60_000, 3, 1)]
    [InlineData(300_000, 4, 1)]
    [InlineData(600_000, 5, 1)]
    [InlineData(1_200_000, 6, 1)]
    [InlineData(3_600_000, 6, 1)]
    public void VisibleTimeHasDocumentedThresholdsAndDailyCap(long duration, int points, int days)
    {
        var (time, engine) = Create();
        engine.Activate();
        Run(time, engine, duration);
        Assert.Equal(points, engine.State.TotalPoints);
        Assert.Equal(days, engine.State.CompanionDays);
        Assert.InRange(engine.State.Days.Values.Single().QualifiedPresenceMs, 0, 1_200_000);
    }

    [Fact]
    public void ShortSessionsAccumulateButReopeningDoesNotRestoreAnActiveWindow()
    {
        var (time, engine) = Create();
        engine.Activate();
        Run(time, engine, 30_000);
        var saved = JsonSerializer.Deserialize<CompanionState>(JsonSerializer.Serialize(engine.State))!;
        Assert.Empty(saved.Validate());
        engine = new CompanionshipEngine(saved, time, time);
        Run(time, engine, 60_000);
        Assert.Equal(30_000, saved.Days.Values.Single().QualifiedPresenceMs);
        Assert.False(engine.HasActiveWindow);
        engine.Activate();
        Run(time, engine, 30_000);
        Assert.Equal(3, saved.TotalPoints);
        Assert.Equal(1, saved.CompanionDays);
    }

    [Theory]
    [InlineData(60_000, 4, 14, 34)]
    [InlineData(300_000, 3, 10, 25)]
    [InlineData(600_000, 3, 8, 21)]
    [InlineData(1_200_000, 3, 8, 21)]
    public void AllFourPublishedGrowthPathsReachTheirStages(long dailyTime, int familiar, int close, int inSync)
    {
        var (time, engine) = Create();
        var reached = new Dictionary<string, int>();
        for (var day = 1; day <= inSync; day++)
        {
            engine.Activate();
            Run(time, engine, dailyTime);
            foreach (var memory in engine.State.Memories.Where(memory => memory.Id.StartsWith("stage_", StringComparison.Ordinal)))
                reached.TryAdd(memory.Id, day);
            time.ShiftWall(TimeSpan.FromDays(1));
            engine.Advance();
        }
        Assert.Equal(familiar, reached["stage_familiar"]);
        Assert.Equal(close, reached["stage_close"]);
        Assert.Equal(inSync, reached["stage_in_sync"]);
        Assert.Equal("in_sync", engine.State.Stage);
        Assert.Empty(engine.State.Validate());
    }

    [Fact]
    public void NonconsecutiveDaysDoNotDecayOrEarnOfflineTime()
    {
        var (time, engine) = Create();
        for (var visit = 0; visit < 4; visit++)
        {
            engine.Activate();
            Run(time, engine, 60_000);
            time.ShiftWall(TimeSpan.FromDays(3));
            engine.Advance();
        }
        Assert.Equal(12, engine.State.TotalPoints);
        Assert.Equal(4, engine.State.CompanionDays);
        Assert.Equal("familiar", engine.State.Stage);
    }

    [Fact]
    public void HiddenLockedAndDraggedTimeDoesNotCountOrExtendTheWindow()
    {
        var (time, engine) = Create();
        engine.Activate();
        Run(time, engine, 30_000);
        foreach (var context in new[]
        {
            new CompanionPresenceContext { IsVisible = false },
            new CompanionPresenceContext { IsSessionAvailable = false },
            new CompanionPresenceContext { IsDragging = true }
        })
        {
            engine.SetContext(context);
            Run(time, engine, 600_000);
        }
        engine.SetContext(new());
        Run(time, engine, 60_000);
        Assert.False(engine.HasActiveWindow);
        Assert.Equal(30_000, engine.State.Days.Values.Single().QualifiedPresenceMs);
        engine.Activate();
        Run(time, engine, 30_000);
        Assert.Equal(3, engine.State.TotalPoints);
    }

    [Fact]
    public void WindowCarriesAcrossMidnightAndStopsAtItsOriginalDeadline()
    {
        var (time, engine) = Create(new DateTimeOffset(2026, 11, 10, 23, 35, 0, TimeSpan.Zero));
        engine.Activate();
        Run(time, engine, 2_400_000);
        Assert.Equal(1_200_000, engine.State.Days["2026-11-10"].QualifiedPresenceMs);
        Assert.Equal(300_000, engine.State.Days["2026-11-11"].QualifiedPresenceMs);
        Assert.Equal(10, engine.State.TotalPoints);
        Assert.Equal(2, engine.State.CompanionDays);
        Assert.False(engine.HasActiveWindow);
    }

    [Fact]
    public void AShortIntervalCrossingMidnightIsSplitWithoutDoubleCounting()
    {
        var (time, engine) = Create(new DateTimeOffset(2026, 11, 10, 23, 59, 59, TimeSpan.Zero).AddMilliseconds(500));
        engine.Activate();
        Run(time, engine, 1_000);
        Assert.Equal(500, engine.State.Days["2026-11-10"].QualifiedPresenceMs);
        Assert.Equal(500, engine.State.Days["2026-11-11"].QualifiedPresenceMs);
        Assert.Null(engine.State.Days["2026-11-11"].ReturnEligible);
    }

    [Fact]
    public void DateTravelReusesBudgetsAndNeverLocksOutTheCorrectDate()
    {
        var (time, engine) = Create();
        engine.Activate();
        Run(time, engine, 60_000);
        time.ShiftWall(TimeSpan.FromDays(100));
        engine.Advance();
        Assert.Single(engine.State.Days);
        engine.Activate();
        Run(time, engine, 60_000);
        time.ShiftWall(TimeSpan.FromDays(-100));
        engine.Advance();
        engine.Activate();
        Run(time, engine, 240_000);
        Assert.Equal(4, engine.State.Days["2026-11-10"].PointsGranted);
        Assert.Equal(7, engine.State.TotalPoints);
        Assert.Equal(2, engine.State.CompanionDays);
        Assert.Equal(2, engine.State.Days.Count);
    }

    [Fact]
    public void SuspiciousSamplingAndCalendarChangesDiscardTheirInterval()
    {
        var (time, engine) = Create();
        engine.Activate();
        time.Step(60_000);
        engine.Advance();
        Assert.Equal(0, engine.State.TotalPoints);
        time.Step(1_000);
        time.ShiftWall(TimeSpan.FromHours(2));
        engine.Advance();
        Assert.Equal(0, engine.State.Days.Values.Single().QualifiedPresenceMs);
        time.Step(1_000);
        time.Zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-eight", TimeSpan.FromHours(8), "test", "test");
        engine.Advance();
        Assert.Equal(0, engine.State.TotalPoints);
        Run(time, engine, 60_000);
        Assert.Equal(3, engine.State.TotalPoints);
    }

    [Fact]
    public void PausedWorkStillCountsCompanionshipButCannotEarnWorkMemory()
    {
        var (time, engine) = Create();
        engine.Activate();
        engine.WorkStarted(1);
        engine.SetContext(new() { IsWorkPaused = true });
        Run(time, engine, 1_200_000);
        Assert.Equal(6, engine.State.TotalPoints);
        Assert.Equal(0, engine.CurrentWorkQualifiedMs);
        engine.WorkEnded(1, true);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_work_completed");
    }

    [Fact]
    public void DefaultWorkAndBreakCanFinishAfterTheWindowExpires()
    {
        var (time, engine) = Create();
        engine.Activate();
        Run(time, engine, 2_000); // Work-entry animation.
        engine.WorkStarted(1);
        Run(time, engine, 1_500_000);
        engine.WorkEnded(1, true);
        Run(time, engine, 3_000); // Work-exit and rest-entry animations.
        engine.BreakStarted(1);
        Run(time, engine, 300_000);
        Assert.False(engine.HasActiveWindow);
        engine.BreakEnded(1, true);
        Assert.Contains(engine.State.Memories, memory => memory.Id == "first_work_completed");
        Assert.Contains(engine.State.Memories, memory => memory.Id == "first_break_completed");
        Assert.Equal(6, engine.State.TotalPoints);
    }

    [Fact]
    public void CancellingQualifiedWorkDoesNotQualifyItsBreak()
    {
        var (time, engine) = Create();
        engine.Activate();
        engine.WorkStarted(1);
        Run(time, engine, 600_000);
        engine.WorkEnded(1, false);
        engine.BreakStarted(1);
        Run(time, engine, 60_000);
        engine.BreakEnded(1, true);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id is "first_work_completed" or "first_break_completed");
    }

    [Fact]
    public void ACompletedWorkFactSurvivesALaterCancellationButBreakMustMatchItsRound()
    {
        var (time, engine) = Create();
        engine.Activate();
        engine.WorkStarted(1);
        Run(time, engine, 600_000);
        engine.WorkEnded(1, true);
        engine.WorkEnded(1, false);
        engine.BreakStarted(2);
        Run(time, engine, 60_000);
        engine.BreakEnded(2, true);
        Assert.Single(engine.State.Memories, memory => memory.Id == "first_work_completed");
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_break_completed");
    }

    [Fact]
    public void MillisecondWorkLoopsHaveNoRewardOrMemoryShortcut()
    {
        var (time, engine) = Create();
        engine.Activate();
        for (var i = 1; i <= 1_000; i++)
        {
            engine.WorkStarted(i); time.Step(1); engine.WorkEnded(i, true);
            engine.BreakStarted(i); time.Step(1); engine.BreakEnded(i, true);
        }
        Assert.Equal(0, engine.State.TotalPoints);
        Assert.Empty(engine.State.Memories);
    }

    [Fact]
    public void FireflyMustBeWitnessedFromStartToFinish()
    {
        var (time, engine) = Create();
        engine.FireflyStarted(1);
        engine.Activate();
        Run(time, engine, 5_000);
        engine.FireflyCompleted(1);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_firefly_visit");
        engine.FireflyStarted(2);
        engine.SetContext(new() { IsVisible = false });
        engine.SetContext(new());
        engine.FireflyCompleted(2);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_firefly_visit");
        engine.FireflyStarted(3);
        Run(time, engine, 5_000);
        engine.FireflyCompleted(3, false);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_firefly_visit");
        engine.FireflyStarted(4);
        Run(time, engine, 5_000);
        engine.FireflyCompleted(4);
        Assert.Single(engine.State.Memories, memory => memory.Id == "first_firefly_visit");
    }

    [Fact]
    public void FireflyCrossingTheAuthorizationDeadlineDoesNotUnlock()
    {
        var (time, engine) = Create();
        engine.Activate();
        Run(time, engine, 1_799_000);
        engine.FireflyStarted(1);
        Run(time, engine, 2_000);
        engine.FireflyCompleted(1);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_firefly_visit");
    }

    [Fact]
    public void ReturnUsesThePreviousActiveDateAndSurvivesFurtherClicksBeforeOneMinute()
    {
        var (time, engine) = Create();
        engine.Activate();
        Assert.False(engine.State.Days["2026-11-10"].ReturnEligible);
        Run(time, engine, 60_000);
        time.ShiftWall(TimeSpan.FromDays(7));
        engine.Advance();
        engine.Activate();
        Assert.True(engine.State.Days["2026-11-17"].ReturnEligible);
        engine.Activate();
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_return");
        Run(time, engine, 60_000);
        Assert.Single(engine.State.Memories, memory => memory.Id == "first_return");
        Assert.Equal(6, engine.State.TotalPoints);
    }

    [Fact]
    public void AnAlreadyQualifiedDateCanRecognizeReturnAtItsFirstActiveUse()
    {
        var (time, _) = Create();
        var state = new CompanionState { LastActiveLocalDate = "2026-11-03" };
        state.Days.Add("2026-11-10", new() { QualifiedPresenceMs = 60_000, PointsGranted = 3, CompanionDayGranted = true });
        var engine = new CompanionshipEngine(state, time, time);
        engine.Activate();
        Assert.True(state.Days["2026-11-10"].ReturnEligible);
        Assert.Contains(state.Memories, memory => memory.Id == "first_return");
    }

    [Fact]
    public void TurningRecordingOffDoesNotMisinterpretUntrackedUseAsAbsence()
    {
        var (time, engine) = Create();
        engine.Activate(); Run(time, engine, 60_000);
        engine.SetEnabled(false);
        Assert.True(engine.State.ReturnBaselineRequired);
        time.ShiftWall(TimeSpan.FromDays(8));
        engine.Activate();
        Assert.Equal("2026-11-10", engine.State.LastActiveLocalDate);
        engine.SetEnabled(true);
        Assert.False(engine.HasActiveWindow);
        engine.Activate(); Run(time, engine, 60_000);
        Assert.False(engine.State.ReturnBaselineRequired);
        Assert.False(engine.State.Days["2026-11-18"].ReturnEligible);
        Assert.DoesNotContain(engine.State.Memories, memory => memory.Id == "first_return");
        time.ShiftWall(TimeSpan.FromDays(7));
        engine.Advance(); engine.Activate(); Run(time, engine, 60_000);
        Assert.Contains(engine.State.Memories, memory => memory.Id == "first_return");
    }

    [Fact]
    public void ReadingMemoriesOrDisablingTextConsumesMilestonesWithoutRemovingGrowth()
    {
        var (time, engine) = Create();
        engine.Activate(); Run(time, engine, 60_000);
        Assert.Equal("milestone.first_day", engine.State.PendingMilestoneId);
        engine.MarkMemoriesRead(["first_day"]);
        Assert.Null(engine.State.PendingMilestoneId);
        Assert.True(engine.State.Memories.Single().IsRead);
        Assert.Contains("milestone.first_day", engine.State.ConsumedMilestoneIds);
        engine.SetTextEnabled(false);
        for (var day = 0; day < 3; day++)
        {
            time.ShiftWall(TimeSpan.FromDays(1)); engine.Advance(); engine.Activate(); Run(time, engine, 60_000);
        }
        engine.SetTextEnabled(true);
        Assert.Equal("familiar", engine.State.Stage);
        Assert.Null(engine.State.PendingMilestoneId);
        Assert.Contains("milestone.familiar", engine.State.ConsumedMilestoneIds);
    }

    [Fact]
    public void AllTwelveMemoriesCanBeEarnedOnceAndSequenceIsIndependentOfWallTime()
    {
        var (time, engine) = Create();
        for (var day = 0; day < 60; day++)
        {
            engine.Activate(); Run(time, engine, 60_000);
            if (day < 59) { time.ShiftWall(TimeSpan.FromDays(1)); engine.Advance(); }
        }
        engine.WorkStarted(1); Run(time, engine, 600_000); engine.WorkEnded(1, true);
        engine.BreakStarted(1); Run(time, engine, 60_000); engine.BreakEnded(1, true);
        engine.FireflyStarted(1); Run(time, engine, 5_000); engine.FireflyCompleted(1);
        engine.GentleWakeCommitted(); engine.GentleWakeCommitted();
        time.ShiftWall(TimeSpan.FromDays(7)); engine.Advance(); engine.Activate(); Run(time, engine, 60_000);
        Assert.Equal(12, engine.State.Memories.Count);
        Assert.Equal(CompanionshipRules.MemoryIds.Order(), engine.State.Memories.Select(memory => memory.Id).Order());
        Assert.Equal(Enumerable.Range(1, 12).Select(value => (long)value), engine.State.Memories.Select(memory => memory.Sequence));
        Assert.Empty(engine.State.Validate());
    }

    [Fact]
    public void SnapshotsAreIndependentAndCorruptAccountingIsRejected()
    {
        var (time, engine) = Create();
        var critical = new List<bool>();
        engine.StateChanged += critical.Add;
        engine.Activate(); Run(time, engine, 60_000);
        Assert.True(critical[0]); Assert.True(critical[^1]); Assert.Contains(false, critical);
        var clone = engine.State.DeepClone();
        var revision = engine.State.StateRevision;
        clone.Days["2026-11-10"].PointsGranted = 6;
        clone.Memories[0].IsRead = true;
        Assert.NotEmpty(clone.Validate());
        Assert.Equal(3, engine.State.TotalPoints);
        Assert.False(engine.State.Memories[0].IsRead);
        Assert.Equal(revision, engine.State.StateRevision);
        Assert.Throws<ArgumentException>(() => new CompanionshipEngine(clone, time, time));
    }

    [Fact]
    public void AHistoricalStageIsNeverDowngradedAndProgressUsesTheSlowerRequirement()
    {
        var state = new CompanionState();
        state.Memories.Add(new CompanionMemory
        {
            Id = "stage_in_sync", LocalDate = "2026-11-10", UnlockedAtUtc = DateTimeOffset.UtcNow, Sequence = 1
        });
        Assert.Equal("in_sync", state.Stage);
        Assert.Equal(1, CompanionshipRules.ProgressToNextStage(state));
        state.Memories.Clear();
        state.Days.Add("2026-11-10", new() { QualifiedPresenceMs = 1_200_000, PointsGranted = 6, CompanionDayGranted = true });
        Assert.Equal(1d / 3, CompanionshipRules.ProgressToNextStage(state), 8);
    }

    private static (TestTime Time, CompanionshipEngine Engine) Create(DateTimeOffset? utc = null)
    {
        var time = new TestTime(utc ?? new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        return (time, new CompanionshipEngine(new(), time, time));
    }

    private static void Run(TestTime time, CompanionshipEngine engine, long milliseconds)
    {
        while (milliseconds > 0)
        {
            var step = Math.Min(milliseconds, 1_000);
            time.Step(step); engine.Advance(); milliseconds -= step;
        }
    }

    private sealed class TestTime(DateTimeOffset utc) : IClock, ICalendarClock
    {
        public long NowMilliseconds { get; private set; }
        public DateTimeOffset Utc { get; private set; } = utc;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public CompanionCalendarTime GetCurrent() => new(Utc, Zone);
        public void Step(long milliseconds) { NowMilliseconds += milliseconds; Utc = Utc.AddMilliseconds(milliseconds); }
        public void ShiftWall(TimeSpan difference) => Utc = Utc.Add(difference);
    }
}
