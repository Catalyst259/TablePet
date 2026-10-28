using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TablePet.App;
using TablePet.Core;

// Exercises the actual WPF decoder/cache and animation clock without a window, tray,
// settings, input injection or instance signals. This does not measure DWM presentation.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: TablePet.PlaybackProbe <asset-root> [sample-hz=240] [height-dip=240] [dpi=1.5]");
            return 1;
        }
        var hz = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 240;
        var height = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 240;
        var dpi = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 1.5;
        if (hz is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(hz));
        var initialization = Stopwatch.StartNew();
        var catalog = new AssetCatalog(Path.GetFullPath(args[0]));
        catalog.CacheForSize(height, dpi);
        initialization.Stop();
        var clock = new StopwatchClock();
        var player = new AnimationPlayer(clock);
        var results = new List<object>();
        var fetchTimes = new List<double>();
        var actualIntervals = new List<double>();
        var rendered = 0;
        long maxCache = catalog.CachedBytes;
        player.FrameChanged += frame =>
        {
            var start = Stopwatch.GetTimestamp();
            catalog.PreparePlayback(player.CurrentClip!, player.FrameIndex);
            var bitmap = catalog.Image(frame.File);
            if (!bitmap.IsFrozen) throw new InvalidOperationException("The decoder returned a thread-affine image.");
            fetchTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            maxCache = Math.Max(maxCache, catalog.CachedBytes);
            rendered++;
        };
        foreach (var name in new[] { "rise", "hold", "settle", "fall_asleep", "sleep_loop", "wake" }
            .Concat(catalog.Clips.ContainsKey("wink") ? new[] { "wink" } : [])
            .Concat(catalog.IdleActions.Select(action => action.Clip)).Distinct())
        {
            fetchTimes.Clear(); actualIntervals.Clear(); rendered = 0;
            var beforeMisses = catalog.SynchronousCacheMisses;
            var clip = catalog.Clips[name];
            var timer = Stopwatch.StartNew();
            player.Play(clip);
            long previousSample = Stopwatch.GetTimestamp();
            var sample = 1;
            while (timer.Elapsed.TotalMilliseconds < clip.DurationMs)
            {
                var target = sample * 1000d / hz;
                while (timer.Elapsed.TotalMilliseconds < target)
                {
                    // Windows may round Sleep(1) to ~16 ms. Active wait is deliberate
                    // in this short-lived probe, never used by the desktop application.
                    if (target - timer.Elapsed.TotalMilliseconds > 20) Thread.Sleep(1);
                    else Thread.SpinWait(32);
                }
                var now = Stopwatch.GetTimestamp();
                actualIntervals.Add((now - previousSample) * 1000d / Stopwatch.Frequency);
                previousSample = now;
                player.Tick();
                // Skip expired sample slots after a slow decode instead of dispatching a burst.
                sample = Math.Max(sample + 1, (int)Math.Floor(timer.Elapsed.TotalMilliseconds * hz / 1000) + 1);
            }
            results.Add(new
            {
                clip = name,
                frames = clip.Frames.Count,
                durationMs = clip.DurationMs,
                elapsedMs = timer.Elapsed.TotalMilliseconds,
                samples = actualIntervals.Count,
                sampledHz = actualIntervals.Count * 1000d / timer.Elapsed.TotalMilliseconds,
                displayedFrameChanges = rendered,
                synchronousCacheMisses = catalog.SynchronousCacheMisses - beforeMisses,
                fetchP95Ms = Percentile(fetchTimes, .95),
                fetchMaxMs = fetchTimes.Max(),
                sampleIntervalP95Ms = Percentile(actualIntervals, .95),
                cachedFrames = catalog.CachedFrameCount,
                cachedMiB = catalog.CachedBytes / 1048576d
            });
        }
        catalog.StopPrefetch();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed = !catalog.IsFallback && !catalog.HasFrameFallback && !catalog.HasOptionalActionFallback && maxCache <= catalog.CacheBudgetBytes,
            requestedSampleHz = hz,
            measurementUsesActiveWait = true,
            heightDip = height,
            dpiScale = dpi,
            initializationMs = initialization.Elapsed.TotalMilliseconds,
            cacheBudgetMiB = catalog.CacheBudgetBytes / 1048576d,
            peakDecodedPixelCacheMiB = maxCache / 1048576d,
            processWorkingSetMiB = Process.GetCurrentProcess().WorkingSet64 / 1048576d,
            clips = results,
            limitation = "Headless timing/decoding probe only. The sampling rate is requested by this probe and does not establish actual window/compositor presentation FPS. Cache budget excludes WPF native allocations and transient decode/GC overhead."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return catalog.IsFallback || catalog.HasFrameFallback || catalog.HasOptionalActionFallback || maxCache > catalog.CacheBudgetBytes ? 2 : 0;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * percentile) - 1)];
    }
}
