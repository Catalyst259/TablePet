using System.Text.Json;
using TablePet.Core;
using Xunit;

namespace TablePet.Core.Tests;

public sealed class DynamicTransitionCadenceTests
{
    private static readonly IReadOnlyDictionary<string, AnimationClip> Runtime = ReadRuntime();

    [Theory]
    [InlineData("sleep_to_wake")]
    [InlineData("work_sleep_to_wake")]
    [InlineData("hold_to_settle")]
    [InlineData("idle_to_react")]
    public void ControllerGeneratedReturnsPreserveEvenCadenceAcrossEveryInterruptionPhase(string route)
    {
        var source = Runtime[route == "idle_to_react" ? "yawn" : "sleep_loop"];
        var failures = new List<string>();
        var reached = 0;
        for (var phase = 0; phase < source.Frames.Count; phase++)
        {
            var (controller, clock) = EnterReturn(route, phase);
            var clip = controller.Player.CurrentClip!;
            if (clip.Name != route) continue; // The base pose needs no bridge.
            reached++;
            var start = clock.PreciseMilliseconds;
            controller.Tick(TimeSpan.Zero);
            for (var refresh = 1; refresh < clip.Frames.Count; refresh++)
            {
                // Observe the generated clip at its own uniform presentation rate,
                // without reconstructing its route or choosing its frame count.
                var elapsed = refresh * (double)clip.DurationMs / clip.Frames.Count;
                clock.PreciseMilliseconds = start + elapsed + (refresh % 2 == 0 ? -0.5 : 0.5);
                controller.Tick(TimeSpan.FromTicks((long)(elapsed * TimeSpan.TicksPerMillisecond)));
                if (controller.Player.FrameIndex != refresh)
                {
                    failures.Add($"phase {phase}: {clip.Frames.Count} slots/{clip.DurationMs} ms, refresh {refresh} showed {controller.Player.FrameIndex}");
                    break;
                }
            }
        }
        Assert.True(reached > 0, $"The production controller never reached {route}.");
        Assert.True(failures.Count == 0, $"{route}: {failures.Count}/{reached} interruption phases stuttered. " + string.Join("; ", failures.Take(8)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControllerReversalsKeepCadenceAtEveryDisplayedFallingAsleepPhase(bool work)
    {
        for (var phase = 1; phase < Runtime["fall_asleep"].Frames.Count; phase++)
        {
            var clock = new PreciseClock();
            var controller = Create(clock);
            Advance(clock, controller, 1);
            Advance(clock, controller, phase * 1000d / 60 + 0.001);
            var visible = controller.Player.CurrentFrame!.File;
            if (work) controller.ApplySettings(controller.Settings with { Mode = CompanionMode.Work });
            else controller.Hover();
            Assert.Equal(work ? "work_sleep_return" : "return_to_idle", controller.Player.CurrentClip!.Name);
            Assert.Equal(visible, controller.Player.CurrentFrame!.File);
            var count = controller.Player.CurrentClip.Frames.Count;
            var start = clock.PreciseMilliseconds;
            controller.Tick(TimeSpan.Zero);
            for (var refresh = 1; refresh < count; refresh++)
            {
                clock.PreciseMilliseconds = start + refresh * 1000d / 60;
                controller.Tick(TimeSpan.FromTicks((long)(refresh * (double)TimeSpan.TicksPerSecond / 60)));
                Assert.Equal(refresh, controller.Player.FrameIndex);
            }
        }
    }

    [Fact]
    public void SleepDecisionInsidePresentationStartsTheNextClipOnThatPresentation()
    {
        var clock = new PreciseClock();
        var controller = Create(clock, new PetSettings { SleepAfterMs = 100, BlinkMinMs = 10000, BlinkMaxMs = 10000 });
        clock.PreciseMilliseconds = 100.7;
        controller.Tick(TimeSpan.FromMilliseconds(100));
        Assert.Equal("fall_asleep", controller.Player.CurrentClip!.Name);
        clock.PreciseMilliseconds = 116;
        controller.Tick(TimeSpan.FromTicks(1166666));
        Assert.Equal(1, controller.Player.FrameIndex);
    }

    [Fact]
    public void WorkDeadlineInsidePresentationStartsExitOnThatPresentation()
    {
        var clock = new PreciseClock();
        var controller = Create(clock, new PetSettings { Mode = CompanionMode.Work, WorkDurationMs = 100 });
        Assert.True(controller.CanWork);
        Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Advance(clock, controller, controller.Player.CurrentClip!.DurationMs);
        Assert.Equal(WorkPhase.Working, controller.WorkPhase);
        var workStart = clock.PreciseMilliseconds;
        clock.PreciseMilliseconds = workStart + 100.7;
        controller.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal("work_exit", controller.Player.CurrentClip!.Name);
        clock.PreciseMilliseconds = workStart + 116;
        controller.Tick(TimeSpan.FromTicks(100166666));
        Assert.Equal(1, controller.Player.FrameIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BehaviorPresentationScopeDoesNotLeakIntoLaterInput(bool throwFromObserver)
    {
        var clock = new PreciseClock();
        var controller = Create(clock);
        var shouldThrow = throwFromObserver;
        controller.Player.FrameChanged += _ =>
        {
            if (shouldThrow && controller.State == PetState.FallAsleep)
            {
                shouldThrow = false;
                throw new InvalidOperationException("Observer failure used to verify scope cleanup.");
            }
        };
        clock.PreciseMilliseconds = 1;
        if (throwFromObserver) Assert.Throws<InvalidOperationException>(() => controller.Tick(TimeSpan.FromMilliseconds(100)));
        else controller.Tick(TimeSpan.FromMilliseconds(100));
        clock.PreciseMilliseconds += 0.1;
        controller.Hover();
        Assert.Equal("return_to_idle", controller.Player.CurrentClip!.Name);
        controller.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal("return_to_idle", controller.Player.CurrentClip!.Name);
        Assert.False(controller.Player.IsCompleted);
        Assert.Equal(0, controller.Player.PresentedElapsedMs, 6);
    }

    private static (BehaviorController Controller, PreciseClock Clock) EnterReturn(string route, int phase)
    {
        var clock = new PreciseClock();
        var clips = Runtime.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (route == "idle_to_react")
        {
            var controller = Create(clock, new PetSettings { AutoSleep = false, BlinkMinMs = 1, BlinkMaxMs = 1 }, clips, [new("yawn", 1000)]);
            Advance(clock, controller, 1);
            Assert.Equal("yawn", controller.Player.CurrentClip!.Name);
            Advance(clock, controller, phase * 1000d / 60 + 0.001);
            controller.Click();
            return (controller, clock);
        }
        if (route == "hold_to_settle")
        {
            // The shipped hold is static; animated-hold compatibility uses the
            // shipped breathing timeline to exercise this real controller route.
            clips["hold"] = clips["sleep_loop"] with { Name = "hold" };
            clips.Remove("meow"); clips.Remove("wink");
            var holdMs = (int)Math.Floor(phase * 1000d / 60) + 1;
            var controller = Create(clock, new PetSettings { AutoSleep = false, HoldMs = holdMs }, clips);
            controller.Click();
            Advance(clock, controller, clips["rise"].DurationMs);
            Advance(clock, controller, holdMs);
            return (controller, clock);
        }
        else
        {
            var controller = Create(clock);
            Advance(clock, controller, 1);
            Advance(clock, controller, clips["fall_asleep"].DurationMs);
            Assert.Equal(PetState.Sleep, controller.State);
            Advance(clock, controller, phase * 1000d / 60 + 0.001);
            if (route == "work_sleep_to_wake") controller.ApplySettings(controller.Settings with { Mode = CompanionMode.Work });
            else controller.Hover();
            return (controller, clock);
        }
    }

    private static BehaviorController Create(PreciseClock clock, PetSettings? settings = null,
        IReadOnlyDictionary<string, AnimationClip>? clips = null, IEnumerable<IdleAction>? actions = null) =>
        new(clock, clips ?? Runtime, settings ?? new PetSettings { SleepAfterMs = 1, BlinkMinMs = 10000, BlinkMaxMs = 10000 },
            (_, max) => max, actions);

    private static void Advance(PreciseClock clock, BehaviorController controller, double elapsed)
    {
        clock.PreciseMilliseconds += elapsed;
        controller.Tick();
    }

    private static IReadOnlyDictionary<string, AnimationClip> ReadRuntime()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "assets", "strawberry-cat", "manifest.json"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("The runtime manifest is required for transition cadence regression tests.");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "assets", "strawberry-cat", "manifest.json")));
        var root = document.RootElement;
        var sampling = root.GetProperty("sampling");
        return root.GetProperty("clips").EnumerateObject().ToDictionary(property => property.Name, property =>
        {
            var fps = sampling.GetProperty("targetFps").GetInt32();
            if (sampling.TryGetProperty("clipOverrides", out var overrides) && overrides.TryGetProperty(property.Name, out var clipOverride)
                && clipOverride.TryGetProperty("targetFps", out var target)) fps = target.GetInt32();
            var definition = property.Value;
            return new AnimationClip(property.Name, definition.GetProperty("frames").EnumerateArray().Select(frame =>
                new AnimationFrame(frame.GetProperty("file").GetString()!, frame.GetProperty("durationMs").GetInt32())).ToArray(),
                definition.TryGetProperty("loop", out var loop) && loop.GetBoolean(), fps);
        });
    }

    private sealed class PreciseClock : IClock
    {
        public double PreciseMilliseconds { get; set; }
        public long NowMilliseconds => (long)PreciseMilliseconds;
    }
}
