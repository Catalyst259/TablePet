using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class AnimationCadenceTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void SampledAnimationAdvancesOncePerRefreshAtEveryStartingPhase(int fps)
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        var clip = SampledClip(fps, fps * 2);
        var period = 1000d / fps;
        for (var phase = 0; phase < 1000; phase++)
        {
            const double start = 1234.375;
            clock.PreciseMilliseconds = start;
            player.Play(clip);
            for (var sample = 0; sample < fps * 2 - 1; sample++)
            {
                clock.PreciseMilliseconds = start + (sample + phase / 1000d) * period;
                player.Tick();
                Assert.True(player.FrameIndex == sample,
                    $"{fps} Hz phase {phase}/1000, refresh {sample}: frame {player.FrameIndex}");
            }
        }
    }

    [Fact]
    public void SampledClipCompletesAndLoopsOnlyAtItsExactDuration()
    {
        var clock = new PreciseClock { PreciseMilliseconds = 0.125 };
        var player = new AnimationPlayer(clock);
        var clip = SampledClip(120, 3);
        player.Play(clip);
        clock.PreciseMilliseconds += 24.999;
        player.Tick();
        Assert.Equal(2, player.FrameIndex);
        Assert.False(player.IsCompleted);
        Assert.Equal(1, player.NextUpdateInMs);
        clock.PreciseMilliseconds = 25.125;
        player.Tick();
        Assert.True(player.IsCompleted);

        player.Play(clip with { Loop = true });
        clock.PreciseMilliseconds += 24.999;
        player.Tick();
        Assert.Equal(2, player.FrameIndex);
        clock.PreciseMilliseconds = 50.125;
        player.Tick();
        Assert.Equal(0, player.FrameIndex);
        Assert.False(player.IsCompleted);
    }

    [Fact]
    public void PauseRetainsFractionalElapsedTimeAndLateRefreshStillSkipsFrames()
    {
        var clock = new PreciseClock { PreciseMilliseconds = 0.125 };
        var player = new AnimationPlayer(clock);
        player.Play(SampledClip(120, 120));
        clock.PreciseMilliseconds += 7.75;
        player.Tick();
        player.Pause();
        clock.PreciseMilliseconds += 1000.25;
        player.Resume();
        clock.PreciseMilliseconds += 0.6;
        player.Tick();
        Assert.Equal(1, player.FrameIndex);
        Assert.Equal(8, player.ElapsedMs);
        clock.PreciseMilliseconds += 250;
        player.Tick();
        Assert.Equal(31, player.FrameIndex);
    }

    [Fact]
    public void LegacyVariableTimingKeepsItsExplicitBoundaries()
    {
        var clock = new PreciseClock { PreciseMilliseconds = 0.25 };
        var player = new AnimationPlayer(clock);
        player.Play(new("variable", [new("a", 17), new("b", 80), new("c", 4)]));
        clock.PreciseMilliseconds += 16.999;
        player.Tick();
        Assert.Equal(0, player.FrameIndex);
        clock.PreciseMilliseconds = 17.25;
        player.Tick();
        Assert.Equal(1, player.FrameIndex);
        clock.PreciseMilliseconds = 97.25;
        player.Tick();
        Assert.Equal(2, player.FrameIndex);
    }

    [Fact]
    public void DeclaredSamplingDoesNotOverrideAnIncompatibleVariableTimeline()
    {
        var clip = SampledClip(120, 3) with { Frames = [new("a", 17), new("b", 80), new("c", 4)] };
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(clip);
        clock.PreciseMilliseconds = 16.9;
        player.Tick();
        Assert.Equal(0, player.FrameIndex);
        clock.PreciseMilliseconds = 97;
        player.Tick();
        Assert.Equal(2, player.FrameIndex);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void ReversingAnInterruptedSampledClipRetainsEveryPoseAndItsCadence(int fps)
    {
        var clock = new PreciseClock { PreciseMilliseconds = 0.375 };
        var player = new AnimationPlayer(clock);
        player.Play(SampledClip(fps, fps));
        clock.PreciseMilliseconds += 42 * 1000d / fps;
        player.Tick();
        Assert.Equal(42, player.FrameIndex);
        player.ReverseToStart();
        Assert.Equal(fps, player.CurrentClip!.SampleFps);
        Assert.Equal(43, player.CurrentClip.Frames.Count);
        Assert.Equal("42", player.CurrentFrame!.File);
        var start = clock.PreciseMilliseconds;
        for (var i = 1; i < 43; i++)
        {
            clock.PreciseMilliseconds = start + i * 1000d / fps;
            player.Tick();
            Assert.Equal((42 - i).ToString(), player.CurrentFrame!.File);
        }
    }

    [Fact]
    public void PartialFinalSamplingSlotKeepsTheDeclaredEndTime()
    {
        var frames = SampledClip(60, 23).Frames.ToArray();
        frames[^1] = frames[^1] with { DurationMs = 8 };
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(new("short-tail", frames, SampleFps: 60));
        clock.PreciseMilliseconds = 366.667;
        player.Tick();
        Assert.Equal(22, player.FrameIndex);
        clock.PreciseMilliseconds = 374.999;
        player.Tick();
        Assert.False(player.IsCompleted);
        clock.PreciseMilliseconds = 375;
        player.Tick();
        Assert.True(player.IsCompleted);
    }

    [Fact]
    public void LegacyDenseReversalRetainsItsGeneratedUniformCadence()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(new("legacy", Enumerable.Range(0, 120).Select(i => new AnimationFrame($"{i}", 4)).ToArray()));
        clock.PreciseMilliseconds = 476;
        player.Tick();
        player.ReverseToStart();
        var clip = player.CurrentClip!;
        player.Tick(TimeSpan.Zero);
        for (var refresh = 1; refresh < clip.Frames.Count; refresh++)
        {
            var elapsed = refresh * (double)clip.DurationMs / clip.Frames.Count;
            clock.PreciseMilliseconds = 476 + elapsed + (refresh % 2 == 0 ? -0.5 : 0.5);
            player.Tick(TimeSpan.FromTicks((long)(elapsed * TimeSpan.TicksPerMillisecond)));
            Assert.Equal(refresh, player.FrameIndex);
        }
    }

    [Fact]
    public void GeneratedUniformDeclarationMustMatchEveryStoredBoundary()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(new AnimationClip("variable", [new("a", 17), new("b", 80), new("c", 4)]) { UniformFrameTiming = true });
        clock.PreciseMilliseconds = 16.9;
        player.Tick();
        Assert.Equal(0, player.FrameIndex);
        clock.PreciseMilliseconds = 97;
        player.Tick();
        Assert.Equal(2, player.FrameIndex);
    }

    [Fact]
    public void GeneratedHalfMillisecondBoundariesAreRestoredFromRoundedStorage()
    {
        var clock = new PreciseClock();
        var player = new AnimationPlayer(clock);
        player.Play(new AnimationClip("uniform", [new("a", 62), new("b", 63), new("c", 63), new("d", 62)])
            { UniformFrameTiming = true });
        clock.PreciseMilliseconds = 62.25;
        player.Tick();
        Assert.Equal(0, player.FrameIndex);
        clock.PreciseMilliseconds = 62.5;
        player.Tick();
        Assert.Equal(1, player.FrameIndex);
    }

    private static AnimationClip SampledClip(int fps, int count)
    {
        return new AnimationClip("sampled", Enumerable.Range(0, count).Select(i =>
            new AnimationFrame($"{i}", (int)Math.Round((i + 1) * 1000d / fps) - (int)Math.Round(i * 1000d / fps))).ToArray(), SampleFps: fps);
    }

    private sealed class PreciseClock : IClock
    {
        public double PreciseMilliseconds { get; set; }
        public long NowMilliseconds => (long)PreciseMilliseconds;
    }
}
