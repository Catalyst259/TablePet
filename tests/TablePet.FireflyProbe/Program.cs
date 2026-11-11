using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using TablePet.App.Skeletal;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : "../../..");
var renderer = new FireflyRenderer(Path.Combine(root, "assets/strawberry-cat"));
using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "reference/firefly-expected.json")));
var output = Path.Combine(root, "artifacts/firefly-csharp"); Directory.CreateDirectory(output);
var mismatches = new List<object>(); var times = new List<double>(); var total = Stopwatch.StartNew();
var checks = new List<string>();
void Check(bool result, string name) { if (!result) throw new InvalidOperationException(name); checks.Add(name); }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException or EndOfStreamException or JsonException or KeyNotFoundException) { checks.Add(name); return; }
    throw new InvalidOperationException("Accepted " + name);
}
Reject(() => renderer.RenderCat(-1), "Negative cat slot rejected");
Reject(() => renderer.RenderCat(600), "Cat slot beyond timeline rejected");
Reject(() => renderer.RenderOverlay(-1), "Negative overlay slot rejected");
Reject(() => renderer.RenderOverlay(600), "Overlay slot beyond timeline rejected");
Reject(() => renderer.Render("unknown"), "Unknown file rejected");
foreach (var index in new[] { 0, 80, 320, 365, 599 })
{
    var first = renderer.RenderCat(index); var original = (byte[])first.Clone(); System.Array.Fill(first, (byte)71);
    Check(original.AsSpan().SequenceEqual(renderer.RenderCat(index)), $"Returned cat {index} cannot mutate shared attachments");
    var fly = renderer.RenderOverlay(index); var originalFly = (byte[])fly.Clone(); System.Array.Fill(fly, (byte)52);
    Check(originalFly.AsSpan().SequenceEqual(renderer.RenderOverlay(index)), $"Returned overlay {index} cannot mutate shared attachments");
}
var failures = new ConcurrentQueue<string>();
Parallel.For(0, 192, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
{
    var index = i * 37 % 600;
    foreach (var kind in new[] { "cat", "overlay" })
    {
        var rgba = kind == "cat" ? renderer.RenderCat(index) : renderer.RenderOverlay(index);
        if (Convert.ToHexStringLower(SHA256.HashData(rgba)) != expected.RootElement[index].GetProperty(kind).GetString()) failures.Enqueue($"{kind}/{index}");
    }
});
Check(failures.IsEmpty, "384 concurrent renders are deterministic");
var fixture = Path.Combine(Path.GetTempPath(), "TablePet-Firefly-" + Guid.NewGuid().ToString("N"));
try
{
    var fixtureRoot = Path.Combine(fixture, "skeletal/firefly"); Directory.CreateDirectory(fixtureRoot);
    var sourceRoot = Path.Combine(root, "assets/strawberry-cat/skeletal/firefly");
    var originalJson = File.ReadAllText(Path.Combine(sourceRoot, "firefly.json"));
    var originalData = File.ReadAllBytes(Path.Combine(sourceRoot, "textures.bin.z"));
    void BadJson(string name, Action<JsonNode> mutate)
    {
        var node = JsonNode.Parse(originalJson)!; mutate(node);
        File.WriteAllText(Path.Combine(fixtureRoot, "firefly.json"), node.ToJsonString());
        File.WriteAllBytes(Path.Combine(fixtureRoot, "textures.bin.z"), originalData);
        Reject(() => new FireflyRenderer(fixture), name);
    }
    BadJson("Unsupported archive version rejected", n => n["version"] = 999);
    BadJson("Oversized decompression budget rejected", n => n["textureBytes"] = int.MaxValue);
    BadJson("Negative attachment offset rejected", n => n["buffers"]!["body/base"]!["offset"] = -1);
    BadJson("Oversized attachment range rejected", n => n["buffers"]!["body/base"]!["length"] = int.MaxValue);
    BadJson("Wrong texture digest rejected", n => n["textureSha256"] = new string('0', 64));
    BadJson("Out-of-range bone value rejected", n => n["frames"]![300]!["body"]!["prepare"] = -1);
    BadJson("Invalid sprite reference rejected", n => n["frames"]![300]!["fly"]!["sprite"] = 10000);
    File.WriteAllText(Path.Combine(fixtureRoot, "firefly.json"), originalJson);
    File.WriteAllBytes(Path.Combine(fixtureRoot, "textures.bin.z"), originalData[..(originalData.Length / 2)]);
    Reject(() => new FireflyRenderer(fixture), "Truncated attachment archive rejected");
}
finally
{
    // Only the explicitly created random test fixture is removed.
    if (Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture, true);
}
for (var index = 0; index < 600; index++)
{
    var sw = Stopwatch.StartNew();
    foreach (var kind in new[] { "cat", "overlay" })
    {
        var rgba = kind == "cat" ? renderer.RenderCat(index) : renderer.RenderOverlay(index);
        var hash = Convert.ToHexStringLower(SHA256.HashData(rgba));
        var want = expected.RootElement[index].GetProperty(kind).GetString();
        if (hash == want) continue;
        mismatches.Add(new { index, kind, expected = want, actual = hash });
        using var file = File.Create(Path.Combine(output, $"{kind}-{index:D3}.rgba.z"));
        using var z = new ZLibStream(file, CompressionLevel.SmallestSize); z.Write(rgba);
    }
    times.Add(sw.Elapsed.TotalMilliseconds);
    if (index % 100 == 0) Console.WriteLine($"Verified {index}/600, mismatches {mismatches.Count}");
}
times.Sort();
var report = new { checkedFrames = 1200, concurrentFrames = 384, checks, mismatches, residentTextureBytes = renderer.ResidentTextureBytes, elapsedSeconds = total.Elapsed.TotalSeconds, p50Milliseconds = times[300], p95Milliseconds = times[570] };
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Firefly: {1200 - mismatches.Count}/1200 exact, {total.Elapsed.TotalSeconds:F1}s; p95 paired render {times[570]:F1}ms");
return mismatches.Count == 0 ? 0 : 1;
