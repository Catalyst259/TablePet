using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class ClickReactionTests
{
    private static Dictionary<string, AnimationClip> Pack()
    {
        var clips = new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle", "wink", "yawn" }
            .ToDictionary(name => name, name => new AnimationClip(name,
                [new("rest.png", 20), new(name + ".png", 100), new("rest.png", 20)], name is "idle" or "sleep_loop"));
        clips["idle"] = new("idle", [new("rest.png", 1000)], true);
        return clips;
    }

    private static BehaviorController Create(FakeClock clock, int ticket = 1, PetSettings? settings = null,
        string? idleAction = null, Func<int>? nextReaction = null) =>
        new(clock, Pack(), settings ?? new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 },
            (min, max) => min == 0 && max == 1 ? nextReaction?.Invoke() ?? ticket : max,
            idleAction is null ? null : [new(idleAction)]);

    private static void Advance(FakeClock clock, BehaviorController controller, long milliseconds)
    {
        clock.Advance(milliseconds);
        controller.Tick();
    }

    [Theory]
    [InlineData(0, "rise", ReactionPhase.Rise)]
    [InlineData(1, "wink", ReactionPhase.Wink)]
    public void AnAcceptedClickCanChooseEitherReaction(int ticket, string clip, ReactionPhase phase)
    {
        var clock = new FakeClock();
        var controller = Create(clock, ticket);
        var idleToken = controller.Player.Token;
        controller.Hover();
        Assert.Equal(idleToken, controller.Player.Token);
        controller.Click();
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal(phase, controller.ReactionPhase);
        Assert.Equal(clip, controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void WinkReturnsDirectlyToIdleAndSharesTheReactionCooldownWithoutQueuingClicks()
    {
        var clock = new FakeClock();
        var selections = new Queue<int>([1, 0]);
        var controller = Create(clock, nextReaction: selections.Dequeue);
        controller.SetAutomaticActionsPaused(true);
        controller.Click();
        var winkToken = controller.Player.Token;
        Advance(clock, controller, 60);
        controller.Click();
        controller.Hover();
        Assert.Equal(winkToken, controller.Player.Token);
        Advance(clock, controller, 79);
        Assert.Equal(ReactionPhase.Wink, controller.ReactionPhase);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(1140, controller.ReactionCooldownUntil);

        controller.HandleAnimationCompleted(new(winkToken, "wink"));
        controller.Click();
        Advance(clock, controller, 999);
        controller.Click();
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Single(selections);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.Idle, controller.State);
        controller.Click();
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
        Assert.Empty(selections);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("looping")]
    [InlineData("wrong-name")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    [InlineData("bad-timing")]
    [InlineData("single-frame")]
    public void MissingOrUnsafeOptionalWinkFallsBackToRise(string kind)
    {
        var clips = Pack();
        switch (kind)
        {
            case "missing": clips.Remove("wink"); break;
            case "looping": clips["wink"] = clips["wink"] with { Loop = true }; break;
            case "wrong-name": clips["wink"] = clips["wink"] with { Name = "other" }; break;
            case "wrong-start": clips["wink"] = new("wink", [new("other.png", 20), new("rest.png", 20)]); break;
            case "wrong-end": clips["wink"] = new("wink", [new("rest.png", 20), new("other.png", 20)]); break;
            case "bad-timing": clips["wink"] = new("wink", [new("rest.png", 0), new("rest.png", 20)]); break;
            case "single-frame": clips["wink"] = new("wink", [new("rest.png", 20)]); break;
        }
        var controller = new BehaviorController(new FakeClock(), clips, randomBetween: (_, max) => max);
        controller.Click();
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.Rise, controller.ReactionPhase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClickWhileSleepingOrHoverWakingStartsOneWinkAfterWake(bool completedSleep, bool hoverFirst)
    {
        var clock = new FakeClock();
        var selections = 0;
        var controller = Create(clock, settings: new PetSettings { SleepAfterMs = 100 },
            nextReaction: () => { selections++; return 1; });
        Advance(clock, controller, 100);
        Advance(clock, controller, completedSleep ? 140 : 60);
        if (completedSleep) Advance(clock, controller, 60);
        if (hoverFirst) controller.Hover();
        controller.Click();
        var token = controller.Player.Token;
        controller.Click();
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Assert.Equal(0, selections);
        for (var i = 0; i < 3 && controller.State == PetState.Wake; i++)
            Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal("wink", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.Wink, controller.ReactionPhase);
        Assert.Equal(1, selections);
    }

    [Theory]
    [InlineData("blink")]
    [InlineData("yawn")]
    public void ClickDuringIdleActionKeepsItsVisiblePoseThenWinksOnce(string action)
    {
        var clock = new FakeClock();
        var selections = 0;
        var controller = Create(clock, idleAction: action, nextReaction: () => { selections++; return 1; });
        Advance(clock, controller, 100);
        Advance(clock, controller, 60);
        var visible = controller.Player.CurrentFrame!.File;
        controller.Click();
        Assert.Equal(visible, controller.Player.CurrentFrame!.File);
        var token = controller.Player.Token;
        controller.Click();
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal("wink", controller.Player.CurrentClip!.Name);
        Assert.Equal(1, selections);
    }

    [Theory]
    [InlineData("blink", false)]
    [InlineData("blink", true)]
    [InlineData("yawn", false)]
    [InlineData("yawn", true)]
    [InlineData("wake", false)]
    [InlineData("wake", true)]
    public void DragOrSuspensionCancelsAPendingWink(string waitingOn, bool suspend)
    {
        var clock = new FakeClock();
        var selections = 0;
        var settings = new PetSettings { AutoSleep = waitingOn == "wake", SleepAfterMs = 100, BlinkMinMs = 100, BlinkMaxMs = 100 };
        var controller = Create(clock, settings: settings, idleAction: waitingOn,
            nextReaction: () => { selections++; return 1; });
        Advance(clock, controller, 100);
        Advance(clock, controller, 60);
        controller.Click();
        var pendingClip = controller.Player.CurrentClip!;
        var pendingToken = controller.Player.Token;
        controller.SetAutomaticActionsPaused(true);
        if (suspend) controller.SetSuspended(true);
        else controller.BeginDrag();
        Advance(clock, controller, 10000);
        if (suspend) controller.SetSuspended(false);
        else controller.EndDrag();
        Advance(clock, controller, pendingClip.DurationMs);
        controller.HandleAnimationCompleted(new(pendingToken, pendingClip.Name));
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(0, selections);
    }

    [Fact]
    public void DragPausesAnActiveWinkAndResumesItsRemainingDuration()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Click();
        Advance(clock, controller, 60);
        var frame = controller.Player.CurrentFrame;
        controller.BeginDrag();
        Advance(clock, controller, 10000);
        controller.Click();
        Assert.Same(frame, controller.Player.CurrentFrame);
        controller.EndDrag();
        Advance(clock, controller, 79);
        Assert.Equal(ReactionPhase.Wink, controller.ReactionPhase);
        Advance(clock, controller, 1);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(clock.NowMilliseconds + controller.Settings.CooldownMs, controller.ReactionCooldownUntil);
    }

    [Fact]
    public void SuspensionCancelsAnActiveWinkAndRejectsItsLateCompletion()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        controller.Click();
        var token = controller.Player.Token;
        controller.SetSuspended(true);
        Advance(clock, controller, 10000);
        controller.SetSuspended(false);
        controller.HandleAnimationCompleted(new(token, "wink"));
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
        Assert.Equal(0, controller.ReactionCooldownUntil);
    }
}
