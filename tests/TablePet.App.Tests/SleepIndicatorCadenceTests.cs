using System.Windows.Media;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class SleepIndicatorCadenceTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void LettersAdvanceOnEveryPresentationDespiteCallbackJitter(int refreshHz)
    {
        Sta.Run(() =>
        {
            var clock = new PreciseClock { PreciseMilliseconds = 200.125 };
            var player = new AnimationPlayer(clock);
            var indicator = new SleepIndicatorVisual();
            player.Play(SleepClip());
            player.Tick(TimeSpan.Zero);
            var positions = new List<double>();
            for (var sample = 1; sample <= refreshHz; sample++)
            {
                var presentation = TimeSpan.FromTicks((long)(sample * (double)TimeSpan.TicksPerSecond / refreshHz));
                clock.PreciseMilliseconds = 200.125 + presentation.TotalMilliseconds + (sample % 2 == 0 ? -.8 : .7);
                player.Tick(presentation);
                // PNG fetches and other overlays may take time before the Z layer
                // is updated; their CPU time must not change this presented pose.
                clock.PreciseMilliseconds += .05;
                Update(indicator, player);
                if (presentation.TotalMilliseconds < 200) continue;
                var (x, opacity) = FirstLetter(indicator);
                positions.Add(x);
                if (presentation.TotalMilliseconds < 400)
                {
                    var t = (presentation.TotalMilliseconds - 100) / 300;
                    Assert.Equal(.94 * t * t * (3 - 2 * t), opacity, 8);
                }
            }
            var expectedStep = 5 * (1000d / refreshHz) / 3200;
            Assert.True(positions.Count > refreshHz / 2);
            for (var i = 1; i < positions.Count; i++)
                Assert.InRange(positions[i] - positions[i - 1], expectedStep - .000001, expectedStep + .000001);
        });
    }

    [Fact]
    public void PausedOrRepeatedPresentationsFreezeTheActualLetterTransforms()
    {
        Sta.Run(() =>
        {
            var clock = new PreciseClock();
            var player = new AnimationPlayer(clock);
            var indicator = new SleepIndicatorVisual();
            player.Play(SleepClip());
            player.Tick(TimeSpan.Zero);
            clock.PreciseMilliseconds = 299.2;
            player.Tick(TimeSpan.FromMilliseconds(300));
            Update(indicator, player);
            var displayed = FirstLetter(indicator);

            clock.PreciseMilliseconds += 20;
            player.Tick(TimeSpan.FromMilliseconds(300)); // Duplicate composition pass.
            Update(indicator, player);
            Assert.Equal(displayed, FirstLetter(indicator));

            player.Pause(); // Dragging uses this same pause path.
            clock.PreciseMilliseconds += 5000;
            player.Tick(TimeSpan.FromMilliseconds(5300));
            Update(indicator, player);
            Assert.Equal(displayed, FirstLetter(indicator));
            player.Resume();
            Update(indicator, player); // No new presentation yet.
            Assert.Equal(displayed, FirstLetter(indicator));
            player.Tick(TimeSpan.FromMilliseconds(5300));
            clock.PreciseMilliseconds += 16.6667;
            player.Tick(TimeSpan.FromTicks(53166667));
            Update(indicator, player);
            Assert.True(FirstLetter(indicator).X > displayed.X);
        });
    }

    private static void Update(SleepIndicatorVisual indicator, AnimationPlayer player) =>
        indicator.Update(PetWindow.SleepDecorationTime(player));

    private static (double X, double Opacity) FirstLetter(SleepIndicatorVisual indicator)
    {
        var canvas = (ContainerVisual)VisualTreeHelper.GetChild(indicator, 0);
        var letter = (DrawingVisual)canvas.Children[0];
        var transforms = (TransformGroup)letter.Transform;
        return (transforms.Children.OfType<TranslateTransform>().Single().X, letter.Opacity);
    }

    private static AnimationClip SleepClip() => new("sleep_loop", Enumerable.Range(0, 216).Select(i =>
        new AnimationFrame($"{i}", (int)Math.Round((i + 1) * 1000d / 60) - (int)Math.Round(i * 1000d / 60))).ToArray(),
        Loop: true, SampleFps: 60);

    private sealed class PreciseClock : IClock
    {
        public double PreciseMilliseconds { get; set; }
        public long NowMilliseconds => (long)PreciseMilliseconds;
    }
}
