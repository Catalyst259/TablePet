using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TablePet.App.Skeletal;

var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "skeletal", "legacy", "legacy.json")));
var archiveSha256 = metadata.RootElement.GetProperty("archiveSha256").GetString();
var watch = Stopwatch.StartNew();
var renderer = new LegacyRenderer(root);
var constructionMs = watch.Elapsed.TotalMilliseconds;
var files = renderer.Files.ToArray();
var durations = new List<double>();
foreach (var file in files)
{
    watch.Restart();
    var pixels = renderer.Render(file); // Decoding checks the frozen RGBA SHA-256.
    durations.Add(watch.Elapsed.TotalMilliseconds);
    if (pixels.Length != 512 * 512 * 4) throw new Exception("Wrong raster length.");
}
double Percentile(IEnumerable<double> values, double quantile)
{
    var sorted = values.Order().ToArray();
    return Math.Round(sorted[(int)Math.Min(sorted.Length - 1, quantile * sorted.Length)], 3);
}
var random = new Random(7301);
var seek = new List<double>();
foreach (var file in files.OrderBy(_ => random.Next()).Take(120))
{
    watch.Restart();
    renderer.Render(file);
    seek.Add(watch.Elapsed.TotalMilliseconds);
}
// Cache ownership must remain internal even if a caller mutates returned data.
var original = renderer.Render(files[0]);
var expected = SHA256.HashData(original);
Array.Fill(original, (byte)37);
if (!SHA256.HashData(renderer.Render(files[0])).AsSpan().SequenceEqual(expected))
    throw new Exception("Caller can corrupt cached skeleton pixels.");
Parallel.ForEach(files.Take(36), new ParallelOptions { MaxDegreeOfParallelism = 2 }, file => renderer.Render(file));
if (renderer.CachedBytes > 24 * 1024 * 1024) throw new Exception("Decoded cache exceeded its budget.");
var report = new
{
    AssetRoot = root, ArchiveSha256 = archiveSha256,
    TimingNote = "Decode timings are diagnostic wall times, not presentation cadence; use the separate playback probe for frame-rate acceptance.",
    AllRgbaHashesExact = true, CheckedPoses = files.Length, ConcurrentPrefetchPassed = true,
    CallerMutationIsolationPassed = true, CacheBytes = renderer.CachedBytes, WarmupPoseCount = renderer.WarmupFiles.Count,
    ArchiveResidentBytes = 0, BindingBytes = 512 * 512 * 3 * 4,
    ConstructionMs = Math.Round(constructionMs, 3),
    SequentialTotalMs = Math.Round(durations.Sum(), 3), SequentialP50Ms = Percentile(durations, .5),
    SequentialP95Ms = Percentile(durations, .95), RandomSeekP50Ms = Percentile(seek, .5),
    RandomSeekP95Ms = Percentile(seek, .95), RandomSeekMaxMs = seek.Max()
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(output, json + Environment.NewLine);
Console.WriteLine(json);
