using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class AnimationPlayerTests
{
    private static AnimationClip Clip(bool loop = false) => new("example", new[]
    {
        new AnimationFrame("a.png", 100), new AnimationFrame("b.png", 200), new AnimationFrame("c.png", 100)
    }, loop);

    [Fact]
    public void DelayedTimerSkipsToCorrectFrameAndCompletesOnlyOnce()
    {
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        var completions = new List<AnimationCompleted>();
        player.Completed += completions.Add;
        player.Play(Clip());
        clock.Advance(350);
        player.Tick();
        Assert.Equal("c.png", player.CurrentFrame!.File);
        Assert.Equal(50, player.NextUpdateInMs);
        clock.Advance(5000);
        player.Tick();
        player.Tick();
        Assert.Single(completions);
    }

    [Fact]
    public void LoopUsesElapsedModuloWithoutDispatchingAccumulatedFrames()
    {
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        var changes = 0;
        player.FrameChanged += _ => changes++;
        player.Play(Clip(true));
        clock.Advance(41050);
        player.Tick();
        Assert.Equal("b.png", player.CurrentFrame!.File);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void PausingDoesNotConsumeTimelineTime()
    {
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip());
        clock.Advance(150);
        player.Tick();
        player.Pause();
        clock.Advance(10000);
        player.Tick();
        Assert.Equal("b.png", player.CurrentFrame!.File);
        Assert.Equal(long.MaxValue, player.NextUpdateInMs);
        player.Resume();
        clock.Advance(149);
        player.Tick();
        Assert.Equal("b.png", player.CurrentFrame!.File);
        clock.Advance(1);
        player.Tick();
        Assert.Equal("c.png", player.CurrentFrame!.File);
    }

    [Fact]
    public void CompletionMayStartNewClipWithoutItBeingMarkedComplete()
    {
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip());
        player.Completed += _ => player.Play(Clip(true));
        clock.Advance(400);
        player.Tick();
        Assert.False(player.IsCompleted);
        Assert.Equal("a.png", player.CurrentFrame!.File);
        Assert.Equal(2, player.Token);
    }

    [Fact]
    public void Dense240FpsTimelineUsesExactExclusiveBoundariesAcrossLongLoops()
    {
        var frames = Enumerable.Range(0, 240).Select(i => new AnimationFrame($"{i}.png",
            (int)Math.Round((i + 1) * 1000d / 240) - (int)Math.Round(i * 1000d / 240))).ToArray();
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        player.Play(new AnimationClip("dense", frames, true));
        for (var ms = 0; ms < 1000; ms++)
        {
            clock.NowMilliseconds = 3600000 + ms;
            player.Tick();
            var end = 0;
            var expected = 0;
            while ((end += frames[expected].DurationMs) <= ms) expected++;
            Assert.Equal(expected, player.FrameIndex);
            Assert.Equal(end - ms, player.NextUpdateInMs);
        }
        clock.NowMilliseconds = 3601000;
        player.Tick();
        Assert.Equal(0, player.FrameIndex);
        Assert.False(player.IsCompleted);
    }

    [Fact]
    public void ContinuousRenderingStopsForStaticPausedAndFinishedClips()
    {
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        Assert.False(player.NeedsContinuousRendering);
        player.Play(new AnimationClip("idle", [new("idle.png", 1000)], true));
        Assert.False(player.NeedsContinuousRendering);
        player.Play(Clip());
        Assert.True(player.NeedsContinuousRendering);
        player.Pause();
        Assert.False(player.NeedsContinuousRendering);
        player.Resume();
        Assert.True(player.NeedsContinuousRendering);
        clock.Advance(400);
        player.Tick();
        Assert.False(player.NeedsContinuousRendering);
    }

    [Fact]
    public void FrameObserverCanReplaceClipWithoutCompletingTheReplacement()
    {
        var clock = new FakeClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip());
        player.FrameChanged += frame => { if (frame.File == "c.png") player.Play(Clip(true)); };
        clock.Advance(1000);
        player.Tick();
        Assert.Equal("a.png", player.CurrentFrame!.File);
        Assert.False(player.IsCompleted);
    }
}
