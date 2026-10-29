using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.Core;
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
    public void OldManifestDefaultsToBlinkWithoutRequiringNewExpressionClips()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("frame.png"), "manifest fixture");
        File.WriteAllText(directory.FilePath("manifest.json"), CreateManifest(RequiredClips, "frame.png"));
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.False(catalog.HasOptionalActionFallback);
        Assert.Equal("blink", Assert.Single(catalog.IdleActions).Clip);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-frame")]
    [InlineData("looping")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    public void MeowIsOptionalAndRejectsBrokenOrUnjoinableReactions(string defect)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("frame.png"), "manifest fixture");
        File.WriteAllText(directory.FilePath("meow.png"), "manifest fixture");
        var clips = RequiredClips.ToDictionary(name => name, name => (object)new
        {
            frames = new[] { new { file = "frame.png", durationMs = 100 } }
        });
        clips["meow"] = new
        {
            loop = defect == "looping",
            frames = new[]
            {
                new { file = defect == "wrong-start" ? "meow.png" : "frame.png", durationMs = 180 },
                new { file = defect == "missing-frame" ? "absent.png" : "meow.png", durationMs = 920 },
                new { file = defect == "wrong-end" ? "meow.png" : "frame.png", durationMs = 300 }
            }
        };
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
        {
            canvas = new { width = 512, height = 512 }, clips
        }));
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.Equal(defect != "valid", catalog.HasOptionalActionFallback);
        Assert.Equal(defect == "valid", catalog.Clips.ContainsKey("meow"));
        Assert.All(RequiredClips, name => Assert.True(catalog.Clips.ContainsKey(name)));
        Assert.Equal("blink", Assert.Single(catalog.IdleActions).Clip);
        if (defect == "valid")
        {
            var behavior = new BehaviorController(new StopwatchClock(), catalog.Clips);
            behavior.DoubleClick();
            Assert.Equal(ReactionPhase.Meow, behavior.ReactionPhase);
            Assert.Equal("meow", behavior.Player.CurrentClip!.Name);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-frame")]
    [InlineData("empty-frames")]
    [InlineData("zero-duration")]
    [InlineData("path-traversal")]
    [InlineData("looping")]
    [InlineData("wrong-endpoint")]
    public void OptionalExpressionDefectsPreserveWorkingCoreClipsAndOtherExpressions(string defect)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("frame.png"), "manifest fixture");
        File.WriteAllText(directory.FilePath("expression.png"), "manifest fixture");
        var clips = RequiredClips.ToDictionary(name => name, name => (object)new
        {
            frames = new[] { new { file = "frame.png", durationMs = 100 } }
        });
        clips["happy"] = new { frames = new[] { new { file = "frame.png", durationMs = 100 }, new { file = "frame.png", durationMs = 100 } } };
        var middle = defect switch { "missing-frame" => "absent.png", "path-traversal" => "../outside.png", _ => "expression.png" };
        clips["yawn"] = new
        {
            loop = defect == "looping",
            frames = defect == "empty-frames" ? [] : new[]
            {
                new { file = "frame.png", durationMs = 100 },
                new { file = middle, durationMs = defect == "zero-duration" ? 0 : 100 },
                new { file = defect == "wrong-endpoint" ? "expression.png" : "frame.png", durationMs = 100 }
            }
        };
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
        {
            canvas = new { width = 512, height = 512 }, clips,
            idleActions = new[] { new { clip = "blink", weight = 4 }, new { clip = "yawn", weight = 1 }, new { clip = "happy", weight = 2 } }
        }));
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.Equal(defect != "valid", catalog.HasOptionalActionFallback);
        Assert.All(RequiredClips, name => Assert.True(catalog.Clips.ContainsKey(name)));
        Assert.Equal(defect == "valid" ? new[] { "blink", "yawn" } : new[] { "blink" },
            catalog.IdleActions.Select(action => action.Clip));
        Assert.Equal(4, catalog.IdleActions[0].Weight);
        Assert.Equal(defect == "valid" ? 1 : 4, catalog.IdleActions[^1].Weight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WinkIsAvailableToClicksButNeverScheduledFromLegacyIdleEntries(bool legacyIdleEntry)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("frame.png"), "manifest fixture");
        File.WriteAllText(directory.FilePath("wink.png"), "manifest fixture");
        var clips = RequiredClips.ToDictionary(name => name, name => (object)new
        {
            frames = new[] { new { file = "frame.png", durationMs = 100 } }
        });
        clips["wink"] = new { frames = new[]
        {
            new { file = "frame.png", durationMs = 100 },
            new { file = "wink.png", durationMs = 350 },
            new { file = "frame.png", durationMs = 100 }
        } };
        File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
        {
            canvas = new { width = 512, height = 512 }, clips,
            idleActions = legacyIdleEntry ? new[] { new { clip = "wink", weight = 1 } } : []
        }));
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.False(catalog.HasOptionalActionFallback);
        Assert.Equal("blink", Assert.Single(catalog.IdleActions).Clip);
        var behavior = new BehaviorController(new StopwatchClock(), catalog.Clips,
            randomBetween: (_, max) => max, idleActions: catalog.IdleActions);
        behavior.Click();
        Assert.Equal(ReactionPhase.Wink, behavior.ReactionPhase);
        Assert.Equal("wink", behavior.Player.CurrentClip!.Name);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null, 42, {\"clip\":\"blink\",\"weight\":\"bad\"}, {\"clip\":\"missing\",\"weight\":1}]")]
    public void MalformedOptionalActionListFallsBackToBlinkWithoutChangingThePack(string actionJson)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.FilePath("frame.png"), "manifest fixture");
        var basic = CreateManifest(RequiredClips, "frame.png");
        File.WriteAllText(directory.FilePath("manifest.json"), basic[..^1] + ",\"idleActions\":" + actionJson + "}");
        var catalog = new AssetCatalog(directory.Root);
        Assert.False(catalog.IsFallback);
        Assert.True(catalog.HasOptionalActionFallback);
        Assert.Equal("blink", Assert.Single(catalog.IdleActions).Clip);
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
            Assert.True(SpinWait.SpinUntil(() => catalog.Hit(frames[3], .5, .5), TimeSpan.FromSeconds(5)));
            var misses = catalog.SynchronousCacheMisses;
            Assert.True(catalog.Image(frames[3]).IsFrozen);
            Assert.Equal(misses, catalog.SynchronousCacheMisses);
            Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
            catalog.StopPrefetch();
        });
    }

    [Fact]
    public void RepeatedOpeningSlotsStillWarmTheFirstThreeDifferentPoses()
    {
        using var directory = new TestDirectory();
        Sta.Run(() =>
        {
            var frames = WriteDensePack(directory, 24);
            var repeatedStart = Enumerable.Repeat(frames[0], 10).Concat(frames.Skip(1)).ToArray();
            File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
            {
                canvas = new { width = 512, height = 512 },
                clips = RequiredClips.ToDictionary(name => name, name => new
                {
                    frames = repeatedStart.Select(file => new { file, durationMs = 4 }).ToArray()
                })
            }));
            var catalog = new AssetCatalog(directory.Root, 16L * 1024 * 1024);
            catalog.CacheForSize(240, 1.5);
            var misses = catalog.SynchronousCacheMisses;
            foreach (var file in frames.Take(3)) Assert.True(catalog.Image(file).IsFrozen);
            Assert.Equal(misses, catalog.SynchronousCacheMisses);
            Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
        });
    }

    [Fact]
    public void FittingExpressionPreparesItsReopeningBeyondALongHoldWithinTheSameBudget()
    {
        using var directory = new TestDirectory();
        Sta.Run(() =>
        {
            var frames = WriteDensePack(directory, 24);
            var clips = RequiredClips.ToDictionary(name => name, name => (object)new
            {
                frames = frames.Select(file => new { file, durationMs = 4 }).ToArray()
            });
            var expression = frames.Take(3).Concat(Enumerable.Repeat(frames[3], 150))
                .Concat(frames.Skip(4).Take(4)).Append(frames[0]);
            clips["wink"] = new { frames = expression.Select(file => new { file, durationMs = 4 }).ToArray() };
            File.WriteAllText(directory.FilePath("manifest.json"), JsonSerializer.Serialize(new
            {
                canvas = new { width = 512, height = 512 }, clips
            }));
            var catalog = new AssetCatalog(directory.Root, 16L * 1024 * 1024);
            catalog.CacheForSize(240, 1.5);
            catalog.Image(frames[3]);
            catalog.PreparePlayback(catalog.Clips["wink"], 3);
            Assert.True(SpinWait.SpinUntil(() => catalog.Hit(frames[7], .5, .5), TimeSpan.FromSeconds(5)),
                "The last reopening pose should be prepared during the 600ms closed-eye hold.");
            foreach (var unrelated in frames.Skip(10)) catalog.Image(unrelated);
            var misses = catalog.SynchronousCacheMisses;
            foreach (var file in frames.Take(8)) Assert.True(catalog.Image(file).IsFrozen);
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
