using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class WorkModeTests
{
    private static Dictionary<string, AnimationClip> Pack()
    {
        AnimationClip Rest(string name, int duration) => new(name,
            [new("idle.png", 17), new(name + ".png", duration - 34), new("idle.png", 17)]);
        var clips = new Dictionary<string, AnimationClip>
        {
            ["idle"] = new("idle", [new("idle.png", 1000)], true),
            ["blink"] = Rest("blink", 375),
            ["meow"] = Rest("meow", 1400),
            ["wink"] = Rest("wink", 1500),
            ["yawn"] = Rest("yawn", 4400),
            ["firefly"] = Rest("firefly", 10000),
            ["fall_asleep"] = new("fall_asleep", [new("idle.png", 100), new("half.png", 100), new("sleep.png", 100)]),
            ["sleep_loop"] = new("sleep_loop", [new("sleep.png", 100), new("breath.png", 100)], true),
            ["wake"] = new("wake", [new("sleep.png", 100), new("half.png", 100), new("idle.png", 100)]),
            ["rise"] = new("rise", [new("idle.png", 100), new("rise.png", 100)]),
            ["hold"] = new("hold", [new("rise.png", 1000)], true),
            ["settle"] = new("settle", [new("rise.png", 100), new("idle.png", 100)]),
            ["work_enter"] = new("work_enter", [new("idle.png", 200), new("focus.png", 200)]),
            ["work_loop"] = new("work_loop", [new("focus.png", 1250), new("work-blink.png", 375), new("focus.png", 7975)], true),
            ["work_exit"] = new("work_exit", [new("focus.png", 200), new("idle.png", 200)]),
            ["work_props"] = new("work_props", [new("computer.png", 17)], true)
        };
        return clips;
    }

    private static BehaviorController Create(FakeClock clock, int work = 1500000, int rest = 300000,
        CompanionMode mode = CompanionMode.Work) => new(clock, Pack(), new PetSettings
        { Mode = mode, WorkDurationMs = work, BreakDurationMs = rest, AutoSleep = false }, (min, _) => min);
    private static void Advance(FakeClock clock, BehaviorController c, long ms) { clock.Advance(ms); c.Tick(); }
    private static void Finish(FakeClock clock, BehaviorController c) => Advance(clock, c,
        Math.Max(1, c.Player.CurrentClip!.DurationMs - c.Player.ElapsedMs));
    private static BehaviorController Working(FakeClock clock, int work = 1500000, int rest = 300000)
    {
        var c = Create(clock, work, rest); Finish(clock, c); Finish(clock, c);
        Assert.Equal(WorkPhase.Working, c.WorkPhase); return c;
    }
    private static BehaviorController Resting(FakeClock clock, int rest = 300000)
    {
        var c = Working(clock, 100, rest); Advance(clock, c, 100);
        Finish(clock, c); Finish(clock, c); Finish(clock, c);
        Assert.Equal(WorkPhase.Resting, c.WorkPhase); return c;
    }
    private static void FinishWake(FakeClock clock, BehaviorController c)
    {
        for (var i = 0; i < 3 && c.State == PetState.Wake; i++) Finish(clock, c);
        Assert.NotEqual(PetState.Wake, c.State);
    }

    [Fact]
    public void DefaultsRemainDailyWithTwentyFiveAndFiveMinuteDurations()
    {
        var s = new PetSettings(); Assert.Equal(CompanionMode.Daily, s.Mode);
        Assert.Equal(1500000, s.WorkDurationMs); Assert.Equal(300000, s.BreakDurationMs);
        var c = Create(new FakeClock(), mode: CompanionMode.Daily);
        Assert.Equal(WorkPhase.None, c.WorkPhase); Assert.Equal("idle", c.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(28800001, 1)] [InlineData(1, 0)] [InlineData(1, 28800001)]
    public void DurationsRejectOutsideBounds(int work, int rest) =>
        Assert.NotEmpty(new PetSettings { WorkDurationMs = work, BreakDurationMs = rest }.Validate());

    [Theory]
    [InlineData(1)] [InlineData(28800000)]
    public void DurationBoundsAreAccepted(int duration) => Assert.Empty(new PetSettings
        { WorkDurationMs = duration, BreakDurationMs = duration }.Validate());

    [Fact]
    public void MissingOptionalWorkAssetsFallBackToDaily()
    {
        var p = Pack(); p.Remove("work_props");
        var c = new BehaviorController(new FakeClock(), p, new PetSettings { Mode = CompanionMode.Work });
        Assert.False(c.CanWork); Assert.Equal(CompanionMode.Daily, c.Mode); Assert.Equal(WorkPhase.None, c.WorkPhase);
    }

    [Fact]
    public void FirstAnnouncementAndEntryPrecedeTheWorkTimer()
    {
        var clock = new FakeClock(); var c = Create(clock);
        Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase); Assert.Equal("meow", c.Player.CurrentClip!.Name);
        Assert.Equal("开始工作啦~", c.SpeechText); Assert.Equal(1400, c.SpeechDurationMs);
        Finish(clock, c); Assert.Equal(WorkPhase.Entering, c.WorkPhase); Assert.Null(c.SpeechText);
        Assert.Equal(0, c.WorkPropsOpacity); Advance(clock, c, 200); Assert.Equal(.5, c.WorkPropsOpacity, 8);
        Finish(clock, c); Assert.Equal(WorkPhase.Working, c.WorkPhase); Assert.Equal(PetState.Work, c.State);
        Assert.Equal(1500000, c.WorkRemainingMs); Assert.Equal(1, c.WorkPropsOpacity); Assert.Equal(0, c.WorkBodyOffsetY);
    }

    [Fact]
    public void WorkClickOnlySpeaksAndHoverDoesNothing()
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 1500);
        var token = c.Player.Token; var frame = c.Player.CurrentFrame; var remaining = c.WorkRemainingMs;
        c.Hover(); Assert.Null(c.SpeechText); c.Click();
        Assert.Equal("工作呢，别打扰我", c.SpeechText); Assert.Equal(token, c.Player.Token);
        Assert.Same(frame, c.Player.CurrentFrame); Assert.Equal(remaining, c.WorkRemainingMs);
        c.DoubleClick(); Assert.Equal(token, c.Player.Token); Assert.Equal(WorkPhase.Working, c.WorkPhase);
    }

    [Theory]
    [InlineData(1250, 375)] [InlineData(1624, 1)] [InlineData(1625, 0)]
    [InlineData(6700, 375)] [InlineData(7074, 1)] [InlineData(7075, 0)]
    [InlineData(10850, 375)] [InlineData(11225, 0)]
    public void WorkDeadlineWaitsOnlyForCurrentBlink(int duration, int wait)
    {
        var clock = new FakeClock(); var c = Working(clock, duration);
        Advance(clock, c, duration);
        Assert.Equal(wait > 0 ? WorkPhase.WaitingForBlink : WorkPhase.Exiting, c.WorkPhase);
        if (wait > 0) { Advance(clock, c, wait - 1); Assert.Equal(WorkPhase.WaitingForBlink, c.WorkPhase); Advance(clock, c, 1); }
        Assert.Equal(WorkPhase.Exiting, c.WorkPhase); Assert.Equal("work_exit", c.Player.CurrentClip!.Name);
    }

    [Fact]
    public void BreakTimerStartsOnlyWhenFirstSleepLoopBegins()
    {
        var clock = new FakeClock(); var c = Working(clock, 100);
        Advance(clock, c, 100); Finish(clock, c);
        Assert.Equal(WorkPhase.AnnouncingBreak, c.WorkPhase); Assert.Equal("休息一下吧~", c.SpeechText);
        Assert.Equal(1500, c.SpeechDurationMs); Assert.Equal(300000, c.BreakRemainingMs);
        Finish(clock, c); Assert.Equal(WorkPhase.FallingAsleep, c.WorkPhase); Assert.Null(c.SpeechText);
        Advance(clock, c, 299); Assert.Equal(300000, c.BreakRemainingMs);
        Advance(clock, c, 1); Assert.Equal(WorkPhase.Resting, c.WorkPhase); Assert.Equal(300000, c.BreakRemainingMs);
        Advance(clock, c, 1234); Assert.Equal(298766, c.BreakRemainingMs);
    }

    [Fact]
    public void RestClickWakesYawnsAndResleepsWithoutExtendingBreak()
    {
        var clock = new FakeClock(); var c = Resting(clock); Advance(clock, c, 150);
        var deadline = clock.NowMilliseconds + c.BreakRemainingMs;
        c.Hover(); Assert.Equal(WorkPhase.Resting, c.WorkPhase);
        c.Click(); Assert.Equal(WorkPhase.RestWake, c.WorkPhase); FinishWake(clock, c);
        Assert.Equal(WorkPhase.RestYawn, c.WorkPhase); Assert.Equal("yawn", c.Player.CurrentClip!.Name);
        Assert.Equal("和我一起休息", c.SpeechText); Assert.Equal(4400, c.SpeechDurationMs);
        var token = c.Player.Token; c.Click(); c.Hover(); Assert.Equal(token, c.Player.Token);
        Finish(clock, c); Assert.Equal(WorkPhase.FallingAsleep, c.WorkPhase); Assert.Null(c.SpeechText);
        Finish(clock, c); Assert.Equal(WorkPhase.Resting, c.WorkPhase);
        Assert.Equal(deadline, clock.NowMilliseconds + c.BreakRemainingMs);
    }

    [Fact]
    public void BreakExpiresDuringYawnWithoutStartingAnotherFiveMinutes()
    {
        var clock = new FakeClock(); var c = Resting(clock, 1000); c.Click(); FinishWake(clock, c);
        Assert.Equal(WorkPhase.RestYawn, c.WorkPhase); Finish(clock, c);
        Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase); Assert.Equal("开始工作啦~", c.SpeechText);
    }

    [Fact]
    public void NaturalBreakCompletionWakesAndStartsTheNextWorkAnnouncement()
    {
        var clock = new FakeClock(); var c = Resting(clock);
        Advance(clock, c, 300000); Assert.Equal(WorkPhase.WakeForNextWork, c.WorkPhase);
        FinishWake(clock, c); Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase);
        Assert.Equal("开始工作啦~", c.SpeechText); Finish(clock, c); Finish(clock, c);
        Assert.Equal(WorkPhase.Working, c.WorkPhase); Assert.Equal(100, c.WorkRemainingMs);
    }

    [Theory]
    [InlineData("drag")] [InlineData("pause")] [InlineData("suspend")]
    public void WorkFreezePreservesFrameBreathingSpeechAndRemainingTime(string reason)
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 1700); c.Click();
        var token = c.Player.Token; var frame = c.Player.CurrentFrame; var body = c.WorkBodyOffsetY; var remaining = c.WorkRemainingMs;
        if (reason == "drag") c.BeginDrag(); else if (reason == "pause") c.SetAutomaticActionsPaused(true); else c.SetSuspended(true);
        Advance(clock, c, 10000000);
        Assert.Equal(frame, c.Player.CurrentFrame); Assert.Equal(body, c.WorkBodyOffsetY); Assert.Equal(remaining, c.WorkRemainingMs);
        Assert.Equal(0, c.SpeechElapsedMs); Assert.Equal("工作呢，别打扰我", c.SpeechText);
        if (reason == "drag") c.EndDrag(); else if (reason == "pause") c.SetAutomaticActionsPaused(false); else c.SetSuspended(false);
        Assert.Equal(token, c.Player.Token); Assert.False(c.Player.IsPaused); Assert.Equal(remaining, c.WorkRemainingMs);
        Advance(clock, c, 50); Assert.Equal(remaining - 50, c.WorkRemainingMs); Assert.Equal(50, c.SpeechElapsedMs);
    }

    [Fact]
    public void OverlappingPauseReasonsResumeOnlyAfterAllReasonsClear()
    {
        var clock = new FakeClock(); var c = Working(clock); c.BeginDrag(); Advance(clock, c, 100);
        c.SetAutomaticActionsPaused(true); c.EndDrag(); Advance(clock, c, 100); Assert.True(c.Player.IsPaused);
        c.SetSuspended(true); c.SetAutomaticActionsPaused(false); Advance(clock, c, 100); Assert.True(c.Player.IsPaused);
        c.SetSuspended(false); Assert.False(c.Player.IsPaused); Assert.Equal(1500000, c.WorkRemainingMs);
    }

    [Fact]
    public void RestDragAndSuspendPreserveTheOriginalBreakAndSleepingPose()
    {
        var clock = new FakeClock(); var c = Resting(clock); Advance(clock, c, 150);
        var remaining = c.BreakRemainingMs; var token = c.Player.Token;
        c.BeginDrag(); Advance(clock, c, 50000); c.EndDrag();
        Assert.Equal(WorkPhase.Resting, c.WorkPhase); Assert.Equal(token, c.Player.Token); Assert.Equal(remaining, c.BreakRemainingMs);
        c.SetSuspended(true); Advance(clock, c, 900000); c.SetSuspended(false);
        Assert.Equal(WorkPhase.Resting, c.WorkPhase); Assert.Equal(token, c.Player.Token); Assert.Equal(remaining, c.BreakRemainingMs);
    }

    [Fact]
    public void SwitchingBackDuringWorkUsesTheCurrentBreathingPhaseAndReturnsToIdle()
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 2400);
        var body = c.WorkBodyOffsetY; c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Assert.Equal(WorkPhase.Exiting, c.WorkPhase); Assert.Equal(body, c.WorkBodyOffsetY); Assert.Equal(1, c.WorkPropsOpacity);
        Advance(clock, c, 200); Assert.Equal(.5, c.WorkPropsOpacity, 8);
        Finish(clock, c); Assert.Equal(WorkPhase.None, c.WorkPhase); Assert.Equal(PetState.Idle, c.State);
        Assert.Equal(0, c.WorkPropsOpacity); Assert.Equal(0, c.WorkBodyOffsetY); Assert.Equal("idle", c.Player.CurrentClip!.Name);
    }

    [Fact]
    public void SwitchingBackWhileBlinkingDoesNotRestartOrExtendTheWait()
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 1400);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Assert.Equal(WorkPhase.WaitingForBlink, c.WorkPhase); var token = c.Player.Token;
        Advance(clock, c, 100); c.ApplySettings(c.Settings with { Topmost = false }); Assert.Equal(token, c.Player.Token);
        Advance(clock, c, 125); Assert.Equal(WorkPhase.Exiting, c.WorkPhase);
    }

    [Fact]
    public void EnablingDuringAutomaticActionWaitsAndCancellingDoesNotCutTheAction()
    {
        var clock = new FakeClock(); var c = new BehaviorController(clock, Pack(), new PetSettings
        { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 }, (_, max) => max, [new("firefly")]);
        Advance(clock, c, 100); Assert.Equal("firefly", c.Player.CurrentClip!.Name); Advance(clock, c, 500);
        var token = c.Player.Token;
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Work }); Assert.Equal(WorkPhase.WaitingForIdle, c.WorkPhase);
        Assert.Equal(token, c.Player.Token); c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Assert.Equal(WorkPhase.None, c.WorkPhase); Assert.Equal(token, c.Player.Token);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Work }); Finish(clock, c);
        Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase);
    }

    [Fact]
    public void EnablingWhileAsleepWakesBeforeAnnouncingWork()
    {
        var clock = new FakeClock(); var c = new BehaviorController(clock, Pack(), new PetSettings { SleepAfterMs = 1 });
        Advance(clock, c, 1); Finish(clock, c); Advance(clock, c, 150);
        var frame = c.Player.CurrentFrame;
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Work }); Assert.Equal(WorkPhase.WakingForWork, c.WorkPhase);
        Assert.Equal(frame, c.Player.CurrentFrame); FinishWake(clock, c); Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase);
    }

    [Fact]
    public void LeavingDuringRestWakesIntoDailyWithoutAnotherAnnouncement()
    {
        var clock = new FakeClock(); var c = Resting(clock); c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Assert.Equal(WorkPhase.LeavingWake, c.WorkPhase); FinishWake(clock, c);
        Assert.Equal(WorkPhase.None, c.WorkPhase); Assert.Equal(PetState.Idle, c.State); Assert.Null(c.SpeechText);
    }

    [Fact]
    public void ChangingDurationUsesExistingCycleOrigin()
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 1000);
        c.ApplySettings(c.Settings with { WorkDurationMs = 1500 }); Assert.Equal(500, c.WorkRemainingMs);
        Advance(clock, c, 500); Assert.Equal(WorkPhase.WaitingForBlink, c.WorkPhase);
    }

    [Fact]
    public void DailyMeowSpeechMatchesTheClipAndFreezesWithDragging()
    {
        var clock = new FakeClock(); var c = Create(clock, mode: CompanionMode.Daily); c.DoubleClick();
        Assert.Equal("喵", c.SpeechText); Assert.Equal(1400, c.SpeechDurationMs); Advance(clock, c, 500);
        c.BeginDrag(); Advance(clock, c, 10000); Assert.Equal(500, c.SpeechElapsedMs); Assert.Equal("喵", c.SpeechText);
        c.EndDrag(); Advance(clock, c, 899); Assert.Equal("喵", c.SpeechText); Advance(clock, c, 1); Assert.Null(c.SpeechText);
    }

    [Fact]
    public void SwitchingBackToWorkCancelsAManualBlinkWaitWithoutResettingTheCycle()
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 1400);
        var token = c.Player.Token;
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily }); Advance(clock, c, 100);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Work });
        Assert.Equal(WorkPhase.Working, c.WorkPhase); Assert.Equal(token, c.Player.Token);
        Assert.Equal(1498500, c.WorkRemainingMs); Advance(clock, c, 125); Assert.Equal(WorkPhase.Working, c.WorkPhase);
    }

    [Fact]
    public void SwitchingBackToWorkDuringLeaveWakeRestartsWorkAtTheRestingEndpoint()
    {
        var clock = new FakeClock(); var c = Resting(clock);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Work }); FinishWake(clock, c);
        Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase); Assert.Equal("开始工作啦~", c.SpeechText);
    }

    [Fact]
    public void RestClickDuringFallingAsleepReversesTheDisplayedPoseThenYawns()
    {
        var clock = new FakeClock(); var c = Working(clock, 100);
        Advance(clock, c, 100); Finish(clock, c); Finish(clock, c); Advance(clock, c, 150);
        var frame = c.Player.CurrentFrame; c.Click();
        Assert.Equal(WorkPhase.RestWake, c.WorkPhase); Assert.Equal(frame, c.Player.CurrentFrame);
        FinishWake(clock, c); Assert.Equal(WorkPhase.RestYawn, c.WorkPhase);
        Finish(clock, c); Finish(clock, c); Assert.Equal(WorkPhase.Resting, c.WorkPhase);
        Assert.Equal(300000, c.BreakRemainingMs);
    }

    [Fact]
    public void StaleCompletionCannotAdvanceWorkAndExitDuringEntryFinishesBothTransitions()
    {
        var clock = new FakeClock(); var c = Create(clock); var obsolete = c.Player.Token;
        Finish(clock, c); c.HandleAnimationCompleted(new AnimationCompleted(obsolete, "meow"));
        Assert.Equal(WorkPhase.Entering, c.WorkPhase); Advance(clock, c, 150);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily }); Assert.Equal(WorkPhase.Entering, c.WorkPhase);
        Finish(clock, c); Assert.Equal(WorkPhase.Exiting, c.WorkPhase); Finish(clock, c);
        Assert.Equal(WorkPhase.None, c.WorkPhase);
    }

    [Fact]
    public void PausingAnEntryFreezesOpacityAndResumesTheExactRemainingTransition()
    {
        var clock = new FakeClock(); var c = Create(clock); Finish(clock, c); Advance(clock, c, 150);
        var opacity = c.WorkPropsOpacity; c.SetAutomaticActionsPaused(true); Advance(clock, c, 30000);
        Assert.Equal(opacity, c.WorkPropsOpacity); Assert.Equal(WorkPhase.Entering, c.WorkPhase);
        c.SetAutomaticActionsPaused(false); Advance(clock, c, 249); Assert.Equal(WorkPhase.Entering, c.WorkPhase);
        Advance(clock, c, 1); Assert.Equal(WorkPhase.Working, c.WorkPhase); Assert.Equal(1500000, c.WorkRemainingMs);
    }

    [Fact]
    public void ExplicitReturnToDailyCanFinishWhileAutomaticActionsRemainPaused()
    {
        var clock = new FakeClock(); var c = Working(clock); Advance(clock, c, 2400);
        c.SetAutomaticActionsPaused(true); Advance(clock, c, 10000);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Assert.Equal(WorkPhase.Exiting, c.WorkPhase); Assert.False(c.Player.IsPaused);
        Finish(clock, c); Assert.Equal(WorkPhase.None, c.WorkPhase); Assert.True(c.AutomaticActionsPaused);
        c.DoubleClick(); Assert.Equal("meow", c.Player.CurrentClip!.Name);
    }
}
