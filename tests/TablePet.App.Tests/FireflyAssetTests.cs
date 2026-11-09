using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class FireflyAssetTests
{
    private static readonly string[] RequiredClips = ["idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const long CacheBudget = 24L * 1024 * 1024;

    [Fact]
    public void ValidPairRetainsAllSixHundredMatchingSlotsAndListsBothChannelsForValidation()
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 600);
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.False(catalog.HasOptionalActionFallback);
        var decoration = Assert.Single(catalog.Decorations).Value;
        Assert.Equal((640d, 640d, -64d, -100d),
            (decoration.CanvasWidth, decoration.CanvasHeight, decoration.OffsetX, decoration.OffsetY));
        var cat = catalog.Clips["firefly"];
        Assert.Equal(600, cat.Frames.Count);
        Assert.Equal(600, decoration.Clip.Frames.Count);
        Assert.Equal(10000, cat.DurationMs);
        Assert.Equal(cat.DurationMs, decoration.Clip.DurationMs);
        Assert.Equal(cat.Frames.Select(frame => frame.DurationMs), decoration.Clip.Frames.Select(frame => frame.DurationMs));
        Assert.All(cat.Frames, frame => Assert.InRange(frame.DurationMs, 16, 17));
        Assert.Equal(catalog.Clips["idle"].Frames[0].File, cat.Frames[0].File);
        Assert.Equal(cat.Frames[0].File, cat.Frames[^1].File);
        var expectedFiles = fixture.Cat.Concat(fixture.Overlay).Append("wink.png").Distinct().Order();
        Assert.Equal(expectedFiles, catalog.AllFrameFiles.Order());
        Assert.DoesNotContain(catalog.IdleActions, action => action.Clip == "firefly");
    }

    [Theory]
    [InlineData("missing-cat")]
    [InlineData("missing-decoration")]
    [InlineData("missing-decoration-frame")]
    [InlineData("path-escape")]
    [InlineData("absolute-escape")]
    [InlineData("count-mismatch")]
    [InlineData("duration-mismatch")]
    [InlineData("same-total-wrong-slots")]
    [InlineData("zero-duration")]
    [InlineData("empty-frames")]
    [InlineData("looping-decoration")]
    [InlineData("looping-cat")]
    [InlineData("wrong-cat-start")]
    [InlineData("wrong-cat-end")]
    [InlineData("zero-canvas")]
    [InlineData("oversized-canvas")]
    [InlineData("fractional-canvas")]
    [InlineData("missing-offset")]
    [InlineData("disjoint-offset")]
    [InlineData("malformed-definition")]
    [InlineData("shared-file-wrong-dimensions")]
    public void BrokenPairIsSkippedTogetherWhileOtherActionsRemainUsable(string defect)
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 6);
        var manifest = fixture.Manifest;
        var clips = manifest["clips"]!.AsObject();
        var decoration = manifest["decorations"]!["firefly"]!.AsObject();
        var overlay = decoration["frames"]!.AsArray();
        var cat = clips["firefly"]!["frames"]!.AsArray();
        switch (defect)
        {
            case "missing-cat": clips.Remove("firefly"); break;
            case "missing-decoration": manifest.Remove("decorations"); break;
            case "missing-decoration-frame": overlay[1]!["file"] = "missing.png"; break;
            case "path-escape": overlay[1]!["file"] = "../outside.png"; break;
            case "absolute-escape": overlay[1]!["file"] = Path.Combine(Path.GetTempPath(), "outside.png"); break;
            case "count-mismatch": overlay.RemoveAt(overlay.Count - 1); break;
            case "duration-mismatch": overlay[1]!["durationMs"] = 20; break;
            case "same-total-wrong-slots": overlay[0]!["durationMs"] = 16; overlay[1]!["durationMs"] = 17; break;
            case "zero-duration": overlay[1]!["durationMs"] = 0; break;
            case "empty-frames": decoration["frames"] = new JsonArray(); break;
            case "looping-decoration": decoration["loop"] = true; break;
            case "looping-cat": clips["firefly"]!["loop"] = true; break;
            case "wrong-cat-start": cat[0]!["file"] = fixture.Cat[1]; break;
            case "wrong-cat-end": cat[^1]!["file"] = fixture.Cat[1]; break;
            case "zero-canvas": decoration["canvas"]!["width"] = 0; break;
            case "oversized-canvas": decoration["canvas"]!["width"] = 8192; break;
            case "fractional-canvas": decoration["canvas"]!["height"] = 640.5; break;
            case "missing-offset": decoration.Remove("offset"); break;
            case "disjoint-offset": decoration["offset"]!["x"] = -700; break;
            case "malformed-definition": manifest["decorations"]!["firefly"] = "invalid"; break;
            case "shared-file-wrong-dimensions": overlay[1]!["file"] = fixture.Cat[1]; break;
        }
        Save(directory, manifest);
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.True(catalog.HasOptionalActionFallback);
        Assert.False(catalog.Clips.ContainsKey("firefly"));
        Assert.Empty(catalog.Decorations);
        Assert.All(RequiredClips, name => Assert.True(catalog.Clips.ContainsKey(name)));
        Assert.True(catalog.Clips.ContainsKey("wink"));
        Assert.DoesNotContain(catalog.AllFrameFiles, file => file.StartsWith("overlay-"));
        var behavior = new BehaviorController(new StopwatchClock(), catalog.Clips, randomBetween: (_, max) => max);
        behavior.Click();
        Assert.Equal(ReactionPhase.Wink, behavior.ReactionPhase);
    }

    [Fact]
    public void OldPackWithoutEitherFireflyChannelDoesNotProduceAnOptionalWarning()
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 6);
        fixture.Manifest["clips"]!.AsObject().Remove("firefly");
        fixture.Manifest.Remove("decorations");
        Save(directory, fixture.Manifest);
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.False(catalog.HasOptionalActionFallback);
        Assert.Empty(catalog.Decorations);
        Assert.Equal(new[] { "rest.png", "wink.png" }, catalog.AllFrameFiles.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentAutomaticPoolPreservesWeightsAndRemovesOnlyAnIncompleteFireflyPair(bool missingDecoration)
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 3);
        var clips = fixture.Manifest["clips"]!.AsObject();
        JsonObject Sequence(params string[] files) => new()
        {
            ["frames"] = new JsonArray(files.Select(file => (JsonNode)new JsonObject
            {
                ["file"] = file, ["durationMs"] = 100
            }).ToArray())
        };
        clips["yawn"] = Sequence("rest.png", "wink.png", "rest.png");
        clips["rise"] = Sequence("rest.png", fixture.Cat[1]);
        clips["hold"] = Sequence(fixture.Cat[1]);
        clips["hold"]!["loop"] = true;
        clips["settle"] = Sequence(fixture.Cat[1], "rest.png");
        fixture.Manifest["idleActions"] = new JsonArray(
            new JsonObject { ["clip"] = "blink", ["weight"] = 2 },
            new JsonObject { ["clip"] = "yawn", ["weight"] = 1 },
            new JsonObject { ["clip"] = "rise", ["weight"] = 1 },
            new JsonObject { ["clip"] = "firefly", ["weight"] = 1 });
        if (missingDecoration) fixture.Manifest.Remove("decorations");
        Save(directory, fixture.Manifest);

        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.Equal(missingDecoration, catalog.HasOptionalActionFallback);
        var expected = new[] { new IdleAction("blink", 2), new IdleAction("yawn", 1), new IdleAction("rise", 1) };
        Assert.Equal(missingDecoration ? expected : expected.Append(new IdleAction("firefly", 1)), catalog.IdleActions);
        Assert.Equal(!missingDecoration, catalog.Decorations.ContainsKey("firefly"));
        Assert.True(catalog.Clips.ContainsKey("wink"));
    }

    [Fact]
    public void NativeOverlayDecodesAt640AndTransparentEndpointsRemainInvisible()
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 3);
        Sta.Run(() =>
        {
            WriteNativeFrames(directory, fixture);
            var catalog = new AssetCatalog(directory.Root);
            catalog.CacheForSize(480, 2);
            Assert.Equal(512, catalog.Image(fixture.Cat[1]).PixelHeight);
            Assert.Equal(640, catalog.Image(fixture.Overlay[1]).PixelHeight);
            Assert.Equal(640, catalog.Image(fixture.Overlay[1]).PixelWidth);
            Assert.False(catalog.Hit(fixture.Overlay[0], .5, .5));
            Assert.True(catalog.Hit(fixture.Overlay[1], .5, .5));
            Assert.False(catalog.HasFrameFallback);
            var misses = catalog.SynchronousCacheMisses;
            var first = catalog.Image(fixture.Overlay[1]);
            catalog.CacheForSize(120, 1);
            Assert.Same(first, catalog.Image(fixture.Overlay[1]));
            Assert.Equal(misses, catalog.SynchronousCacheMisses);
        });
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("wrong-width")]
    [InlineData("wrong-height")]
    public void BadOverlayPixelsBecomeSameSizeTransparencyWithoutUsingTheCatFallback(string defect)
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 3);
        Sta.Run(() =>
        {
            WriteNativeFrames(directory, fixture);
            // Deliberately do not create fallback.png: a decoration must recover
            // without loading the character fallback image.
            if (defect == "corrupt") File.WriteAllText(directory.FilePath(fixture.Overlay[1]), "not a PNG");
            else WritePng(directory.FilePath(fixture.Overlay[1]), defect == "wrong-width" ? 512 : 640,
                defect == "wrong-height" ? 512 : 640);
            var catalog = new AssetCatalog(directory.Root);
            catalog.CacheForSize(240, 1.5);
            var image = catalog.Image(fixture.Overlay[1]);
            Assert.True(catalog.HasFrameFallback);
            Assert.Equal((640, 640), (image.PixelWidth, image.PixelHeight));
            Assert.True(image.IsFrozen);
            Assert.False(catalog.Hit(fixture.Overlay[1], .5, .5));
            Assert.True(catalog.Hit(fixture.Cat[1], .5, .5));
            var pixels = new byte[640 * 640 * 4];
            image.CopyPixels(pixels, 640 * 4, 0);
            Assert.All(pixels, value => Assert.Equal(0, value));
        });
    }

    [Fact]
    public void OnePrefetchRefreshKeepsBothChannelsInFlightAndProtectedWithinTheSharedBudget()
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 48);
        Sta.Run(() =>
        {
            using var decoder = new PairDecoder(fixture.Cat[3], fixture.Overlay[3]);
            var catalog = new AssetCatalog(directory.Root, CacheBudget, decoder.Decode);
            catalog.CacheForSize(240, 1.5);
            var openingMisses = catalog.SynchronousCacheMisses;
            foreach (var file in fixture.Cat.Take(3).Concat(fixture.Overlay.Take(3))) catalog.Image(file);
            Assert.Equal(openingMisses, catalog.SynchronousCacheMisses);
            try
            {
                catalog.PreparePlayback(catalog.Clips["firefly"], 0);
                Assert.True(decoder.Entered.Wait(Deadline), "The two workers must start the cat and overlay together.");
                for (var i = 0; i < 12; i++) catalog.PreparePlayback(catalog.Clips["firefly"], 0);
                Assert.Equal(1, decoder.Calls(fixture.Cat[3]));
                Assert.Equal(1, decoder.Calls(fixture.Overlay[3]));
                decoder.Release.Set();
                WaitForIdle(catalog);
                foreach (var file in fixture.Cat.Skip(10).Take(20))
                {
                    catalog.Image(file);
                    Assert.InRange(catalog.CachedBytes, 1, CacheBudget);
                }
                var misses = catalog.SynchronousCacheMisses;
                foreach (var file in new[] { fixture.Cat[0], fixture.Overlay[0], fixture.Cat[3], fixture.Overlay[3], fixture.Cat[4], fixture.Overlay[4] })
                    Assert.True(catalog.Image(file).IsFrozen);
                Assert.Equal(misses, catalog.SynchronousCacheMisses);
                Assert.Equal(512, decoder.HeightFor(fixture.Cat[3]));
                Assert.Equal(640, decoder.HeightFor(fixture.Overlay[3]));
                Assert.False(catalog.HasFrameFallback);
            }
            finally
            {
                decoder.Release.Set();
                catalog.StopPrefetch();
                WaitForIdle(catalog);
            }
        });
    }

    [Fact]
    public void SameNameAndFramesOnATemporaryClipDoNotBindTheCatalogDecoration()
    {
        using var directory = new TestDirectory();
        var fixture = WritePack(directory, 48);
        Sta.Run(() =>
        {
            using var decoder = new PairDecoder();
            var catalog = new AssetCatalog(directory.Root, CacheBudget, decoder.Decode);
            catalog.CacheForSize(240, 1.5);
            try
            {
                var temporary = new AnimationClip("firefly", catalog.Clips["firefly"].Frames);
                catalog.PreparePlayback(temporary, 3);
                WaitForIdle(catalog);
                Assert.True(catalog.Hit(fixture.Cat[4], .5, .5));
                Assert.False(catalog.Hit(fixture.Overlay[3], .5, .5));
                Assert.False(catalog.Hit(fixture.Overlay[4], .5, .5));
                Assert.Equal(0, decoder.Calls(fixture.Overlay[3]));
            }
            finally { catalog.StopPrefetch(); WaitForIdle(catalog); }
        });
    }

    private static void WaitForIdle(AssetCatalog catalog)
    {
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var sync = typeof(AssetCatalog).GetField("_cacheLock", fields)!.GetValue(catalog)!;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (sync) return (int)typeof(AssetCatalog).GetField("_workerGeneration", fields)!.GetValue(catalog)! == -1;
        }, Deadline), "Background decoders must finish within the test deadline.");
    }

    private static Fixture WritePack(TestDirectory directory, int count)
    {
        var cat = Enumerable.Range(0, count).Select(index => index == 0 || index == count - 1 ? "rest.png" : $"cat-{index}.png").ToArray();
        var overlay = Enumerable.Range(0, count).Select(index => index == count - 1 ? "overlay-0.png" : $"overlay-{index}.png").ToArray();
        foreach (var file in cat.Concat(overlay).Append("wink.png").Distinct())
            File.WriteAllText(directory.FilePath(file), "The native or injected decoder supplies this fixture's pixels.");
        JsonObject Clip(IEnumerable<string> files) => new()
        {
            ["frames"] = new JsonArray(files.Select((file, index) => (JsonNode)new JsonObject
            {
                ["file"] = file,
                ["durationMs"] = (int)Math.Round((index + 1) * 1000d / 60) - (int)Math.Round(index * 1000d / 60)
            }).ToArray())
        };
        var clips = new JsonObject();
        foreach (var name in RequiredClips) clips[name] = Clip(["rest.png"]);
        clips["wink"] = Clip(["rest.png", "wink.png", "rest.png"]);
        clips["firefly"] = Clip(cat);
        var decoration = Clip(overlay);
        decoration["canvas"] = new JsonObject { ["width"] = 640, ["height"] = 640 };
        decoration["offset"] = new JsonObject { ["x"] = -64, ["y"] = -100 };
        var manifest = new JsonObject
        {
            ["canvas"] = new JsonObject { ["width"] = 512, ["height"] = 512 },
            ["clips"] = clips,
            ["decorations"] = new JsonObject { ["firefly"] = decoration }
        };
        Save(directory, manifest);
        return new(cat, overlay, manifest);
    }

    private static void Save(TestDirectory directory, JsonObject manifest) =>
        File.WriteAllText(directory.FilePath("manifest.json"), manifest.ToJsonString());

    private static void WriteNativeFrames(TestDirectory directory, Fixture fixture)
    {
        foreach (var file in fixture.Cat.Append("wink.png").Distinct()) WritePng(directory.FilePath(file), 512, 512);
        foreach (var file in fixture.Overlay.Distinct()) WritePng(directory.FilePath(file), 640, 640, file == fixture.Overlay[0]);
    }

    private static void WritePng(string file, int width, int height, bool transparent = false)
    {
        var pixels = new byte[width * height * 4];
        if (!transparent)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file);
        encoder.Save(output);
    }

    private sealed record Fixture(string[] Cat, string[] Overlay, JsonObject Manifest);

    private sealed class PairDecoder : IDisposable
    {
        private readonly HashSet<string> _blocked;
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private readonly ConcurrentDictionary<string, int> _heights = new();
        private readonly AssetCatalog.CachedFrame _cat, _overlay;
        public CountdownEvent Entered { get; }
        public ManualResetEventSlim Release { get; } = new(false);

        public PairDecoder(params string[] blocked)
        {
            _blocked = blocked.ToHashSet();
            Entered = new(_blocked.Count);
            AssetCatalog.CachedFrame Frame(int size)
            {
                var pixels = new byte[size * size * 4];
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
                image.Freeze();
                return new(image, Enumerable.Repeat((byte)255, size * size).ToArray());
            }
            _cat = Frame(512);
            _overlay = Frame(640);
        }

        public int Calls(string file) => _calls.GetValueOrDefault(file);
        public int HeightFor(string file) => _heights[file];
        public AssetCatalog.CachedFrame Decode(string path, int height)
        {
            var file = Path.GetFileName(path);
            var count = _calls.AddOrUpdate(file, 1, (_, previous) => previous + 1);
            _heights[file] = height;
            if (_blocked.Contains(file))
            {
                if (count == 1) Entered.Signal();
                Release.Wait();
            }
            return file.StartsWith("overlay-") ? _overlay : _cat;
        }
        public void Dispose() { Release.Dispose(); Entered.Dispose(); }
    }
}
