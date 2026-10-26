using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TablePet.App.Tests;

public sealed class AssetCatalogTests
{
    private static readonly string[] RequiredClips = ["idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle"];

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("malformed-json")]
    [InlineData("missing-frame")]
    [InlineData("missing-clip")]
    [InlineData("empty-frames")]
    [InlineData("zero-duration")]
    [InlineData("invalid-canvas")]
    [InlineData("path-traversal")]
    public void UnusableManifestFallsBackToCompleteStaticAnimationSet(string defect)
    {
        using var directory = new TestDirectory();
        var frame = "frame.png";
        File.WriteAllText(directory.FilePath(frame), "File existence is sufficient for manifest validation.");
        if (defect == "path-traversal") frame = "../outside.png";
        var names = defect == "missing-clip" ? RequiredClips[..^1] : RequiredClips;
        if (defect == "missing-frame") frame = "does-not-exist.png";
        if (defect != "missing-manifest")
        {
            var manifest = CreateManifest(names, frame, defect == "zero-duration" ? 0 : 100,
                defect == "empty-frames", defect == "invalid-canvas" ? 0 : 512);
            File.WriteAllText(directory.FilePath("manifest.json"), defect == "malformed-json" ? "{broken" : manifest);
        }
        var catalog = new AssetCatalog(directory.Root);
        Assert.True(catalog.IsFallback);
        Assert.Equal(RequiredClips.Order(), catalog.Clips.Keys.Order());
        Assert.All(catalog.Clips.Values, clip =>
        {
            clip.EnsureValid();
            Assert.Equal("fallback.png", Assert.Single(clip.Frames).File);
        });
    }

    [Fact]
    public void CorruptPngUsesDecodedFallbackAndReusesCacheAtUnchangedSize()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("frame.png"), "not a PNG");
        File.WriteAllText(directory.FilePath("manifest.json"), CreateManifest(RequiredClips, "frame.png"));
        Sta.Run(() =>
        {
            WriteOpaquePng(directory.FilePath("fallback.png"));
            var catalog = new AssetCatalog(directory.Root);
            Assert.False(catalog.IsFallback); // Manifest is valid; the individual frame must recover on decode.
            catalog.CacheForSize(240, 1);
            var fallback = catalog.Image("frame.png");
            Assert.True(fallback.PixelWidth > 0);
            Assert.True(fallback.IsFrozen);
            Assert.True(catalog.Hit("frame.png", 0.5, 0.5));
            catalog.CacheForSize(240, 1);
            Assert.Same(fallback, catalog.Image("frame.png"));
        });
    }

    [Fact]
    public void BodyRegionUsesAssetCoordinatesAtEveryDisplayScale()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
        {
            canvas = new { width = 512, height = 512 },
            hitRegion = new { x = 128, y = 128, width = 256, height = 256 },
            clips = RequiredClips.ToDictionary(name => name, name => new
            {
                loop = false,
                frames = new[] { new { file = "frame.png", durationMs = 100 } }
            })
        }));
        Sta.Run(() =>
        {
            WriteOpaquePng(directory.FilePath("frame.png"));
            var catalog = new AssetCatalog(directory.Root);
            foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
            {
                catalog.CacheForSize(240, dpi);
                Assert.True(catalog.Hit("frame.png", 0.5, 0.5));
                Assert.False(catalog.Hit("frame.png", 0.1, 0.1));
                Assert.False(catalog.Hit("frame.png", 0.9, 0.5));
            }
        });
    }

    [Fact]
    public void MissingManifestStillDisplaysAvailableFallbackImage()
    {
        using var directory = new TestDirectory();
        Sta.Run(() =>
        {
            WriteOpaquePng(directory.FilePath("fallback.png"));
            var catalog = new AssetCatalog(directory.Root);
            catalog.CacheForSize(240, 1);
            Assert.True(catalog.IsFallback);
            Assert.True(catalog.Image("fallback.png").PixelHeight > 0);
        });
    }

    [Fact]
    public void DenseAnimationEvictsOldFramesWithinBudgetAndPreservesAlphaHitTesting()
    {
        using var directory = new TestDirectory();
        Sta.Run(() =>
        {
            var frames = WriteDensePack(directory, 24);
            var catalog = new AssetCatalog(directory.Root, 8L * 1024 * 1024);
            catalog.CacheForSize(480, 2);
            Assert.Equal(512, catalog.Image(frames[0]).PixelHeight); // Never expand native art in memory.
            foreach (var file in frames)
            {
                Assert.True(catalog.Image(file).IsFrozen);
                Assert.True(catalog.Hit(file, .5, .5));
                Assert.False(catalog.Hit(file, 1, .5));
                Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
            }
            Assert.True(catalog.CachedFrameCount < frames.Length);
            Assert.True(catalog.Image(frames[1]).IsFrozen); // An evicted frame can be visited again.
        });
    }

    [Fact]
    public void BackgroundLookAheadPreparesUpcomingFramesWithoutOverflowingSmallBudget()
    {
        using var directory = new TestDirectory();
        Sta.Run(() =>
        {
            var frames = WriteDensePack(directory, 24);
            var catalog = new AssetCatalog(directory.Root, 16L * 1024 * 1024);
            catalog.CacheForSize(512, 1);
            catalog.PreparePlayback(catalog.Clips["rise"], 0);
            Assert.True(SpinWait.SpinUntil(() => catalog.CachedFrameCount >= 4, TimeSpan.FromSeconds(5)));
            var misses = catalog.SynchronousCacheMisses;
            Assert.True(catalog.Image(frames[3]).IsFrozen);
            Assert.Equal(misses, catalog.SynchronousCacheMisses);
            Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
            catalog.StopPrefetch();
        });
    }

    [Fact]
    public void PrefetchedFutureFramesSurviveEvictionOfRecentlyDisplayedPastFrames()
    {
        using var directory = new TestDirectory();
        Sta.Run(() =>
        {
            var frames = WriteDensePack(directory, 24);
            var catalog = new AssetCatalog(directory.Root, 8L * 1024 * 1024);
            catalog.CacheForSize(240, 1.5);
            catalog.Image(frames[3]);
            catalog.PreparePlayback(catalog.Clips["rise"], 3);
            Assert.True(SpinWait.SpinUntil(() => catalog.CachedFrameCount >= 5, TimeSpan.FromSeconds(5)));
            // Future frames were loaded earlier than later cache accesses. LRU alone
            // would discard them immediately before they are needed for presentation.
            foreach (var file in frames.Skip(10)) catalog.Image(file);
            var misses = catalog.SynchronousCacheMisses;
            catalog.Image(frames[4]);
            Assert.Equal(misses, catalog.SynchronousCacheMisses);
            Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
            catalog.StopPrefetch();
        });
    }

    private static string[] WriteDensePack(TestDirectory directory, int count)
    {
        var frames = Enumerable.Range(0, count).Select(i => $"frame-{i}.png").ToArray();
        foreach (var file in frames) WriteOpaquePng(directory.FilePath(file));
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
        {
            canvas = new { width = 512, height = 512 },
            clips = RequiredClips.ToDictionary(name => name, name => new
            {
                loop = false,
                frames = frames.Select(file => new { file, durationMs = 4 }).ToArray()
            })
        }));
        return frames;
    }

    private static string CreateManifest(IEnumerable<string> clips, string file, int duration = 100, bool emptyFrames = false, int width = 512) =>
        JsonSerializer.Serialize(new
        {
            canvas = new { width, height = 512 },
            clips = clips.ToDictionary(name => name, name => new
            {
                loop = name == "sleep_loop",
                frames = emptyFrames ? Array.Empty<object>() : new object[] { new { file, durationMs = duration } }
            })
        });

    private static void WriteOpaquePng(string path)
    {
        var pixels = new byte[512 * 512 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 120; pixels[i + 1] = 120; pixels[i + 2] = 120; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 512 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
