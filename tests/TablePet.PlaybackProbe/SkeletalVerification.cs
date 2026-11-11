using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App;
using TablePet.Core;

internal static partial class Program
{
    // No application startup, tray, settings writes, input injection or live pet instance.
    // PNG decoding and all scaled composition go through the production WPF APIs.
    private static int VerifySkeletal(string oldRoot, string newRoot, string output)
    {
        var sourcePrefix = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(output).StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(output).Equals(Path.GetFullPath(oldRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Verification output must not be inside frozen source assets.");
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, "verification.json");
        var timer = Stopwatch.StartNew();
        var failures = new List<object>();
        var totalFailures = 0;
        void Fail(string stage, string file, object detail)
        {
            totalFailures++;
            if (failures.Count < 30) failures.Add(new { stage, file, detail });
        }
        AssetCatalog? oldCatalog = null, newCatalog = null;
        var nativeResults = new List<object>();
        var scaledResults = new List<object>();
        var timings = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var comparedSlots = 0;
        var hitChecks = 0;
        var temporalChecks = 0;
        var stressChecks = 0;
        long maximumCacheBytes = 0;
        const long cacheBudget = 8L * 1024 * 1024;
        const double displayHeight = 240;
        var sourceManifestBefore = SkeletalHash(File.ReadAllBytes(Path.Combine(oldRoot, "manifest.json")));
        try
        {
            oldCatalog = new AssetCatalog(oldRoot, cacheBudget);
            newCatalog = new AssetCatalog(newRoot, cacheBudget);
            if (oldCatalog.IsFallback || oldCatalog.HasOptionalActionFallback || newCatalog.IsFallback || newCatalog.HasOptionalActionFallback)
                throw new InvalidDataException("Both catalogs must be complete without fallback before verification.");
            if (oldCatalog.CanvasWidth != newCatalog.CanvasWidth || oldCatalog.CanvasHeight != newCatalog.CanvasHeight)
                Fail("canvas", "manifest.json", new { oldCatalog.CanvasWidth, oldCatalog.CanvasHeight });
            if (!oldCatalog.IdleActions.SequenceEqual(newCatalog.IdleActions))
                Fail("idle-action-selection", "manifest.json", "Idle actions or weights changed.");
            var oldNames = oldCatalog.Clips.Keys.Order(StringComparer.Ordinal).ToArray();
            if (!oldNames.SequenceEqual(newCatalog.Clips.Keys.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Clip identities changed.");
            void CheckClip(AnimationClip before, AnimationClip after)
            {
                if (before.Loop != after.Loop || before.SampleFps != after.SampleFps || before.UniformFrameTiming != after.UniformFrameTiming
                    || before.DurationMs != after.DurationMs || before.Frames.Count != after.Frames.Count)
                    Fail("timeline", before.Name, "Loop, sampling rate, total duration or slot count changed.");
                for (var i = 0; i < Math.Min(before.Frames.Count, after.Frames.Count); i++)
                {
                    comparedSlots++;
                    if (before.Frames[i] != after.Frames[i]) Fail("timeline-slot", before.Name, new { index = i, expected = before.Frames[i], actual = after.Frames[i] });
                }
            }
            foreach (var name in oldNames) CheckClip(oldCatalog.Clips[name], newCatalog.Clips[name]);
            if (!oldCatalog.Decorations.Keys.Order(StringComparer.Ordinal).SequenceEqual(newCatalog.Decorations.Keys.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Decoration identities changed.");
            foreach (var (name, before) in oldCatalog.Decorations)
            {
                var after = newCatalog.Decorations[name];
                if (before.CanvasWidth != after.CanvasWidth || before.CanvasHeight != after.CanvasHeight
                    || before.OffsetX != after.OffsetX || before.OffsetY != after.OffsetY)
                    Fail("decoration-layout", name, "Decoration canvas or offset changed.");
                CheckClip(before.Clip, after.Clip);
            }

            var files = oldCatalog.AllFrameFiles.ToArray();
            if (!files.Order(StringComparer.Ordinal).SequenceEqual(newCatalog.AllFrameFiles.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Unique frame bindings changed.");
            oldCatalog.CacheForSize(displayHeight, 1);
            newCatalog.CacheForSize(displayHeight, 1);
            // Native pass deliberately leaves prefetch disabled, yielding on-demand
            // cache-miss timings. CacheForSize warms a small entry-pose set; those
            // hits are explicitly excluded from the decode timing samples.
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                var originalMisses = oldCatalog.SynchronousCacheMisses;
                var rewrittenMisses = newCatalog.SynchronousCacheMisses;
                var stamp = Stopwatch.GetTimestamp();
                var original = oldCatalog.Image(file);
                var originalMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                stamp = Stopwatch.GetTimestamp();
                var rewritten = newCatalog.Image(file);
                var rewrittenMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                var provider = SkeletalProvider(file);
                if (oldCatalog.SynchronousCacheMisses > originalMisses) AddTiming("original:" + provider, originalMs);
                if (newCatalog.SynchronousCacheMisses > rewrittenMisses) AddTiming("skeletal:" + provider, rewrittenMs);
                var originalPixels = SkeletalPixels(original);
                var rewrittenPixels = SkeletalPixels(rewritten);
                var difference = SkeletalDifference(originalPixels, rewrittenPixels);
                var hash = SkeletalHash(originalPixels); expectedHashes[file] = hash;
                if (original.PixelWidth != rewritten.PixelWidth || original.PixelHeight != rewritten.PixelHeight || difference.ChangedBytes != 0)
                {
                    Fail("native-pixels", file, difference);
                    if (totalFailures <= 12)
                    {
                        SavePng(original, Path.Combine(output, $"mismatch-{index:0000}-original.png"));
                        SavePng(rewritten, Path.Combine(output, $"mismatch-{index:0000}-skeletal.png"));
                    }
                }
                nativeResults.Add(new { file, provider, width = original.PixelWidth, height = original.PixelHeight,
                    expectedBgraSha256 = hash, actualBgraSha256 = SkeletalHash(rewrittenPixels), difference });
                // Same-point alpha hit testing, including half-pixel locations,
                // exterior points and pixels bordering the actual alpha threshold.
                for (var y = -1; y <= 17; y++) for (var x = -1; x <= 17; x++)
                    CheckHit(file, (x + .5) / 17, (y + .5) / 17);
                var edgeSamples = 0;
                for (var p = 0; p < originalPixels.Length / 4 && edgeSamples < 24; p++)
                    if (originalPixels[p * 4 + 3] is >= 92 and <= 100)
                    {
                        CheckHit(file, (p % original.PixelWidth + .5) / original.PixelWidth,
                            (p / original.PixelWidth + .5) / original.PixelHeight);
                        edgeSamples++;
                    }
                maximumCacheBytes = Math.Max(maximumCacheBytes, newCatalog.CachedBytes);
                if (index % 100 == 0 || index == files.Length - 1)
                    Console.WriteLine($"Native WPF comparison {index + 1}/{files.Length}; failures={totalFailures}");
            }

            // Use the same production clock/player on both catalogs. Cover three
            // display rates, shifted launch phases, and looping across two cycles.
            foreach (var name in oldNames)
            foreach (var hz in new[] { 60, 120, 144 })
            foreach (var phase in new[] { 0d, .125, .499, .875, .999 })
            {
                var clock = new SkeletalProbeClock();
                var before = new AnimationPlayer(clock); var after = new AnimationPlayer(clock);
                var clip = oldCatalog.Clips[name];
                before.Play(clip); after.Play(newCatalog.Clips[name]);
                before.Tick(TimeSpan.Zero); after.Tick(TimeSpan.Zero);
                var duration = clip.DurationMs * (clip.Loop && clip.Frames.Count > 1 ? 2 : 1);
                for (var sample = 0; sample <= Math.Ceiling(duration * hz / 1000d) + 1; sample++)
                {
                    var elapsed = (sample + phase) * 1000 / hz;
                    clock.PreciseMilliseconds = elapsed + (sample % 2 == 0 ? -.8 : .7);
                    var presentation = TimeSpan.FromTicks((long)Math.Round(elapsed * TimeSpan.TicksPerMillisecond));
                    before.Tick(presentation); after.Tick(presentation); temporalChecks++;
                    if (before.FrameIndex != after.FrameIndex || before.CurrentFrame != after.CurrentFrame
                        || before.IsCompleted != after.IsCompleted || before.PresentedElapsedMs != after.PresentedElapsedMs)
                        Fail("presentation-time", name, new { hz, phase, sample, elapsed });
                }
            }

            // Capture actual WPF high-quality scaling over both light and dark
            // backgrounds. DrawingContext uses the same image transforms and layer
            // positions as the pet; no alternate image library performs composition.
            foreach (var name in oldNames)
            {
                var clip = oldCatalog.Clips[name];
                var indices = new[] { 0, clip.Frames.Count / 4, clip.Frames.Count / 2, clip.Frames.Count * 3 / 4, clip.Frames.Count - 1 }.Distinct();
                foreach (var index in indices)
                foreach (var dark in new[] { false, true })
                {
                    var before = SkeletalScene(oldCatalog, name, index, displayHeight, dark);
                    var after = SkeletalScene(newCatalog, name, index, displayHeight, dark);
                    var difference = SkeletalDifference(SkeletalPixels(before), SkeletalPixels(after));
                    var label = name + "-" + index.ToString("000") + (dark ? "-dark" : "-light");
                    if (difference.ChangedBytes != 0) Fail("scaled-wpf-pixels", label, difference);
                    scaledResults.Add(new { clip = name, index, background = dark ? "dark" : "light", displayHeightDip = displayHeight,
                        expectedBgraSha256 = SkeletalHash(SkeletalPixels(before)), actualBgraSha256 = SkeletalHash(SkeletalPixels(after)), difference });
                    if (index == clip.Frames.Count / 2 || difference.ChangedBytes != 0)
                    {
                        // Two equal renders side by side provide reviewable samples.
                        var contact = Draw(before.PixelWidth * 2, before.PixelHeight, context =>
                        {
                            context.DrawImage(before, new Rect(0, 0, before.PixelWidth, before.PixelHeight));
                            context.DrawImage(after, new Rect(before.PixelWidth, 0, after.PixelWidth, after.PixelHeight));
                        });
                        SavePng(contact, Path.Combine(output, label + "-comparison.png"));
                    }
                }
            }
            Console.WriteLine($"Scaled WPF comparisons={scaledResults.Count}; timing checks={temporalChecks}; failures={totalFailures}");

            // Activate the production prefetcher at the deliberately small budget.
            // Check every slot forward, then seek to each midpoint and walk backward.
            newCatalog.CacheForSize(displayHeight, 1);
            foreach (var name in oldNames)
            {
                var clip = newCatalog.Clips[name];
                for (var i = 0; i < clip.Frames.Count; i++) StressFrame(clip, i, "sequential-prefetch");
                newCatalog.StopPrefetch();
                for (var i = clip.Frames.Count / 2; i >= 0; i--) StressFrame(clip, i, "reverse-from-middle");
                newCatalog.StopPrefetch();
                Console.WriteLine($"Cache traversal completed: {name}; checks={stressChecks}; failures={totalFailures}");
            }
            // Deterministic Fisher-Yates shuffle visits every unique binding and
            // forces bounded dependency reconstruction after unrelated requests.
            var random = new Random(904060);
            var shuffled = files.ToArray();
            for (var i = shuffled.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1); (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            foreach (var file in shuffled) StressFile(file, "random-seek");
            if (oldCatalog.HasFrameFallback || newCatalog.HasFrameFallback) Fail("fallback", "catalog", "A frame used the fallback during verification.");
            if (maximumCacheBytes > cacheBudget) Fail("cache-budget", "catalog", new { maximumCacheBytes, cacheBudget });
            if (sourceManifestBefore != SkeletalHash(File.ReadAllBytes(Path.Combine(oldRoot, "manifest.json"))))
                Fail("source-integrity", "manifest.json", "Frozen source manifest changed during verification.");

            void CheckHit(string file, double x, double y)
            {
                hitChecks++;
                if (oldCatalog.Hit(file, x, y) != newCatalog.Hit(file, x, y)) Fail("alpha-hit", file, new { x, y });
            }
            void AddTiming(string key, double value)
            {
                if (!timings.TryGetValue(key, out var samples)) timings[key] = samples = [];
                samples.Add(value);
            }
            void StressFrame(AnimationClip clip, int index, string mode)
            {
                newCatalog.PreparePlayback(clip, index);
                StressFile(clip.Frames[index].File, mode);
                if (newCatalog.Decorations.TryGetValue(clip.Name, out var decoration))
                    StressFile(decoration.Clip.Frames[index].File, mode);
            }
            void StressFile(string file, string mode)
            {
                var actual = SkeletalHash(SkeletalPixels(newCatalog.Image(file))); stressChecks++;
                if (actual != expectedHashes[file]) Fail(mode, file, new { expected = expectedHashes[file], actual });
                maximumCacheBytes = Math.Max(maximumCacheBytes, newCatalog.CachedBytes);
            }
        }
        catch (Exception error)
        {
            Fail("exception", "verification", error.ToString());
        }
        finally
        {
            oldCatalog?.StopPrefetch(); newCatalog?.StopPrefetch();
        }
        var oldRuntimeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(oldRoot, "manifest.json"), Path.Combine(oldRoot, "fallback.png"), Path.Combine(oldRoot, "tray.ico") };
        foreach (var file in oldCatalog?.AllFrameFiles ?? []) oldRuntimeFiles.Add(Path.Combine(oldRoot, file));
        var oldBytes = oldRuntimeFiles.Where(File.Exists).Sum(file => new FileInfo(file).Length);
        var newBytes = Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        var report = new
        {
            passed = totalFailures == 0, oldAssetRoot = oldRoot, newAssetRoot = newRoot, elapsedSeconds = timer.Elapsed.TotalSeconds,
            sourceManifestSha256 = sourceManifestBefore, clipCount = oldCatalog?.Clips.Count ?? 0, comparedSlots,
            uniqueNativeFrames = nativeResults.Count, hitChecks, temporalChecks, scaledComparisons = scaledResults.Count, stressChecks,
            failureCount = totalFailures, failures, cacheBudgetBytes = cacheBudget, maximumObservedCacheBytes = maximumCacheBytes,
            storage = new { oldRuntimeAssetsBytes = oldBytes, newRuntimeAssetsBytes = newBytes, reductionPercent = oldBytes == 0 ? 0 : 100 * (1 - (double)newBytes / oldBytes) },
            onDemandDecodeMilliseconds = timings.ToDictionary(pair => pair.Key, pair => new
            {
                samples = pair.Value.Count, p50 = Percentile(pair.Value, .5), p95 = Percentile(pair.Value, .95), max = pair.Value.Max(), total = pair.Value.Sum()
            }),
            nativeFrames = nativeResults, scaledFrames = scaledResults,
            limitation = "Native pixels, alpha hits and offscreen WPF high-quality composition are exact comparisons. Timing uses deterministic production player clocks at 60/120/144 Hz. Decode timings are on-demand fetches in this process, not display refresh performance. The observed 8 MiB cache budget covers only the outer decoded-frame LRU, not the renderer's separate source-pose/texture caches, WPF native allocations or temporary buffers. This probe does not measure DWM, test live mouse input, alter settings or start/stop the user's desktop pet."
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.passed, report.uniqueNativeFrames, report.comparedSlots, report.hitChecks,
            report.temporalChecks, report.scaledComparisons, report.stressChecks, report.failureCount, report.elapsedSeconds, report.storage, reportPath }));
        return report.passed ? 0 : 2;
    }

    private static string SkeletalProvider(string file) => file.StartsWith("frames/firefly-v3/", StringComparison.Ordinal) ? "firefly"
        : file.StartsWith("frames/work-v1/", StringComparison.Ordinal) ? "work" : "legacy";

    private static string SkeletalHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] SkeletalPixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return bytes;
    }

    private sealed record SkeletalPixelDifference(long ChangedBytes, long ChangedPixels, long ChangedAlphaBytes, int MaximumChannelDifference);

    private static SkeletalPixelDifference SkeletalDifference(byte[] expected, byte[] actual)
    {
        long channels = 0, pixels = 0, alpha = 0; var maximum = 0;
        for (var offset = 0; offset < Math.Max(expected.Length, actual.Length); offset += 4)
        {
            var changed = false;
            for (var c = 0; c < 4; c++)
            {
                var index = offset + c;
                if (index >= expected.Length || index >= actual.Length)
                {
                    channels++; changed = true; if (c == 3) alpha++; maximum = 255; continue;
                }
                var delta = Math.Abs(expected[index] - actual[index]);
                if (delta == 0) continue;
                channels++; changed = true; if (c == 3) alpha++; maximum = Math.Max(maximum, delta);
            }
            if (changed) pixels++;
        }
        return new(channels, pixels, alpha, maximum);
    }

    private static BitmapSource SkeletalScene(AssetCatalog catalog, string name, int index, double height, bool dark)
    {
        const int size = 360;
        const double originX = 60, originY = 75;
        var clip = catalog.Clips[name];
        var time = clip.Frames.Take(index).Sum(frame => (double)frame.DurationMs);
        var scale = height / catalog.CanvasHeight;
        var width = catalog.CanvasWidth * scale;
        double offset = 0, propsOpacity = 0;
        double Quintic(double progress) { var t = Math.Clamp(progress, 0, 1); return t * t * t * (10 + t * (-15 + 6 * t)); }
        if (name == "work_loop") { offset = (Math.Cos(2 * Math.PI * time / 4800) - 1) * scale; propsOpacity = 1; }
        if (name == "work_enter") propsOpacity = Quintic(time / 400);
        if (name == "work_exit") { propsOpacity = 1 - Quintic(time / 400); offset = (Math.Cos(2 * Math.PI * (7075 + time) / 4800) - 1) * propsOpacity * scale; }
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(29, 33, 43) : Color.FromRgb(246, 241, 233)), null, new Rect(0, 0, size, size));
            draw.DrawImage(catalog.Image(clip.Frames[index].File), new Rect(originX, originY + offset, width, height));
            if (catalog.Decorations.TryGetValue(name, out var decoration))
                draw.DrawImage(catalog.Image(decoration.Clip.Frames[index].File), new Rect(originX + decoration.OffsetX * scale,
                    originY + decoration.OffsetY * scale, decoration.CanvasWidth * scale, decoration.CanvasHeight * scale));
            if (propsOpacity > 0 && catalog.Clips.TryGetValue("work_props", out var props))
            {
                draw.PushOpacity(propsOpacity);
                draw.DrawImage(catalog.Image(props.Frames[0].File), new Rect(originX, originY, width, height));
                draw.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private sealed class SkeletalProbeClock : IClock
    {
        public double PreciseMilliseconds { get; set; }
        public long NowMilliseconds => (long)PreciseMilliseconds;
    }
}
