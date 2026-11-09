using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class PresentationCadenceTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void StablePresentationTicksIgnoreCpuCallbackJitter(int fps)
    {
        var clock = new PreciseClock { PreciseMilliseconds = 100.125 };
        var player = new AnimationPlayer(clock);
        player.Play(Clip(fps, fps * 2));
        for (var sample = 0; sample < fps * 2; sample++)
        {
            var jitter = sample == 0 ? 0 : sample % 2 == 0 ? -0.8 : 0.7;
            clock.PreciseMilliseconds = 100.125 + sample * 1000d / fps + jitter;
            Present(player, TimeSpan.FromTicks(12345 + (long)(sample * (double)TimeSpan.TicksPerSecond / fps)));
            Assert.Equal(sample, player.FrameIndex);
        }
    }

    [Fact]
    public void CompletionStartedClipUsesTheSamePresentationAsItsZeroPoint()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 3));
        player.Completed += _ => player.Play(Clip(60, 60));
        Present(player, TimeSpan.Zero);
        clock.PreciseMilliseconds = 50.7;
        Present(player, TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, player.Token);
        Assert.Equal(0, player.FrameIndex);
        clock.PreciseMilliseconds = 66;
        Present(player, TimeSpan.FromTicks(666666));
        Assert.Equal(1, player.FrameIndex);
    }

    [Fact]
    public void BehaviorForwardsPresentationTimeWithoutReplacingItsRealClock()
    {
        var clock = new PreciseClock();
        var clips = new[] { "idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle" }
            .ToDictionary(name => name, name => Clip(60, 120) with { Name = name, Loop = name == "sleep_loop" });
        clips["idle"] = new("idle", [new("rest", 1000)], true);
        var behavior = new BehaviorController(clock, clips, new PetSettings { SleepAfterMs = 100 });
        behavior.Tick(TimeSpan.Zero);
        clock.PreciseMilliseconds = 99;
        behavior.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal(PetState.Idle, behavior.State);
        clock.PreciseMilliseconds = 100;
        behavior.Tick();
        Assert.Equal(PetState.FallAsleep, behavior.State);
        behavior.Tick(TimeSpan.FromSeconds(10));
        for (var sample = 1; sample < 60; sample++)
        {
            clock.PreciseMilliseconds = 100 + sample * 1000d / 60 + (sample % 2 == 0 ? -0.8 : 0.7);
            behavior.Tick(TimeSpan.FromTicks(100000000 + (long)(sample * (double)TimeSpan.TicksPerSecond / 60)));
            Assert.Equal(sample, behavior.Player.FrameIndex);
        }
    }

    [Fact]
    public void FrameObserverStartedClipAlsoInheritsTheCurrentPresentation()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 60));
        player.FrameChanged += frame =>
        {
            if (player.Token == 1 && frame.File == "1") player.Play(Clip(60, 60));
        };
        Present(player, TimeSpan.Zero);
        clock.PreciseMilliseconds = 17.4;
        Present(player, TimeSpan.FromTicks(166666));
        Assert.Equal(2, player.Token);
        clock.PreciseMilliseconds = 32.6;
        Present(player, TimeSpan.FromTicks(333333));
        Assert.Equal(1, player.FrameIndex);
    }

    [Fact]
    public void PauseBetweenJitteredCallbacksCannotRewindOrConsumePausedTime()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 180));
        Present(player, TimeSpan.Zero);
        clock.PreciseMilliseconds = 32.533;
        Present(player, TimeSpan.FromTicks(333333));
        Assert.Equal(2, player.FrameIndex);
        clock.PreciseMilliseconds += 0.1;
        player.Pause();
        clock.PreciseMilliseconds += 1000;
        player.Resume();
        clock.PreciseMilliseconds += 0.1;
        Present(player, TimeSpan.FromTicks(10350000));
        Assert.Equal(2, player.FrameIndex);
        clock.PreciseMilliseconds += 16;
        Present(player, TimeSpan.FromTicks(10516666));
        Assert.Equal(3, player.FrameIndex);
    }

    [Fact]
    public void DuplicateAndBackwardPresentationTimesAreSafeAndLongGapsStillSkip()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 180));
        Present(player, TimeSpan.FromMilliseconds(100));
        clock.PreciseMilliseconds = 1000;
        Present(player, TimeSpan.FromMilliseconds(1100));
        Assert.Equal(60, player.FrameIndex);
        clock.PreciseMilliseconds += 20;
        Present(player, TimeSpan.FromMilliseconds(1100));
        Assert.Equal(60, player.FrameIndex);
        Present(player, TimeSpan.Zero);
        Assert.Equal(61, player.FrameIndex);
        clock.PreciseMilliseconds += 20;
        player.Tick();
        Assert.Equal(62, player.FrameIndex);
        Present(player, TimeSpan.FromMilliseconds(5));
        Assert.Equal(62, player.FrameIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationTickToleranceDoesNotFinishOrLoopBeforeTheEnd(bool loop)
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 3) with { Loop = loop });
        Present(player, TimeSpan.Zero);
        clock.PreciseMilliseconds = 49.9999;
        Present(player, TimeSpan.FromTicks(499999));
        Assert.Equal(2, player.FrameIndex);
        Assert.False(player.IsCompleted);
        clock.PreciseMilliseconds = 50;
        Present(player, TimeSpan.FromTicks(500000));
        Assert.Equal(loop ? 0 : 2, player.FrameIndex);
        Assert.Equal(!loop, player.IsCompleted);
    }

    [Fact]
    public void PresentedElapsedIsAStableSnapshotAcrossWallTimePauseAndDuplicateCallbacks()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 180));
        Assert.Equal(0, Presented(player));
        Present(player, TimeSpan.Zero);
        clock.PreciseMilliseconds = 17.4;
        Present(player, TimeSpan.FromTicks(166666));
        Assert.Equal(16.6666, Presented(player), 6);
        clock.PreciseMilliseconds += 25;
        Assert.Equal(16.6666, Presented(player), 6);
        Present(player, TimeSpan.FromTicks(166666));
        Assert.Equal(16.6666, Presented(player), 6);
        player.Pause();
        clock.PreciseMilliseconds += 1000;
        Present(player, TimeSpan.FromSeconds(1));
        Assert.Equal(16.6666, Presented(player), 6);
    }

    [Fact]
    public void FrameObserverSeesThePresentationSnapshotAndNewPlaybackResetsIt()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 60));
        player.FrameChanged += frame =>
        {
            if (player.Token == 1 && frame.File == "1")
            {
                Assert.Equal(16.6666, Presented(player), 6);
                player.Play(Clip(60, 60));
            }
        };
        Present(player, TimeSpan.Zero);
        clock.PreciseMilliseconds = 17.3;
        Present(player, TimeSpan.FromTicks(166666));
        Assert.Equal(2, player.Token);
        Assert.Equal(0, Presented(player));
    }

    [Fact]
    public void NestedPresentationRestoresTheOuterContextBeforeStartingANewClip()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(Clip(60, 60));
        player.FrameChanged += frame =>
        {
            if (player.Token != 1 || frame.File != "1") return;
            player.Tick(TimeSpan.FromMilliseconds(200));
            player.Play(Clip(60, 60));
        };
        player.Tick(TimeSpan.Zero);
        clock.PreciseMilliseconds = 16.7;
        player.Tick(TimeSpan.FromTicks(166666));
        clock.PreciseMilliseconds = 17;
        player.Tick(TimeSpan.FromTicks(333333));
        Assert.Equal(1, player.FrameIndex);
    }

    private static double Presented(AnimationPlayer player) => player.PresentedElapsedMs;

    private static AnimationClip Clip(int fps, int count) => new("sampled", Enumerable.Range(0, count).Select(i =>
        new AnimationFrame($"{i}", (int)Math.Round((i + 1) * 1000d / fps) - (int)Math.Round(i * 1000d / fps))).ToArray(), SampleFps: fps);

    private static void Present(AnimationPlayer player, TimeSpan time)
    {
        player.Tick(time);
    }

    private sealed class PreciseClock : IClock
    {
        public double PreciseMilliseconds { get; set; }
        public long NowMilliseconds => (long)PreciseMilliseconds;
    }
}
