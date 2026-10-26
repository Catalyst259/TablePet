using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class FakeClock : IClock
{
    public long NowMilliseconds { get; set; }
    public void Advance(long milliseconds) => NowMilliseconds += milliseconds;
}

public sealed class BehaviorTests
{
    private static Dictionary<string, AnimationClip> Clips() => new()
    {
        ["idle"] = Clip("idle", 1, 1000, true),
        ["blink"] = Clip("blink", 3, 30),
        ["fall_asleep"] = Clip("fall_asleep", 3, 100),
        ["sleep_loop"] = Clip("sleep_loop", 4, 400, true),
        ["wake"] = Clip("wake", 3, 100),
        ["rise"] = Clip("rise", 3, 100),
        ["hold"] = Clip("hold", 1, 1000),
        ["settle"] = Clip("settle", 3, 100)
    };

    private static AnimationClip Clip(string name, int frames, int duration, bool loop = false) =>
        new(name, Enumerable.Range(0, frames).Select(i => new AnimationFrame($"{name}-{i}.png", duration)).ToArray(), loop);

    private static BehaviorController Create(FakeClock clock, PetSettings? settings = null) =>
        new(clock, Clips(), settings, (min, _) => min);

    private static void Advance(FakeClock clock, BehaviorController controller, long ms)
    {
        clock.Advance(ms);
        controller.Tick();
    }

    private static void FinishReaction(FakeClock clock, BehaviorController controller)
    {
        Advance(clock, controller, 300);
        Advance(clock, controller, controller.Settings.HoldMs);
        Advance(clock, controller, 300);
    }

