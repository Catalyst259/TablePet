using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TablePet.App;
using TablePet.Core;

internal static partial class Program
{
    private static int AuditCadence(string assetRoot, string reportPath)
    {
        var catalog = new AssetCatalog(assetRoot);
        if (catalog.IsFallback || catalog.HasOptionalActionFallback)
            throw new InvalidDataException("Cadence audit requires complete runtime assets.");
        var clock = new CadenceAuditClock();
        var player = new AnimationPlayer(clock);
        var reports = new List<object>();
        var failures = new List<object>();
        var checks = 0;
        var failedChecks = 0;
        foreach (var (name, clip) in catalog.Clips)
        {
            var fps = clip.SampleFps;
            var duration = clip.DurationMs;
            var staticPose = clip.Frames.Count == 1;
            if (!staticPose && fps is not > 0)
                throw new InvalidDataException(name + " has no declared sampling cadence.");
            if (!staticPose)
            {
                long start = 0;
                for (var i = 0; i < clip.Frames.Count; i++)
                {
                    var expectedStart = Math.Round(i * 1000d / fps!.Value, MidpointRounding.AwayFromZero);
                    var expectedEnd = Math.Min(duration, Math.Round((i + 1d) * 1000 / fps.Value, MidpointRounding.AwayFromZero));
                    if (start != expectedStart || start + clip.Frames[i].DurationMs != expectedEnd)
                        throw new InvalidDataException(name + " has nonuniform timing; inspect its intentional holds separately.");
                    start += clip.Frames[i].DurationMs;
                }
            }
            var clipChecks = 0;
            var clipFailures = 0;
            foreach (var displayHz in new[] { 60, 120, 144 })
            foreach (var phase in new[] { 0d, 0.125, 0.499, 0.875, 0.999 })
            {
                const double origin = 1234.375;
                var presentationOrigin = TimeSpan.FromTicks(123456789);
                clock.PreciseMilliseconds = origin;
                player.Play(clip);
                player.Tick(presentationOrigin);
                var end = duration * (clip.Loop && !staticPose ? 2 : 1);
                var samples = (int)Math.Ceiling(end * displayHz / 1000d) + 1;
                for (var sample = 0; sample <= samples; sample++)
                {
                    var elapsed = (sample + phase) * 1000d / displayHz;
                    var jitter = sample == 0 ? 0 : sample % 2 == 0 ? -0.8 : 0.7;
                    clock.PreciseMilliseconds = origin + elapsed + jitter;
                    player.Tick(presentationOrigin + TimeSpan.FromTicks((long)Math.Round(elapsed * TimeSpan.TicksPerMillisecond)));
                    var position = clip.Loop ? elapsed % duration : Math.Min(elapsed, duration);
                    if (clip.Loop && duration - position < 0.0000001) position = 0;
                    var expected = staticPose ? 0 : Math.Min(clip.Frames.Count - 1,
                        (int)Math.Floor(position * fps!.Value / 1000 + 0.00000001));
                    var shouldComplete = !clip.Loop && elapsed >= duration - 0.00000001;
                    checks++; clipChecks++;
                    if (player.FrameIndex != expected || player.IsCompleted != shouldComplete)
                    {
                        failedChecks++; clipFailures++;
                        if (failures.Count < 20) failures.Add(new { clip = name, displayHz, phase, sample,
                            elapsedMs = elapsed, expectedFrame = expected, actualFrame = player.FrameIndex,
                            expectedCompletion = shouldComplete, actualCompletion = player.IsCompleted });
                    }
                }
            }
            reports.Add(new { clip = name, frames = clip.Frames.Count, durationMs = duration, sampleFps = fps,
                staticPose, loop = clip.Loop, cases = 15, checks = clipChecks, failedChecks = clipFailures });
        }
        var report = new { passed = failedChecks == 0, assetRoot,
            manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(assetRoot, "manifest.json")))),
            displayRates = new[] { 60, 120, 144 }, phasesPerRate = 5, checks, failedChecks,
            clips = reports, failures,
            limitation = "Deterministic real-catalog/player audit with quantized presentation timestamps and CPU callback jitter. Does not measure actual DWM rendering or image decoding; generated interrupt paths are covered by controller regression tests." };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report.passed, clips = reports.Count, report.checks, report.failedChecks, reportPath }));
        return report.passed ? 0 : 2;
    }

    private sealed class CadenceAuditClock : IClock
    {
        public double PreciseMilliseconds { get; set; }
        public long NowMilliseconds => (long)PreciseMilliseconds;
    }
}
