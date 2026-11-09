using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class WorkAssetTests
{
    private static readonly string[] DailyClips = ["idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle"];
    private static readonly string[] WorkClips = ["work_enter", "work_loop", "work_exit", "work_props"];

    [Fact]
    public void ProductionWorkLayersDecodeAndJoinTheOriginalIdleWithoutFallback() => Sta.Run(() =>
    {
        var catalog = new AssetCatalog(FindProductionAssets());
        try
        {
            Assert.False(catalog.IsFallback);
            Assert.False(catalog.HasOptionalActionFallback);
            var enter = catalog.Clips["work_enter"];
            var loop = catalog.Clips["work_loop"];
            var exit = catalog.Clips["work_exit"];
            var props = catalog.Clips["work_props"];
            Assert.Equal(400, enter.DurationMs);
            Assert.Equal(400, exit.DurationMs);
            Assert.Equal(9600, loop.DurationMs);
            Assert.Equal(24, enter.Frames.Count);
            Assert.Equal(24, exit.Frames.Count);
            Assert.Equal(576, loop.Frames.Count);
            Assert.False(enter.Loop);
            Assert.False(exit.Loop);
            Assert.True(loop.Loop);
            Assert.True(props.Loop);
            Assert.Equal(17, Assert.Single(props.Frames).DurationMs);
            Assert.All(WorkClips.SelectMany(name => catalog.Clips[name].Frames), frame => Assert.Contains(frame.DurationMs, new[] { 16, 17 }));

            var idle = catalog.Clips["idle"].Frames[0].File;
            var focused = loop.Frames[0].File;
            Assert.Equal(idle, enter.Frames[0].File);
            Assert.Equal(focused, enter.Frames[^1].File);
            Assert.Equal(focused, exit.Frames[0].File);
            Assert.Equal(idle, exit.Frames[^1].File);
            Assert.Equal(focused, loop.Frames[^1].File);
            Assert.NotEqual(idle, focused);
            Assert.NotEqual(focused, props.Frames[0].File);
            Assert.DoesNotContain(catalog.IdleActions, action => WorkClips.Contains(action.Clip));

            catalog.CacheForSize(512, 1);
            foreach (var file in WorkClips.SelectMany(name => catalog.Clips[name].Frames).Select(frame => frame.File).Distinct())
            {
                var decoded = catalog.Image(file);
                Assert.True(decoded.IsFrozen);
                Assert.Equal(512, decoded.PixelWidth);
                Assert.Equal(512, decoded.PixelHeight);
            }
            var propsPixels = Pixels(catalog.Image(props.Frames[0].File));
            Assert.Contains(Enumerable.Range(0, propsPixels.Length / 4), pixel => propsPixels[pixel * 4 + 3] == 0);
            Assert.Contains(Enumerable.Range(0, propsPixels.Length / 4), pixel => propsPixels[pixel * 4 + 3] == 255);
            Assert.False(Pixels(catalog.Image(idle)).SequenceEqual(Pixels(catalog.Image(focused))));
            Assert.False(catalog.HasFrameFallback);
            Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);

            var behavior = new BehaviorController(new TestClock(), catalog.Clips, new PetSettings { Mode = CompanionMode.Work });
            Assert.True(behavior.CanWork);
            Assert.Equal(WorkPhase.AnnouncingWork, behavior.WorkPhase);
            Assert.Equal("meow", behavior.Player.CurrentClip!.Name);
        }
        finally { catalog.StopPrefetch(); }
    });

    [Theory]
    [InlineData("absent")]
    [InlineData("missing-props")]
    [InlineData("missing-frame")]
    [InlineData("wrong-duration")]
    [InlineData("wrong-endpoint")]
    [InlineData("looping-entry")]
    [InlineData("multiple-props-frames")]
    public void MissingOrIncompleteWorkPackPreservesDailyActions(string defect) => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        WriteFixture(directory, defect);
        var catalog = new AssetCatalog(directory.Root);
        try
        {
            Assert.False(catalog.IsFallback);
            Assert.Equal(defect != "absent", catalog.HasOptionalActionFallback);
            Assert.All(DailyClips, name => Assert.True(catalog.Clips.ContainsKey(name)));
            Assert.All(WorkClips, name => Assert.False(catalog.Clips.ContainsKey(name)));
            Assert.Equal("blink", Assert.Single(catalog.IdleActions).Clip);
            var behavior = new BehaviorController(new TestClock(), catalog.Clips, new PetSettings { Mode = CompanionMode.Work });
            Assert.False(behavior.CanWork);
            Assert.Equal(CompanionMode.Daily, behavior.Mode);
            Assert.Equal(WorkPhase.None, behavior.WorkPhase);
            Assert.Equal(PetState.Idle, behavior.State);
            behavior.DoubleClick();
            Assert.Equal(ReactionPhase.Meow, behavior.ReactionPhase);
            Assert.Equal("meow", behavior.Player.CurrentClip!.Name);
            catalog.CacheForSize(240, 1);
            Assert.True(catalog.Image(behavior.Player.CurrentFrame!.File).IsFrozen);
            Assert.False(catalog.HasFrameFallback);
        }
        finally { catalog.StopPrefetch(); }
    });

    [Theory]
    [InlineData(120)]
    [InlineData(240)]
    [InlineData(480)]
    public void WorkPresentationMovesOnlyCatAndRestoresDailyLayers(int displayedHeight) => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        WriteFixture(directory, "valid");
        var catalog = new AssetCatalog(directory.Root);
        try
        {
            catalog.CacheForSize(displayedHeight, 1);
            var clock = new TestClock();
            var behavior = new BehaviorController(clock, catalog.Clips,
                new PetSettings { Mode = CompanionMode.Work, AutoSleep = false, WorkDurationMs = 30000, BreakDurationMs = 5000 });
            var catOffset = new TranslateTransform();
            var propsImage = new Image();

            clock.NowMilliseconds = 300;
            behavior.Tick();
            Assert.Equal(WorkPhase.Entering, behavior.WorkPhase);
            clock.NowMilliseconds = 500;
            behavior.Tick();
            WorkScenePresentation.Apply(catOffset, propsImage, behavior, catalog, displayedHeight);
            Assert.Equal(0, catOffset.Y);
            Assert.Equal(.5, propsImage.Opacity, 10);
            Assert.Equal(Visibility.Visible, propsImage.Visibility);
            Assert.NotNull(propsImage.Source);
            Assert.True(propsImage.RenderTransform.Value.IsIdentity);

            clock.NowMilliseconds = 700;
            behavior.Tick();
            Assert.Equal(WorkPhase.Working, behavior.WorkPhase);
            clock.NowMilliseconds = 3100; // 2400 ms into the 4800 ms breathing cycle.
            behavior.Tick();
            WorkScenePresentation.Apply(catOffset, propsImage, behavior, catalog, displayedHeight);
            Assert.Equal(-2d * displayedHeight / 512, catOffset.Y, 10);
            Assert.Equal(1, propsImage.Opacity);
            Assert.True(propsImage.RenderTransform.Value.IsIdentity);
            var stationaryPropsSource = propsImage.Source;

            behavior.ApplySettings(behavior.Settings with { Mode = CompanionMode.Daily });
            Assert.Equal(WorkPhase.Exiting, behavior.WorkPhase);
            clock.NowMilliseconds = 3300;
            behavior.Tick();
            WorkScenePresentation.Apply(catOffset, propsImage, behavior, catalog, displayedHeight);
            Assert.Equal(.5, propsImage.Opacity, 10);
            Assert.Same(stationaryPropsSource, propsImage.Source);
            Assert.True(propsImage.RenderTransform.Value.IsIdentity);
            Assert.Equal(behavior.WorkBodyOffsetY * displayedHeight / 512, catOffset.Y, 10);
            clock.NowMilliseconds = 3500;
            behavior.Tick();
            WorkScenePresentation.Apply(catOffset, propsImage, behavior, catalog, displayedHeight);
            Assert.Equal(WorkPhase.None, behavior.WorkPhase);
            Assert.Equal(PetState.Idle, behavior.State);
            Assert.Equal(0, catOffset.Y);
            Assert.Equal(0, propsImage.Opacity);
            Assert.Equal(Visibility.Collapsed, propsImage.Visibility);
            Assert.False(catalog.HasFrameFallback);
        }
        finally { catalog.StopPrefetch(); }
    });

    private static string FindProductionAssets()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "strawberry-cat");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find the repository's production strawberry-cat assets above the test output.");
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

    private static void WriteFixture(TestDirectory directory, string defect)
    {
        foreach (var file in new[] { "idle.png", "focused.png", "reaction.png", "props.png" }) WritePng(directory.FilePath(file));
        object Clip(bool loop, params (string File, int Duration)[] frames) => new
        {
            loop, frames = frames.Select(frame => new { file = frame.File, durationMs = frame.Duration }).ToArray()
        };
        var clips = DailyClips.ToDictionary(name => name, name => Clip(name is "idle" or "sleep_loop" or "hold", ("idle.png", 100)));
        foreach (var name in new[] { "meow", "wink", "yawn" })
            clips[name] = Clip(false, ("idle.png", 100), ("reaction.png", 100), ("idle.png", 100));
        if (defect != "absent")
        {
            // Fixtures use only two endpoint poses; the production test above
            // separately checks the full 60 Hz timeline and every decoded PNG.
            clips["work_enter"] = Clip(defect == "looping-entry", ("idle.png", 200), ("focused.png", defect == "wrong-duration" ? 201 : 200));
            clips["work_loop"] = Clip(true, ("focused.png", 4800), ("focused.png", 4800));
            clips["work_exit"] = Clip(false, (defect == "wrong-endpoint" ? "idle.png" : "focused.png", 200), ("idle.png", 200));
            if (defect != "missing-props")
                clips["work_props"] = defect == "multiple-props-frames"
                    ? Clip(true, ("props.png", 17), ("props.png", 17))
                    : Clip(true, (defect == "missing-frame" ? "absent.png" : "props.png", 17));
        }
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
        {
            canvas = new { width = 512, height = 512 }, clips
        }));
    }

    private static void WritePng(string path)
    {
        var pixels = new byte[512 * 512 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 80; pixels[i + 1] = 100; pixels[i + 2] = 120; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 512 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private sealed class TestClock : IClock
    {
        public long NowMilliseconds { get; set; }
    }
}
