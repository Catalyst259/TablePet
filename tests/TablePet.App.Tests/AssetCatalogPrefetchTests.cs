using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TablePet.App.Tests;

public sealed class AssetCatalogPrefetchTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const long CacheBudget = 16L * 1024 * 1024;
    private static readonly string[] RequiredClips = ["idle", "blink", "fall_asleep", "sleep_loop", "wake", "rise", "hold", "settle"];

    [Fact]
    public void TwoWorkersPrepareDifferentFramesWithoutRepeatingInFlightDecodes()
    {
        using var directory = new TestDirectory();
        var frames = WriteDenseManifest(directory);
        Sta.Run(() =>
        {
            using var decoder = new BlockingDecoder(frames[3], frames[4]);
            var catalog = new AssetCatalog(directory.Root, CacheBudget, decoder.Decode);
            catalog.CacheForSize(240, 1.5);
            try
            {
                catalog.PreparePlayback(catalog.Clips["rise"], 0);
                Assert.True(decoder.Entered.Wait(Deadline), "Two different future frames must begin decoding before either is released.");
                Assert.Equal(2, decoder.MaximumConcurrency);

                // Refreshing the look-ahead must not enqueue frames already owned by
                // either decoder, even though neither has reached the cache yet.
                for (var repeat = 0; repeat < 12; repeat++)
                    catalog.PreparePlayback(catalog.Clips["rise"], 0);
                Assert.Equal(1, decoder.CallsFor(frames[3]));
                Assert.Equal(1, decoder.CallsFor(frames[4]));

                decoder.Release.Set();
                WaitForIdle(catalog);
                Assert.True(catalog.Hit(frames[3], .5, .5));
                Assert.True(catalog.Hit(frames[4], .5, .5));
                Assert.True(catalog.Hit(frames[7], .5, .5));
                Assert.Equal(1, decoder.CallsFor(frames[3]));
                Assert.Equal(1, decoder.CallsFor(frames[4]));
                Assert.InRange(decoder.MaximumConcurrency, 1, 2);
                Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
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
    public void ObsoleteGenerationCannotInsertItsFrameOrClearNewWorkerOwnership()
    {
        using var directory = new TestDirectory();
        var frames = WriteDenseManifest(directory);
        Sta.Run(() =>
        {
            using var decoder = new BlockingDecoder(frames[3]);
            var catalog = new AssetCatalog(directory.Root, CacheBudget, decoder.Decode);
            catalog.CacheForSize(240, 1.5);
            var sync = Read<object>(catalog, "_cacheLock");
            Task? oldWorker = null;
            try
            {
                int oldGeneration;
                lock (sync)
                {
                    oldGeneration = Read<int>(catalog, "_generation");
                    Read<Queue<string>>(catalog, "_lookAhead").Enqueue(frames[3]);
                    Read<HashSet<string>>(catalog, "_playbackFrames").Add(frames[3]);
                    Write(catalog, "_workerCount", 1);
                    Write(catalog, "_workerGeneration", oldGeneration);
                }
                var prefetch = typeof(AssetCatalog).GetMethod("Prefetch", BindingFlags.Instance | BindingFlags.NonPublic)!;
                oldWorker = Task.Run(() => prefetch.Invoke(catalog, [oldGeneration, 512]));
                Assert.True(decoder.Entered.Wait(Deadline), "The old generation must own a frame before cache invalidation.");

                // Native art normally keeps a fixed decoded size. Force only the
                // invalidation condition, then exercise the real CacheForSize reset.
                lock (sync) Write(catalog, "_decodedHeight", 0);
                catalog.CacheForSize(240, 1.5);
                int newGeneration;
                lock (sync)
                {
                    newGeneration = Read<int>(catalog, "_generation");
                    Assert.NotEqual(oldGeneration, newGeneration);
                    // Model new work owning the same filename. The old completion
                    // must leave both that ownership and the new worker count intact.
                    Read<HashSet<string>>(catalog, "_inFlight").Add(frames[3]);
                    Read<HashSet<string>>(catalog, "_playbackFrames").Add(frames[3]);
                    Write(catalog, "_workerCount", 2);
                    Write(catalog, "_workerGeneration", newGeneration);
                }

                decoder.Release.Set();
                Assert.True(oldWorker.Wait(Deadline), "The obsolete worker must finish after its decoder is released.");
                lock (sync)
                {
                    Assert.Contains(frames[3], Read<HashSet<string>>(catalog, "_inFlight"));
                    Assert.Equal(2, Read<int>(catalog, "_workerCount"));
                    Assert.Equal(newGeneration, Read<int>(catalog, "_workerGeneration"));
                }
                Assert.False(catalog.Hit(frames[3], .5, .5));
                Assert.Equal(3, catalog.CachedFrameCount);
                Assert.InRange(catalog.CachedBytes, 1, catalog.CacheBudgetBytes);
            }
            finally
            {
                decoder.Release.Set();
                if (oldWorker is not null)
                    Assert.True(oldWorker.Wait(Deadline), "Cleanup must release the old decoder.");
                lock (sync)
                {
                    // These are simulated new workers; no tasks were created for them.
                    Read<HashSet<string>>(catalog, "_inFlight").Clear();
                    Write(catalog, "_workerCount", 0);
                    Write(catalog, "_workerGeneration", -1);
                }
                catalog.StopPrefetch();
            }
        });
    }

    private static void WaitForIdle(AssetCatalog catalog)
    {
        var sync = Read<object>(catalog, "_cacheLock");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (sync) return Read<int>(catalog, "_workerGeneration") == -1;
        }, Deadline), "All queued background work must finish within the test deadline.");
    }

    private static T Read<T>(AssetCatalog catalog, string name) =>
        (T)typeof(AssetCatalog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;

    private static void Write(AssetCatalog catalog, string name, object value) =>
        typeof(AssetCatalog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalog, value);

    private static string[] WriteDenseManifest(TestDirectory directory)
    {
        var frames = Enumerable.Range(0, 24).Select(index => $"frame-{index}.png").ToArray();
        foreach (var frame in frames) File.WriteAllText(directory.FilePath(frame), "The injected decoder supplies this manifest fixture's pixels.");
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

    private sealed class BlockingDecoder : IDisposable
    {
        private readonly HashSet<string> _blocked;
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private readonly AssetCatalog.CachedFrame _frame;
        private int _active, _maximumConcurrency;
        public CountdownEvent Entered { get; }
        public ManualResetEventSlim Release { get; } = new(false);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public BlockingDecoder(params string[] blocked)
        {
            _blocked = new HashSet<string>(blocked);
            Entered = new CountdownEvent(_blocked.Count);
            var pixels = new byte[512 * 512 * 4];
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
            var image = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 512 * 4);
            image.Freeze();
            _frame = new AssetCatalog.CachedFrame(image, Enumerable.Repeat((byte)255, 512 * 512).ToArray());
        }

        public int CallsFor(string file) => _calls.TryGetValue(file, out var count) ? count : 0;

        public AssetCatalog.CachedFrame Decode(string path, int height)
        {
            var file = Path.GetFileName(path);
            var calls = _calls.AddOrUpdate(file, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _active);
            var maximum = Volatile.Read(ref _maximumConcurrency);
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrency, active, maximum);
                if (observed == maximum) break;
                maximum = observed;
            }
            try
            {
                if (_blocked.Contains(file))
                {
                    if (calls == 1) Entered.Signal();
                    // Each caller releases this gate in a finally block. Keeping the
                    // decoder blocked until then makes the overlap deterministic.
                    Release.Wait();
                }
                return _frame;
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public void Dispose()
        {
            Release.Dispose();
            Entered.Dispose();
        }
    }
}
