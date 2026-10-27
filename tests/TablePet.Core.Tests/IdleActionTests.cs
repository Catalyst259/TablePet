using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class IdleActionTests
{
    private static readonly IdleAction[] Choices = [new("blink", 4), new("yawn"), new("happy"), new("wink")];

    private static Dictionary<string, AnimationClip> Pack(bool expressions = true)
    {
        var clips = new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" }
            .ToDictionary(name => name, name => new AnimationClip(name,
                [new("rest.png", 20), new(name + ".png", 100), new("rest.png", 20)], name is "idle" or "sleep_loop"));
        clips["idle"] = new("idle", [new("rest.png", 1000)], true);
        if (expressions)
            foreach (var name in new[] { "yawn", "happy", "wink" })
                clips[name] = new(name, [new("rest.png", 20), new(name + "-opening.png", 400),
                    new(name + "-peak.png", 600), new(name + "-closing.png", 300), new("rest.png", 20)]);
        return clips;
    }

    private static BehaviorController Create(FakeClock clock, int ticket = 5, PetSettings? settings = null) =>
        new(clock, Pack(), settings ?? new PetSettings { BlinkMinMs = 100, BlinkMaxMs = 100 },
            (min, max) => min == max ? min : Math.Min(ticket, max), Choices);

    private static void Advance(FakeClock clock, BehaviorController controller, long milliseconds)
    {
        clock.Advance(milliseconds);
        controller.Tick();
    }

    [Theory]
    [InlineData(1, "blink")]
    [InlineData(4, "blink")]
    [InlineData(5, "yawn")]
    [InlineData(6, "happy")]
    [InlineData(7, "wink")]
    public void RandomTicketSelectsEveryExpressionIncludingWeightedBlink(int ticket, string expected)
    {
        var clock = new FakeClock();
        var controller = Create(clock, ticket);
        Advance(clock, controller, 100);
        Assert.Equal(expected, controller.Player.CurrentClip!.Name);
        Assert.False(controller.Player.CurrentClip.Loop);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(0, controller.LastPetInteraction);
    }

    [Fact]
    public void RepeatedIdleDeadlinesChooseAllActionsWithoutImmediateRepetition()
    {
        var clock = new FakeClock();
        var random = new Random(42);
        var controller = new BehaviorController(clock, Pack(),
            new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 },
            (min, max) => random.Next(min, max + 1), Choices);
        var played = new HashSet<string>();
        string? previous = null;
        for (var i = 0; i < 100; i++)
        {
            Advance(clock, controller, 100);
            var clip = controller.Player.CurrentClip!;
            Assert.NotEqual(previous, clip.Name);
            played.Add(clip.Name);
            previous = clip.Name;
            Advance(clock, controller, clip.DurationMs);
            Assert.Equal("idle", controller.Player.CurrentClip!.Name);
            Assert.Equal(clock.NowMilliseconds + 100, controller.NextIdleActionAt);
        }
        Assert.Equal(Choices.Select(a => a.Clip).Order(), played.Order());
        Assert.Equal(0, controller.LastPetInteraction);
    }

    [Fact]
    public void OldPackOrMissingOptionalClipsKeepsBlinkAvailable()
    {
        var clock = new FakeClock();
        foreach (var requested in new[] { null, Choices })
        {
            var controller = new BehaviorController(clock, Pack(false),
                new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 }, (_, max) => max, requested);
            Assert.Equal("blink", Assert.Single(controller.IdleActions).Clip);
            for (var i = 0; i < 3; i++)
            {
                Advance(clock, controller, 100);
                Assert.Equal("blink", controller.Player.CurrentClip!.Name);
                Advance(clock, controller, controller.Player.CurrentClip.DurationMs);
            }
        }
    }

    [Fact]
    public void InvalidOrUnregisteredTransitionsCannotBecomeIdleActions()
    {
        var clips = Pack();
        clips["looping"] = clips["yawn"] with { Name = "looping", Loop = true };
        clips["wrong-start"] = new("wrong-start", [new("other.png", 10), new("rest.png", 10)]);
        clips["wrong-end"] = new("wrong-end", [new("rest.png", 10), new("other.png", 10)]);
        clips["bad-timing"] = new("bad-timing", [new("rest.png", 0), new("rest.png", 10)]);
        IdleAction[] requested = [new("looping"), new("wrong-start"), new("wrong-end"), new("bad-timing"),
            new("missing"), new("rise"), new("idle"), new("happy", 0), new("wink", 1001), new("yawn"), new("yawn")];
        var available = IdleAction.Available(clips, requested);
        Assert.Equal(new[] { "blink", "yawn" }, available.Select(a => a.Clip));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ClickReturnsFromTheVisibleExpressionThenRisesOnlyOnce(int ticket)
    {
        var clock = new FakeClock();
        var controller = Create(clock, ticket);
        Advance(clock, controller, 100);
        var expression = controller.Player.CurrentClip!;
        var oldToken = controller.Player.Token;
        Advance(clock, controller, 500);
        var visible = controller.Player.CurrentFrame!.File;
        controller.Click();
        var returning = controller.Player.CurrentClip!;
        Assert.Equal("idle_to_react", returning.Name);
        Assert.Equal(visible, returning.Frames[0].File);
        Assert.Equal("rest.png", returning.Frames[^1].File);
        Assert.InRange(returning.DurationMs, 1, 240);
        var returnToken = controller.Player.Token;
        controller.Click();
        controller.Hover();
        controller.HandleAnimationCompleted(new(oldToken, expression.Name));
        Assert.Equal(returnToken, controller.Player.Token);
        Advance(clock, controller, returning.DurationMs);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
        Assert.Equal(PetState.React, controller.State);
        Assert.Equal(ReactionPhase.Rise, controller.ReactionPhase);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(599)]
    public void ClickDuringLongExpressionHoldStartsReturningWithoutReplayingTheHold(int holdElapsed)
    {
        var clips = Pack();
        var sourceFrames = new[] { new AnimationFrame("rest.png", 4) }
            .Concat(Enumerable.Range(1, 20).Select(i => new AnimationFrame($"closing-{i}.png", 4)))
            .Concat(Enumerable.Repeat(new AnimationFrame("closed.png", 4), 150))
            .Concat(Enumerable.Range(1, 18).Select(i => new AnimationFrame($"opening-{i}.png", 4)))
            .Append(new AnimationFrame("rest.png", 4)).ToArray();
        var sourceSnapshot = sourceFrames.ToArray();
        clips["wink"] = new("wink", sourceFrames);
        var clock = new FakeClock();
        var controller = new BehaviorController(clock, clips,
            new PetSettings { AutoSleep = false, BlinkMinMs = 100, BlinkMaxMs = 100 },
            (min, max) => min == max ? min : 7, Choices);
        Advance(clock, controller, 100);
        Advance(clock, controller, 84 + holdElapsed);
        Assert.Equal("closed.png", controller.Player.CurrentFrame!.File);

        controller.Click();
        var returning = controller.Player.CurrentClip!;
        Assert.Equal("idle_to_react", returning.Name);
        Assert.Equal("closed.png", returning.Frames[0].File);
        Assert.Equal("rest.png", returning.Frames[^1].File);
        Assert.InRange(returning.DurationMs, 1, 240);
        Assert.DoesNotContain(returning.Frames.Zip(returning.Frames.Skip(1)), pair => pair.First.File == pair.Second.File);
        Advance(clock, controller, returning.Frames[0].DurationMs);
        Assert.NotEqual("closed.png", controller.Player.CurrentFrame!.File);
        Advance(clock, controller, returning.DurationMs - returning.Frames[0].DurationMs);
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
        Assert.Equal(sourceSnapshot, clips["wink"].Frames);
    }

    [Fact]
    public void HoverKeepsTheExpressionAndOnlyRefreshesItsSleepDeadline()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        Advance(clock, controller, 100);
        Advance(clock, controller, 500);
        var token = controller.Player.Token;
        controller.Hover();
        Assert.Equal(token, controller.Player.Token);
        Assert.Equal("yawn", controller.Player.CurrentClip!.Name);
        Assert.Equal(600, controller.LastPetInteraction);
        Advance(clock, controller, 840);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
    }

    [Fact]
    public void SleepDeadlineIsNotPostponedByExpressionsAndStartsAtTheirRestingEndpoint()
    {
        var clock = new FakeClock();
        var controller = Create(clock, settings: new PetSettings { BlinkMinMs = 100, BlinkMaxMs = 100, SleepAfterMs = 250 });
        Advance(clock, controller, 100);
        Advance(clock, controller, 150);
        Assert.Equal("yawn", controller.Player.CurrentClip!.Name);
        Assert.Equal(250, controller.SleepDeadline);
        Advance(clock, controller, 1190);
        Assert.Equal("fall_asleep", controller.Player.CurrentClip!.Name);
        Assert.Equal(PetState.FallAsleep, controller.State);
        Assert.Equal(0, controller.LastPetInteraction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DragOrSuspensionCancelsAPendingClickWithoutLaterRising(bool suspended)
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        Advance(clock, controller, 100);
        Advance(clock, controller, 500);
        controller.Click();
        var token = controller.Player.Token;
        if (suspended) controller.SetSuspended(true);
        else controller.BeginDrag();
        var visible = controller.Player.CurrentFrame;
        Advance(clock, controller, 10000);
        Assert.Same(visible, controller.Player.CurrentFrame);
        if (suspended) controller.SetSuspended(false);
        else controller.EndDrag();
        controller.HandleAnimationCompleted(new(token - 1, "yawn"));
        Advance(clock, controller, 240);
        Assert.Equal(PetState.Idle, controller.State);
        Assert.Equal(ReactionPhase.None, controller.ReactionPhase);
    }

    [Fact]
    public void PausingAutomaticActionsLetsTheCurrentExpressionFinishButStartsNoOthers()
    {
        var clock = new FakeClock();
        var controller = Create(clock);
        Advance(clock, controller, 100);
        controller.SetAutomaticActionsPaused(true);
        Advance(clock, controller, 1340);
        Advance(clock, controller, 100000);
        Assert.Equal("idle", controller.Player.CurrentClip!.Name);
        Assert.Equal(long.MaxValue, controller.NextUpdateInMs);
        controller.Click();
        Assert.Equal("rise", controller.Player.CurrentClip!.Name);
    }
}
