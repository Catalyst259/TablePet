using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class FireflyReactionTests
{
    private static readonly IdleAction[] Choices = [new("blink", 2), new("yawn"), new("rise"), new("firefly")];

    private static Dictionary<string, AnimationClip> Pack()
    {
        var clips = new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "wink", "yawn", "meow" }
            .ToDictionary(name => name, name => new AnimationClip(name,
                [new("rest.png", 20), new(name + ".png", 100), new("rest.png", 20)], name is "idle" or "sleep_loop"));
        clips["idle"] = new("idle", [new("rest.png", 1000)], true);
        clips["rise"] = new("rise", [new("rest.png", 20), new("standing.png", 80)]);
        clips["hold"] = new("hold", [new("standing.png", 1000)], true);
        clips["settle"] = new("settle", [new("standing.png", 20), new("rest.png", 80)]);
        clips["firefly"] = new("firefly", [new("rest.png", 20), new("tracking.png", 4980),
            new("paw.png", 2000), new("following-up.png", 2980), new("rest.png", 20)]);
        return clips;
    }

    private static BehaviorController Create(FakeClock clock, int ticket = 5, PetSettings? settings = null,
        Func<int, int, int>? random = null, Dictionary<string, AnimationClip>? clips = null) =>
        new(clock, clips ?? Pack(), settings ?? new PetSettings
        { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100, HoldMs = 500 },
            random ?? ((min, max) => min == 0 ? 1 : min == max ? min : Math.Min(ticket, max)), Choices);

    private static void Advance(FakeClock clock, BehaviorController controller, long milliseconds)
    {
        clock.Advance(milliseconds);
        controller.Tick();
    }

    private static void FinishAutomatic(FakeClock clock, BehaviorController controller)
    {
        for (var guard = 0; controller.IsAutomaticActionActive && guard < 100; guard++)
        {
            Assert.InRange(controller.NextUpdateInMs, 1, 10000);
            Advance(clock, controller, controller.NextUpdateInMs);
        }
        Assert.False(controller.IsAutomaticActionActive);
    }

    [Theory]
    [InlineData(1, "blink")]
    [InlineData(2, "blink")]
    [InlineData(3, "yawn")]
    [InlineData(4, "rise")]
    [InlineData(5, "firefly")]
    public void WeightedDrawUsesTheExactTwoOneOneOneBoundaries(int ticket, string expected)
    {
        var clock = new FakeClock();
        var draws = new List<(int, int)>();
        var controller = Create(clock, random: (min, max) =>
        {
            if (min == max) return min;
            draws.Add((min, max));
            return ticket;
        });
        Advance(clock, controller, 100);
        Assert.Equal((1, 5), Assert.Single(draws));
        Assert.Equal(expected, controller.Player.CurrentClip!.Name);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.True(controller.IsAutomaticActionActive);
        Assert.Equal(expected == "firefly", controller.IsFireflyActive);
        Assert.Equal(0, controller.LastPetInteraction);
    }

    [Fact]
    public void IndependentDrawsPreserveTheDistributionAndAllowConsecutiveActions()
    {
        var clock = new FakeClock();
        var random = new Random(734);
        var controller = Create(clock, random: (min, max) => min == max ? min : random.Next(min, max + 1));
        var counts = Choices.ToDictionary(choice => choice.Clip, _ => 0);
        string? previous = null;
        var consecutive = 0;
        for (var i = 0; i < 10000; i++)
        {
            Advance(clock, controller, 100);
            var selected = controller.Player.CurrentClip!.Name;
            counts[selected]++;
            if (selected == previous) consecutive++;
            previous = selected;
            FinishAutomatic(clock, controller);
            Assert.Equal(clock.NowMilliseconds + 100, controller.NextIdleActionAt);
            Assert.Equal(0, controller.ReactionCooldownUntil);
        }
        Assert.InRange(counts["blink"], 3850, 4150);
        foreach (var name in new[] { "yawn", "rise", "firefly" }) Assert.InRange(counts[name], 1850, 2150);
        Assert.True(consecutive > 2000);
        Assert.Equal(0, controller.LastPetInteraction);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void AutomaticSequenceFinishesBeforeSleepingWithoutMovingTheDeadline(int ticket)
    {
        var clock = new FakeClock();
        var controller = Create(clock, ticket, settings: new PetSettings
        { AutoSleep = true, SleepAfterMs = 250, BlinkMinMs = 100, BlinkMaxMs = 100, HoldMs = 500 });
        Advance(clock, controller, 100);
        FinishAutomatic(clock, controller);
        Assert.Equal(PetState.FallAsleep, controller.State);
        Assert.Equal("fall_asleep", controller.Player.CurrentClip!.Name);
        Assert.Equal(250, controller.SleepDeadline);
        Assert.Equal(0, controller.LastPetInteraction);
        Assert.Equal(0, controller.ReactionCooldownUntil);
    }

    [Fact]
    public void AutomaticRisePlaysItsWholeHoldAndSettleThenSchedulesFromCompletion()
    {
        var clock = new FakeClock();
        var controller = Create(clock, 4);
        Advance(clock, controller, 100);
        Assert.Equal(ReactionPhase.Rise, controller.ReactionPhase);
        Advance(clock, controller, 100);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(ReactionPhase.Hold, controller.ReactionPhase);
        Advance(clock, controller, 499);
        Assert.Equal("hold", controller.Player.CurrentClip!.Name);
        Advance(clock, controller, 1);
        Assert.Equal(ReactionPhase.Settle, controller.ReactionPhase);
        Assert.Equal("standing.png", controller.Player.CurrentFrame!.File);
        Advance(clock, controller, 100);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
        Assert.Equal(900, controller.NextIdleActionAt);
        Assert.Equal(0, controller.ReactionCooldownUntil);
        Assert.Equal(0, controller.LastPetInteraction);
    }

    [Theory]
    [InlineData(false, "wink")]
    [InlineData(true, "meow")]
    public void ClickDuringFireflyKeepsTheOriginalPairedClipUntilItsEndpoint(bool doubleClick, string expected)
    {
        var clock = new FakeClock();
        var pack = Pack();
        var draws = 0;
        var controller = Create(clock, clips: pack, random: (min, max) =>
        { if (min == 0) { draws++; return 1; } return max; });
        Advance(clock, controller, 100);
        Advance(clock, controller, 6000);
        var token = controller.Player.Token;
        var frame = controller.Player.CurrentFrame;
        controller.Click();
        if (doubleClick) controller.DoubleClick();
        controller.Click();
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Assert.Same(pack["firefly"], controller.Player.CurrentClip);
        Assert.Same(frame, controller.Player.CurrentFrame);
        Assert.True(controller.IsFireflyActive);
        Assert.Equal(0, draws);
        Advance(clock, controller, 3999);
        Assert.True(controller.IsFireflyActive);
        Advance(clock, controller, 1);
        Assert.False(controller.IsFireflyActive);
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal(expected, controller.Player.CurrentClip!.Name);
        Assert.Equal(doubleClick ? 0 : 1, draws);
        var reactionToken = controller.Player.Token;
        controller.Click();
        controller.DoubleClick();
        Assert.Equal(reactionToken, controller.Player.Token);
        controller.SetAutomaticActionsPaused(true);
        Advance(clock, controller, 140);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(clock.NowMilliseconds + controller.Settings.CooldownMs, controller.ReactionCooldownUntil);
        Advance(clock, controller, 2000);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(600)]
    public void ClickDuringAutomaticRiseWaitsForAllThreePhases(int elapsed)
    {
        var clock = new FakeClock();
        var controller = Create(clock, 4);
        Advance(clock, controller, 100);
        if (elapsed >= 100) Advance(clock, controller, 100);
        if (elapsed >= 600) Advance(clock, controller, 500);
        var token = controller.Player.Token;
        var frame = controller.Player.CurrentFrame;
        controller.Click();
        controller.Click();
        Assert.Equal(token, controller.Player.Token);
        Assert.Same(frame, controller.Player.CurrentFrame);
        FinishAutomatic(clock, controller);
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal("wink", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void DragPausesTheAutomaticSequenceAndCancelsThePendingClick(int ticket)
    {
        var clock = new FakeClock();
        var controller = Create(clock, ticket);
        Advance(clock, controller, 100);
        Advance(clock, controller, 100);
        Advance(clock, controller, 200);
        controller.Click();
        var remaining = controller.NextUpdateInMs;
        var token = controller.Player.Token;
        var frame = controller.Player.CurrentFrame;
        controller.BeginDrag();
        Advance(clock, controller, 20000);
        controller.Click();
        controller.HandleAnimationCompleted(new(token, controller.Player.CurrentClip!.Name));
        Assert.Same(frame, controller.Player.CurrentFrame);
        controller.EndDrag();
        Assert.Equal(remaining, controller.NextUpdateInMs);
        Assert.Equal(token, controller.Player.Token);
        FinishAutomatic(clock, controller);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(0, controller.ReactionCooldownUntil);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void SuspensionCancelsTheSequenceAndRejectsOldCompletions(int ticket)
    {
        var clock = new FakeClock();
        var controller = Create(clock, ticket);
        Advance(clock, controller, 100);
        controller.Click();
        var oldClip = controller.Player.CurrentClip!;
        var oldToken = controller.Player.Token;
        controller.SetSuspended(true);
        Assert.False(controller.IsFireflyActive);
        Advance(clock, controller, 20000);
        controller.SetSuspended(false);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Advance(clock, controller, 100);
        var newToken = controller.Player.Token;
        controller.HandleAnimationCompleted(new(oldToken, oldClip.Name));
        Assert.Equal(newToken, controller.Player.Token);
        Assert.True(controller.IsAutomaticActionActive);
        FinishAutomatic(clock, controller);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseOrInputBusyLetsFireflyFinishAndSuppressesNewAutomaticWork(bool busy)
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        Advance(clock, controller, 100);
        if (busy) controller.SetInputBusy(true);
        else controller.SetAutomaticActionsPaused(true);
        FinishAutomatic(clock, controller);
        Advance(clock, controller, 100000);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(long.MaxValue, controller.NextUpdateInMs);
        controller.Click();
        Assert.Equal("wink", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("looping")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    [InlineData("bad-timing")]
    public void UnsafeOptionalFireflyIsRemovedFromOnlyTheAutomaticPool(string defect)
    {
        var pack = Pack();
        switch (defect)
        {
            case "missing": pack.Remove("firefly"); break;
            case "looping": pack["firefly"] = pack["firefly"] with { Loop = true }; break;
            case "wrong-start": pack["firefly"] = new("firefly", [new("wrong.png", 20), new("rest.png", 20)]); break;
            case "wrong-end": pack["firefly"] = new("firefly", [new("rest.png", 20), new("wrong.png", 20)]); break;
            case "bad-timing": pack["firefly"] = new("firefly", [new("rest.png", 0), new("rest.png", 20)]); break;
        }
        var controller = Create(new FakeClock(), clips: pack);
        Assert.Equal(new[] { "blink", "yawn", "rise" }, controller.IdleActions.Select(action => action.Clip));
        controller.Click();
        Assert.Equal("wink", controller.Player.CurrentClip!.Name);
    }

    [Fact]
    public void UnsafeRiseIsExcludedAndAnOldPackStillBlinksAndFallsBackToExplicitRise()
    {
        var pack = Pack();
        pack["settle"] = new("settle", [new("wrong-standing.png", 20), new("rest.png", 20)]);
        Assert.DoesNotContain(IdleAction.Available(pack, Choices), action => action.Clip == "rise");
        foreach (var optional in new[] { "meow", "wink", "yawn", "firefly" }) pack.Remove(optional);
        var controller = new BehaviorController(new FakeClock(), pack, randomBetween: (_, max) => max);
        Assert.Equal("blink", Assert.Single(controller.IdleActions).Clip);
        controller.Click();
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData(0, "meow")]
    [InlineData(1, "wink")]
    public void ClickPoolContainsEqualMeowAndWinkWithoutAutomaticActions(int ticket, string expected)
    {
        var draws = new List<(int, int)>();
        var controller = Create(new FakeClock(), random: (min, max) =>
        { if (min != 0) return max; draws.Add((min, max)); return ticket; });
        controller.Click();
        Assert.Equal((0, 1), Assert.Single(draws));
        Assert.Equal(expected, controller.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData("wink", "meow")]
    [InlineData("meow", "wink")]
    public void ClickUsesTheRemainingSafeReactionWithoutDrawing(string missing, string expected)
    {
        var pack = Pack();
        pack.Remove(missing);
        var controller = Create(new FakeClock(), clips: pack,
            random: (min, max) => min == 0 ? throw new InvalidOperationException("Only one choice") : max);
        controller.Click();
        Assert.Equal(expected, controller.Player.CurrentClip!.Name);
    }
}
