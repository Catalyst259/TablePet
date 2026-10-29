using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class MeowReactionTests
{
    private static Dictionary<string, AnimationClip> Pack()
    {
        var clips = new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle", "wink", "yawn" }
            .ToDictionary(name => name, name => new AnimationClip(name,
                [new("rest.png", 20), new(name + ".png", 100), new("rest.png", 20)], name is "idle" or "sleep_loop"));
        clips["idle"] = new("idle", [new("rest.png", 1000)], true);
        clips["meow"] = new("meow", [new("rest.png", 20), new("meow.png", 1360), new("rest.png", 20)]);
        return clips;
    }

    private static BehaviorController Create(FakeClock clock, PetSettings? settings = null, string? idleAction = null) =>
        new(clock, Pack(), settings ?? new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 },
            (_, max) => max, idleAction is null ? null : [new(idleAction)]);

    private static void Advance(FakeClock clock, BehaviorController controller, long milliseconds)
    {
        clock.Advance(milliseconds);
        controller.Tick();
    }

    [Fact]
    public void DoubleClickAlwaysMeowsAndDoesNotConsultRandomClickSelection()
    {
        var clock = new FakeClock();
        var controller = new BehaviorController(clock, Pack(), new PetSettings { AutoSleep = false },
            (min, max) => min == 0 && max == 1 ? throw new InvalidOperationException("Random click selection") : max);
        controller.DoubleClick();
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal(ReactionPhase.Meow, controller.ReactionPhase);
        Assert.Equal("meow", controller.Player.CurrentClip!.Name);
        Assert.Equal(0, controller.Player.ElapsedMs);
        Advance(clock, controller, 1400);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void MeowIgnoresRepeatedInputAndSharesCooldownWithoutQueueing()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.SetAutomaticActionsPaused(true);
        controller.DoubleClick();
        var token = controller.Player.Token;
        Advance(clock, controller, 500);
        controller.Click();
        controller.DoubleClick();
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Advance(clock, controller, 899);
        Assert.Equal(ReactionPhase.Meow, controller.ReactionPhase);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(2400, controller.ReactionCooldownUntil);
        controller.DoubleClick();
        Advance(clock, controller, 999);
        controller.Click();
        Assert.Equal(PetState.Idle, controller.State);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.Idle, controller.State);
        controller.Click();
        Assert.Equal(ReactionPhase.Wink, controller.ReactionPhase);
        controller.DoubleClick();
        Advance(clock, controller, 140);
        Assert.Equal(PetState.Idle, controller.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SleepingDoubleClickWaitsForWakeAndPreservesMeowOverLaterClicks(bool completedSleep, bool hoverFirst)
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100 });
        Advance(clock, controller, 100);
        Advance(clock, controller, completedSleep ? 140 : 60);
        if (completedSleep) Advance(clock, controller, 60);
        if (hoverFirst) controller.Hover();
        controller.DoubleClick();
        Assert.Equal(PetState.Wake, controller.State);
        Assert.NotEqual(ReactionPhase.Meow, controller.ReactionPhase);
        var token = controller.Player.Token;
        controller.Click();
        controller.Hover();
        controller.DoubleClick();
        Assert.Equal(token, controller.Player.Token);
        for (var i = 0; i < 3 && controller.State == PetState.Wake; i++)
            Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal(ReactionPhase.Meow, controller.ReactionPhase);
        Assert.Equal("meow", controller.Player.CurrentClip!.Name);
        Assert.Equal(0, controller.Player.ElapsedMs);
    }

    [Theory]
    [InlineData("blink")]
    [InlineData("yawn")]
    public void DoubleClickUpgradesPendingClickWithoutRestartingIdleReturn(string action)
    {
        var clock = new FakeClock();
        var controller = Create(clock, idleAction: action);
        Advance(clock, controller, 100);
        Advance(clock, controller, 60);
        var visible = controller.Player.CurrentFrame!.File;
        controller.Click();
        var token = controller.Player.Token;
        controller.DoubleClick();
        controller.Click();
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Assert.Equal(visible, controller.Player.CurrentFrame!.File);
        Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal("meow", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData("blink", false)]
    [InlineData("blink", true)]
    [InlineData("yawn", false)]
    [InlineData("yawn", true)]
    [InlineData("wake", false)]
    [InlineData("wake", true)]
    public void DragOrSuspensionCancelsPendingMeow(string waitingOn, bool suspend)
    {
        var clock = new FakeClock();
        var settings = new PetSettings { AutoSleep = waitingOn == "wake", SleepAfterMs = 100, BlinkMinMs = 100, BlinkMaxMs = 100 };
        var controller = Create(clock, settings, waitingOn);
        Advance(clock, controller, 100);
        Advance(clock, controller, 60);
        controller.DoubleClick();
        var pendingClip = controller.Player.CurrentClip!;
        var token = controller.Player.Token;
        controller.SetAutomaticActionsPaused(true);
        if (suspend) controller.SetSuspended(true);
        else controller.BeginDrag();
        Advance(clock, controller, 10000);
        if (suspend) controller.SetSuspended(false);
        else controller.EndDrag();
        Advance(clock, controller, pendingClip.DurationMs);
        controller.HandleAnimationCompleted(new(token, pendingClip.Name));
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void DragFreezesMeowClockAndResumesRemainingDuration()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.DoubleClick();
        Advance(clock, controller, 300);
        controller.BeginDrag();
        Advance(clock, controller, 10000);
        controller.DoubleClick();
        Assert.Equal(300, controller.Player.ElapsedMs);
        controller.EndDrag();
        Advance(clock, controller, 1099);
        Assert.Equal(ReactionPhase.Meow, controller.ReactionPhase);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.Idle, controller.State);
    }

    [Fact]
    public void SuspensionCancelsActiveMeowAndRejectsLateCompletion()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.DoubleClick();
        var token = controller.Player.Token;
        controller.SetSuspended(true);
        Advance(clock, controller, 10000);
        controller.DoubleClick();
        controller.SetSuspended(false);
        controller.HandleAnimationCompleted(new(token, "meow"));
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
        Assert.Equal(0, controller.ReactionCooldownUntil);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("looping")]
    [InlineData("wrong-name")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    [InlineData("bad-timing")]
    [InlineData("single-frame")]
    public void MissingOrUnsafeOptionalMeowFallsBackToExistingClickReaction(string kind)
    {
        var clips = Pack();
        switch (kind)
        {
            case "missing": clips.Remove("meow"); break;
            case "looping": clips["meow"] = clips["meow"] with { Loop = true }; break;
            case "wrong-name": clips["meow"] = clips["meow"] with { Name = "other" }; break;
            case "wrong-start": clips["meow"] = new("meow", [new("other.png", 20), new("rest.png", 20)]); break;
            case "wrong-end": clips["meow"] = new("meow", [new("rest.png", 20), new("other.png", 20)]); break;
            case "bad-timing": clips["meow"] = new("meow", [new("rest.png", 0), new("rest.png", 20)]); break;
            case "single-frame": clips["meow"] = new("meow", [new("rest.png", 20)]); break;
        }
        var controller = new BehaviorController(new FakeClock(), clips, randomBetween: (_, max) => max);
        controller.DoubleClick();
        Assert.Equal("wink", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.Wink, controller.ReactionPhase);
    }

    [Fact]
    public void PendingGesturePreventsNewAutomaticActionsButAllowsExplicitMeow()
    {
        var clock = new FakeClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100, BlinkMinMs = 50, BlinkMaxMs = 50 });
        controller.SetInputBusy(true);
        Advance(clock, controller, 500);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(long.MaxValue, controller.NextUpdateInMs);
        controller.DoubleClick();
        Assert.Equal("meow", controller.Player.CurrentClip!.Name);
    }
}
