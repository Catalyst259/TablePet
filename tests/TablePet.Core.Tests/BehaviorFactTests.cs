using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class BehaviorFactTests
{
    private static Dictionary<string, AnimationClip> Pack()
    {
        AnimationClip Rest(string name, int duration) => new(name,
            [new("idle.png", 17), new(name + ".png", duration - 34), new("idle.png", 17)]);
        return new()
        {
            ["idle"] = new("idle", [new("idle.png", 1000)], true),
            ["blink"] = Rest("blink", 375), ["meow"] = Rest("meow", 1400),
            ["wink"] = Rest("wink", 1500), ["yawn"] = Rest("yawn", 4400),
            ["firefly"] = Rest("firefly", 10000),
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

    private static BehaviorController Create(FakeClock clock, List<BehaviorFact> facts,
        PetSettings? settings = null, bool firefly = false, bool externalSpeech = false) => new(clock, Pack(),
        settings ?? new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 },
        (min, max) => firefly ? max : min, firefly ? [new("firefly")] : null,
        facts.Add, externalSpeech);

    private static void Advance(FakeClock clock, BehaviorController controller, long ms)
    { clock.Advance(ms); controller.Tick(); }

    private static void Finish(FakeClock clock, BehaviorController controller) =>
        Advance(clock, controller, Math.Max(1, controller.Player.CurrentClip!.DurationMs - controller.Player.ElapsedMs));

    private static void FinishWake(FakeClock clock, BehaviorController controller)
    {
        for (var i = 0; i < 3 && controller.State == PetState.Wake; i++) Finish(clock, controller);
        Assert.NotEqual(PetState.Wake, controller.State);
    }

    private static BehaviorController Working(FakeClock clock, List<BehaviorFact> facts, int work = 5000, int rest = 5000)
    {
        var controller = Create(clock, facts, new PetSettings
        { Mode = CompanionMode.Work, AutoSleep = false, WorkDurationMs = work, BreakDurationMs = rest });
        Finish(clock, controller); Finish(clock, controller);
        Assert.Equal(WorkPhase.Working, controller.WorkPhase);
        return controller;
    }

    private static BehaviorController Resting(FakeClock clock, List<BehaviorFact> facts, int rest = 5000)
    {
        var controller = Working(clock, facts, 100, rest);
        Advance(clock, controller, 100); Finish(clock, controller); Finish(clock, controller); Finish(clock, controller);
        Assert.Equal(WorkPhase.Resting, controller.WorkPhase);
        return controller;
    }

    [Fact]
    public void DailyClickCommitsOnceAndSpeechLinksToItsFact()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Create(clock, facts);
        c.Hover(); Assert.Empty(facts); c.DoubleClick(); c.Click(); c.DoubleClick();
        var interaction = Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        Assert.Equal(BehaviorInteractionSource.Daily, interaction.Source);
        Assert.False(interaction.FromNaturalSleep);
        var cue = Assert.Single(facts, f => f.Kind == BehaviorFactKind.SpeechCue);
        Assert.Equal(interaction.Id, cue.RelatedId);
        Assert.Equal(BehaviorSpeechScene.DailyClick, cue.Scene);
        Assert.Equal("喵", c.SpeechText);
        Finish(clock, c); c.Click();
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
    }

    [Fact]
    public void AClickThatActuallyWakesNaturalSleepKeepsItsProvenanceThroughUpgrades()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>();
        var c = Create(clock, facts, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, c, 100); Finish(clock, c); Assert.Equal(PetState.Sleep, c.State);
        c.Click(); c.DoubleClick(); c.Hover();
        Assert.DoesNotContain(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        FinishWake(clock, c);
        var interaction = Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        Assert.True(interaction.FromNaturalSleep);
        Assert.Equal("meow", c.Player.CurrentClip!.Name);
    }

    [Fact]
    public void ClickDuringAHoverWakeIsNotReportedAsTheCauseOfWaking()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>();
        var c = Create(clock, facts, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, c, 100); Finish(clock, c); c.Hover(); c.Click(); FinishWake(clock, c);
        Assert.False(Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted).FromNaturalSleep);
    }

    [Theory]
    [InlineData("drag")]
    [InlineData("suspend")]
    [InlineData("work")]
    public void CancelledDelayedClickNeverCommits(string cancellation)
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Create(clock, facts, firefly: true);
        Advance(clock, c, 100); c.Click();
        Assert.DoesNotContain(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        if (cancellation == "drag") { c.BeginDrag(); c.EndDrag(); }
        else if (cancellation == "suspend") { c.SetSuspended(true); c.SetSuspended(false); }
        else c.ApplySettings(c.Settings with { Mode = CompanionMode.Work });
        Finish(clock, c);
        Assert.DoesNotContain(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
    }

    [Fact]
    public void CompletedFireflyAndQueuedInteractionAreDistinctFacts()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Create(clock, facts, firefly: true);
        Advance(clock, c, 100); c.Click(); c.DoubleClick(); c.Hover(); Finish(clock, c);
        var start = Assert.Single(facts, f => f.Kind == BehaviorFactKind.IdleActionStarted);
        var end = Assert.Single(facts, f => f.Kind == BehaviorFactKind.IdleActionCompleted);
        Assert.Equal(start.Id, end.RelatedId);
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        Assert.Equal(facts.Count, facts.Select(f => f.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DragOrSuspensionInvalidatesFireflyOnlyOnce(bool suspend)
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Create(clock, facts, firefly: true);
        Advance(clock, c, 100);
        if (suspend) { c.SetSuspended(true); c.SetSuspended(true); c.SetSuspended(false); }
        else { c.BeginDrag(); c.BeginDrag(); c.EndDrag(); }
        Finish(clock, c);
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.IdleActionInvalidated);
        Assert.DoesNotContain(facts, f => f.Kind == BehaviorFactKind.IdleActionCompleted);
    }

    [Fact]
    public void PausingFutureDailyActionsDoesNotInvalidateCurrentFirefly()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Create(clock, facts, firefly: true);
        Advance(clock, c, 100); c.SetAutomaticActionsPaused(true); Finish(clock, c);
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.IdleActionCompleted);
        Assert.DoesNotContain(facts, f => f.Kind == BehaviorFactKind.IdleActionInvalidated);
    }

    [Fact]
    public void NaturalWorkDeadlineIsRecordedBeforeBlinkAndExitAndCannotBecomeCancellation()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Working(clock, facts, 1250);
        Advance(clock, c, 1250); Assert.Equal(WorkPhase.WaitingForBlink, c.WorkPhase);
        var ended = Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundEnded);
        Assert.Equal(WorkEndReason.DeadlineReached, ended.Reason);
        Assert.Equal(1250, ended.ElapsedMs);
        Assert.Equal(Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundStarted).Id, ended.RelatedId);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Advance(clock, c, 375); Finish(clock, c);
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundEnded);
        Assert.DoesNotContain(facts, f => f.Scene == BehaviorSpeechScene.WorkBreak);
    }

    [Fact]
    public void ACancelledWorkRoundCannotBecomeCompletedAfterQuicklyReturningToWork()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Working(clock, facts);
        Advance(clock, c, 1300);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily });
        Assert.Equal(WorkPhase.WaitingForBlink, c.WorkPhase);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Work });
        Assert.Equal(WorkPhase.Working, c.WorkPhase);
        Advance(clock, c, 3700);
        var ended = facts.Where(f => f.Kind == BehaviorFactKind.WorkRoundEnded).ToArray();
        Assert.Equal(2, ended.Length);
        Assert.Equal(WorkEndReason.UserCancelled, ended[0].Reason);
        Assert.Equal(WorkEndReason.DeadlineReached, ended[1].Reason);
        Assert.NotEqual(ended[0].RelatedId, ended[1].RelatedId);
        Assert.Equal(3700, ended[1].ElapsedMs);
    }

    [Fact]
    public void ChangingDurationKeepsTheRoundIdentityAndRequiresAnActualDeadline()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Working(clock, facts);
        Advance(clock, c, 1000); c.ApplySettings(c.Settings with { WorkDurationMs = 6000 });
        Assert.DoesNotContain(facts, f => f.Kind == BehaviorFactKind.WorkRoundEnded);
        c.ApplySettings(c.Settings with { WorkDurationMs = 1000 });
        var ended = Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundEnded);
        Assert.Equal(WorkEndReason.DeadlineReached, ended.Reason);
        Assert.True(ended.DurationChanged); Assert.Equal(5000, ended.PlannedDurationMs);
        Assert.Equal(1000, ended.ElapsedMs);
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundStarted);
    }

    [Fact]
    public void RawWorkElapsedExcludesPausedHiddenAndDraggedTime()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Working(clock, facts);
        Advance(clock, c, 1000);
        c.SetAutomaticActionsPaused(true); Advance(clock, c, 10000); c.SetAutomaticActionsPaused(false);
        c.SetSuspended(true); Advance(clock, c, 10000); c.SetSuspended(false);
        c.BeginDrag(); Advance(clock, c, 10000); c.EndDrag();
        Advance(clock, c, 4000);
        Assert.Equal(5000, Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundEnded).ElapsedMs);
    }

    [Fact]
    public void BusyClickCommitsWorkingSourceWithoutRestartingTheAnimation()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Working(clock, facts);
        var token = c.Player.Token; c.Hover(); c.Click();
        var interaction = Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        Assert.Equal(BehaviorInteractionSource.Working, interaction.Source);
        Assert.Equal(interaction.Id, Assert.Single(facts, f => f.Scene == BehaviorSpeechScene.WorkingClick).RelatedId);
        Assert.Equal(token, c.Player.Token);
        c.SetAutomaticActionsPaused(true); c.Click();
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
    }

    [Fact]
    public void RestClickCommitsBeforeWakeButSpeaksOnlyAtYawnAndDoesNotRestartBreak()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Resting(clock, facts);
        c.Click(); c.DoubleClick();
        var interaction = Assert.Single(facts, f => f.Kind == BehaviorFactKind.InteractionCommitted);
        Assert.Equal(BehaviorInteractionSource.Rest, interaction.Source);
        Assert.DoesNotContain(facts, f => f.Scene == BehaviorSpeechScene.RestClick);
        FinishWake(clock, c);
        Assert.Equal(interaction.Id, Assert.Single(facts, f => f.Scene == BehaviorSpeechScene.RestClick).RelatedId);
        Finish(clock, c); Finish(clock, c);
        Assert.Single(facts, f => f.Kind == BehaviorFactKind.BreakStarted);
    }

    [Fact]
    public void BreakDeadlineIsEmittedOnceEvenWhenItExpiresDuringTheYawnCompletion()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Resting(clock, facts, 100);
        c.Click(); FinishWake(clock, c); Finish(clock, c);
        var started = Assert.Single(facts, f => f.Kind == BehaviorFactKind.BreakStarted);
        var ended = Assert.Single(facts, f => f.Kind == BehaviorFactKind.BreakEnded);
        Assert.Equal(started.Id, ended.RelatedId);
        Assert.Equal(WorkEndReason.DeadlineReached, ended.Reason);
        Assert.Equal(WorkPhase.AnnouncingWork, c.WorkPhase);
    }

    [Fact]
    public void CancellingBreakAndShuttingDownDoNotProduceNaturalCompletion()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Resting(clock, facts);
        c.ApplySettings(c.Settings with { Mode = CompanionMode.Daily }); c.NotifyShutdown(); c.NotifyShutdown();
        Assert.Equal(WorkEndReason.UserCancelled, Assert.Single(facts, f => f.Kind == BehaviorFactKind.BreakEnded).Reason);
        facts.Clear(); clock = new FakeClock(); c = Working(clock, facts); c.NotifyShutdown(); c.NotifyShutdown();
        Assert.Equal(WorkEndReason.Shutdown, Assert.Single(facts, f => f.Kind == BehaviorFactKind.WorkRoundEnded).Reason);
    }

    [Fact]
    public void ExternalSpeechReceivesInitialWorkCueAndSuppressesTheLegacyBubble()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>();
        var c = Create(clock, facts, new PetSettings { Mode = CompanionMode.Work }, externalSpeech: true);
        Assert.Equal(BehaviorSpeechScene.WorkStart, Assert.Single(facts).Scene);
        Assert.Null(c.SpeechText);
        Finish(clock, c); Finish(clock, c); c.Click();
        Assert.Null(c.SpeechText);
        Assert.Contains(facts, f => f.Scene == BehaviorSpeechScene.WorkingClick && f.DefaultText == "工作呢，别打扰我");
    }

    [Fact]
    public void HidingDuringRestWakeDoesNotEmitAStaleExternalRestCue()
    {
        var clock = new FakeClock(); var facts = new List<BehaviorFact>(); var c = Resting(clock, facts);
        c.ExternalSpeechEnabled = true; c.Click(); c.SetSuspended(true); c.SetSuspended(false); FinishWake(clock, c);
        Assert.DoesNotContain(facts, f => f.Scene == BehaviorSpeechScene.RestClick);
    }
}