    [Fact]
    public void SleepWinsWhenBlinkAndSleepAreDueTogether()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100, BlinkMinMs = 100, BlinkMaxMs = 100 });
        Advance(clock, controller, 100);
        Assert.Equal(PetState.FallAsleep, controller.State);
        Assert.Equal("fall_asleep", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void BlinkDoesNotCountAsPetInteractionAndFinishesBeforeSleep()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 150, BlinkMinMs = 100, BlinkMaxMs = 100 });
        Advance(clock, controller, 100);
        Assert.Equal("blink", controller.Player.CurrentClip!.Name);
        Advance(clock, controller, 50);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(0, controller.LastPetInteraction);
        Advance(clock, controller, 40);
        Assert.Equal(PetState.FallAsleep, controller.State);
    }

    [Fact]
    public void FallingAsleepReversesDisplayedFramesAndIgnoresOldCompletion()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        var oldToken = controller.Player.Token;
        Advance(clock, controller, 150);
        Assert.Equal("fall_asleep-1.png", controller.Player.CurrentFrame!.File);
        controller.Touch();
        Assert.Equal(PetState.Wake, controller.State);
        Assert.Equal(new[] { "fall_asleep-1.png", "fall_asleep-0.png" }, controller.Player.CurrentClip!.Frames.Select(f => f.File));
        controller.HandleAnimationCompleted(new AnimationCompleted(oldToken, "fall_asleep"));
        Assert.Equal(PetState.Wake, controller.State);
        Advance(clock, controller, 200);
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal(ReactionPhase.Rise, controller.ReactionPhase);
    }

    [Fact]
    public void SleepingTouchPlaysWakeThenReaction()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 300);
        Assert.Equal(PetState.Sleep, controller.State);
        controller.Touch();
        Assert.Equal("wake", controller.Player.CurrentClip!.Name);
        Advance(clock, controller, 300);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void AwakeHoverOnlyRefreshesSleepDeadlineAndDoesNotInterruptBlink()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 250, BlinkMinMs = 100, BlinkMaxMs = 100 });
        var idleToken = controller.Player.Token;
        clock.Advance(50);
        controller.Hover();
        Assert.Equal(idleToken, controller.Player.Token);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(300, controller.SleepDeadline);
        Advance(clock, controller, 50);
        var blinkToken = controller.Player.Token;
        controller.Hover();
        Assert.Equal(blinkToken, controller.Player.Token);
        Assert.Equal("blink", controller.Player.CurrentClip!.Name);
        Assert.Equal(350, controller.SleepDeadline);
        Advance(clock, controller, 90);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoverWakesIntoIdleAndStartsAFreshQuietPeriod(bool completedSleep)
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, completedSleep ? 300 : 150);
        // Exercise an arbitrary breathing phase as well as a partly closed eye pose.
        if (completedSleep) Advance(clock, controller, 450);
        var visible = controller.Player.CurrentFrame!.File;
        controller.Hover();
        Assert.Equal(PetState.Wake, controller.State);
        Assert.Equal(visible, controller.Player.CurrentFrame!.File);
        var interactedAt = clock.NowMilliseconds;
        for (var i = 0; i < 3 && controller.State == PetState.Wake; i++)
            Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
        Assert.Equal(interactedAt, controller.LastPetInteraction);
        Assert.Equal(clock.NowMilliseconds + 100, controller.SleepDeadline);
        Advance(clock, controller, 99);
        Assert.Equal(PetState.Idle, controller.State);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.FallAsleep, controller.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickDuringHoverWakeUpgradesToRiseAndFurtherHoverCannotCancelIt(bool completedSleep)
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, completedSleep ? 300 : 150);
        if (completedSleep) Advance(clock, controller, 450);
        controller.Hover();
        var token = controller.Player.Token;
        var visible = controller.Player.CurrentFrame;
        controller.Click();
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Assert.Same(visible, controller.Player.CurrentFrame);
        for (var i = 0; i < 3 && controller.State == PetState.Wake; i++)
            Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void HoverDoesNotCancelAValidClickWaitingForBlinkOrExtendReaction()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { BlinkMinMs = 100, BlinkMaxMs = 100 });
        Advance(clock, controller, 100);
        controller.Click();
        controller.Hover();
        Advance(clock, controller, 90);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
        controller.Hover();
        FinishReaction(clock, controller);
        Assert.Equal(PetState.Idle, controller.State);
    }

    [Fact]
    public void DragDuringClickWakeCancelsThePendingRiseButCompletesWake()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 300);
        controller.Click();
        Advance(clock, controller, 100);
        controller.BeginDrag();
        var frame = controller.Player.CurrentFrame;
        Advance(clock, controller, 2000);
        Assert.Same(frame, controller.Player.CurrentFrame);
        controller.EndDrag();
        Advance(clock, controller, 200);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void TouchDuringBlinkPreservesTheBlinkAndReactsOnlyOnceAfterItEnds()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { BlinkMinMs = 100, BlinkMaxMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 35);
        var visible = controller.Player.CurrentFrame;
        var token = controller.Player.Token;
        controller.Touch();
        controller.Touch();
        Assert.Equal(token, controller.Player.Token);
        Assert.Same(visible, controller.Player.CurrentFrame);
        Assert.Equal(PetState.Idle, controller.State);
        Advance(clock, controller, 55);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
        FinishReaction(clock, controller);
        Assert.Equal(PetState.Idle, controller.State);
    }

    [Fact]
    public void SuspensionCancelsATouchWaitingForBlinkToFinish()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { BlinkMinMs = 100, BlinkMaxMs = 100 });
        Advance(clock, controller, 100);
        controller.Touch();
        var token = controller.Player.Token;
        controller.SetSuspended(true);
        controller.SetSuspended(false);
        controller.HandleAnimationCompleted(new AnimationCompleted(token, "blink"));
        Advance(clock, controller, 100);
        Advance(clock, controller, 90);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData(1, "sleep_loop-1.png", "sleep_loop-0.png")]
    [InlineData(3, "sleep_loop-3.png", "sleep_loop-0.png")]
    public void WakeReturnsFromTheExactBreathingPoseByTheShortPath(int phase, string first, string last)
    {
        var clock = new FakeClock();
        var clips = Clips();
        clips["wake"] = clips["wake"] with { Frames = [new("sleep_loop-0.png", 100), new("awake.png", 100)] };
        var controller = new BehaviorController(clock, clips, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 300);
        Advance(clock, controller, phase * 400);
        var visible = controller.Player.CurrentFrame!.File;
        controller.Touch();
        Assert.Equal(visible, controller.Player.CurrentFrame!.File);
        Assert.Equal("sleep_to_wake", controller.Player.CurrentClip!.Name);
        Assert.Equal(new[] { first, last }, controller.Player.CurrentClip.Frames.Select(frame => frame.File));
        Assert.InRange(controller.Player.CurrentClip.DurationMs, 1, 240);
        var bridgeToken = controller.Player.Token;
        controller.Touch();
        Assert.Equal(bridgeToken, controller.Player.Token);
        Advance(clock, controller, controller.Player.CurrentClip.DurationMs);
        Assert.Equal("wake", controller.Player.CurrentClip!.Name);
        Assert.Equal(last, controller.Player.CurrentFrame!.File);
        controller.HandleAnimationCompleted(new AnimationCompleted(bridgeToken, "sleep_to_wake"));
        Assert.Equal("wake", controller.Player.CurrentClip.Name);
        Advance(clock, controller, 200);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void DenseBreathingBridgeIsBoundedAndKeepsBothEndpoints()
    {
        var clock = new FakeClock();
        var clips = Clips();
        clips["sleep_loop"] = Clip("sleep_loop", 1000, 4, true);
        var controller = new BehaviorController(clock, clips, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 300);
        Advance(clock, controller, 600 * 4);
        controller.Touch();
        var bridge = controller.Player.CurrentClip!;
        Assert.Equal("sleep_loop-600.png", bridge.Frames[0].File);
        Assert.Equal("sleep_loop-0.png", bridge.Frames[^1].File);
        Assert.InRange(bridge.DurationMs, 1, 240);
        Assert.InRange(bridge.Frames.Count, 2, 60);
        Assert.All(bridge.Frames, frame => Assert.True(frame.DurationMs > 0));
        Assert.Equal(bridge.Frames.Count, bridge.Frames.Select(frame => frame.File).Distinct().Count());
    }

    [Fact]
    public void AnimatedHoldReturnsFromItsVisiblePoseBeforeSettlingAndCanPauseForDrag()
    {
        var clock = new FakeClock();
        var clips = Clips();
        clips["hold"] = Clip("hold", 4, 100, true);
        clips["settle"] = clips["settle"] with { Frames = [new("hold-0.png", 100), new("idle.png", 100)] };
        var controller = new BehaviorController(clock, clips, new PetSettings { HoldMs = 250 });
        controller.Touch();
        Advance(clock, controller, 300);
        Advance(clock, controller, 250);
        Assert.Equal(ReactionPhase.Settle, controller.ReactionPhase);
        Assert.Equal("hold_to_settle", controller.Player.CurrentClip!.Name);
        Assert.Equal("hold-2.png", controller.Player.CurrentFrame!.File);
        Assert.Equal("hold-0.png", controller.Player.CurrentClip.Frames[^1].File);
        Assert.InRange(controller.Player.CurrentClip.DurationMs, 1, 240);
        var token = controller.Player.Token;
        controller.BeginDrag();
        Advance(clock, controller, 10000);
        Assert.Equal("hold-2.png", controller.Player.CurrentFrame!.File);
        controller.EndDrag();
        Advance(clock, controller, controller.Player.CurrentClip.DurationMs);
        Assert.Equal("settle", controller.Player.CurrentClip!.Name);
        Assert.Equal("hold-0.png", controller.Player.CurrentFrame!.File);
        controller.HandleAnimationCompleted(new AnimationCompleted(token, "hold_to_settle"));
        Assert.Equal("settle", controller.Player.CurrentClip.Name);
        Advance(clock, controller, 200);
        Assert.Equal(PetState.Idle, controller.State);
    }

    [Fact]
    public void ReactionDoesNotExtendHoldOrQueueWhenTouchedAgain()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Touch();
        Advance(clock, controller, 300);
        Assert.Equal(ReactionPhase.Hold, controller.ReactionPhase);
        Advance(clock, controller, 900);
        controller.Touch();
        Advance(clock, controller, 100);
        Assert.Equal(ReactionPhase.Settle, controller.ReactionPhase);
        Advance(clock, controller, 300);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(1200, controller.LastPetInteraction);
    }

    [Fact]
    public void CooldownTouchesRefreshInteractionWithoutQueuedReaction()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Touch();
        FinishReaction(clock, controller);
        Advance(clock, controller, 200);
        controller.Touch();
        Assert.Equal(1800, controller.LastPetInteraction);
        Assert.Equal(PetState.Idle, controller.State);
        Advance(clock, controller, 1000);
        Assert.Equal(PetState.Idle, controller.State);
        controller.Touch();
        Assert.Equal(PetState.React, controller.State);
    }

    [Fact]
    public void ChangingCooldownImmediatelyRecalculatesFromReactionEnd()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Touch();
        FinishReaction(clock, controller);
        var endedAt = clock.NowMilliseconds;
        Advance(clock, controller, 200);
        controller.ApplySettings(controller.Settings with { CooldownMs = 5000 });
        Assert.Equal(endedAt + 5000, controller.ReactionCooldownUntil);
        controller.Touch();
        Assert.Equal(PetState.Idle, controller.State);
        controller.ApplySettings(controller.Settings with { CooldownMs = 0 });
        controller.Touch();
        Assert.Equal(PetState.React, controller.State);
    }

    [Fact]
    public void WakeBypassesResidualReactionCooldown()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 1700, CooldownMs = 5000 });
        controller.Touch();
        FinishReaction(clock, controller);
        Advance(clock, controller, 100);
        Assert.Equal(PetState.FallAsleep, controller.State);
        controller.Touch();
        Assert.Equal(PetState.Wake, controller.State);
        Assert.True(clock.NowMilliseconds < controller.ReactionCooldownUntil);
    }

    [Fact]
    public void DragFreezesFramesAndReleaseFromSleepWakes()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 300);
        controller.BeginDrag();
        var frame = controller.Player.CurrentFrame;
        Advance(clock, controller, 10000);
        Assert.Same(frame, controller.Player.CurrentFrame);
        Assert.Equal(PetState.Sleep, controller.State);
        controller.EndDrag();
        Assert.False(controller.IsDragging);
        Assert.Equal(PetState.Wake, controller.State);
        Assert.Equal(10400, controller.LastPetInteraction);
        Advance(clock, controller, 300);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void DragDuringHoldPreservesItsRemainingDuration()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Touch();
        Advance(clock, controller, 300);
        Advance(clock, controller, 200);
        controller.BeginDrag();
        Advance(clock, controller, 10000);
        controller.EndDrag();
        Advance(clock, controller, 799);
        Assert.Equal(ReactionPhase.Hold, controller.ReactionPhase);
        Advance(clock, controller, 1);
        Assert.Equal(ReactionPhase.Settle, controller.ReactionPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumePreservesOnlyCompletedSleep(bool sleeping)
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        if (sleeping) Advance(clock, controller, 300);
        controller.SetSuspended(true);
        Advance(clock, controller, 100000);
        Assert.Equal(long.MaxValue, controller.NextUpdateInMs);
        controller.SetSuspended(false);
        Assert.Equal(sleeping ? PetState.Sleep : PetState.Idle, controller.State);
        Assert.Equal(clock.NowMilliseconds + 100, controller.SleepDeadline);
        Assert.Equal(0, controller.ReactionCooldownUntil);
    }

    [Fact]
    public void SuspensionClearsDragAndOldCompletionCannotChangeRestoredState()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Touch();
        var token = controller.Player.Token;
        controller.BeginDrag();
        controller.SetSuspended(true);
        Assert.False(controller.IsDragging);
        controller.SetSuspended(false);
        controller.HandleAnimationCompleted(new AnimationCompleted(token, "rise"));
        Assert.Equal(PetState.Idle, controller.State);
    }

    [Fact]
    public void BusyMenuSuppressesSleepAndClosingRestartsQuietPeriod()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        controller.SetInputBusy(true);
        Advance(clock, controller, 500);
        Assert.Equal(PetState.Idle, controller.State);
        controller.SetInputBusy(false);
        Assert.Equal(0, controller.LastPetInteraction);
        Advance(clock, controller, 99);
        Assert.Equal(PetState.Idle, controller.State);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.FallAsleep, controller.State);
    }

    [Fact]
    public void DisablingAutomaticActionsStillAllowsTouch()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.SetAutomaticActionsPaused(true);
        Advance(clock, controller, 100000);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(long.MaxValue, controller.NextUpdateInMs);
        controller.Touch();
        Assert.Equal(PetState.React, controller.State);
    }

    [Fact]
    public void SettingsAreValidatedBeforeReplacingCurrentConfiguration()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        Assert.Throws<ArgumentException>(() => controller.ApplySettings(new PetSettings { BlinkMinMs = 9000, BlinkMaxMs = 1 }));
        Assert.Equal(3000, controller.Settings.BlinkMinMs);
        controller.ApplySettings(new PetSettings { SleepAfterMs = 50 });
        Advance(clock, controller, 50);
        Assert.Equal(PetState.FallAsleep, controller.State);
    }
}
