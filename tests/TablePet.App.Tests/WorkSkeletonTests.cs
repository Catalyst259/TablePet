using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TablePet.App.Skeletal;
using Xunit;

namespace TablePet.App.Tests;

public sealed class WorkSkeletonTests
{
    [Fact]
    public void EveryWorkAttachmentMatchesFrozenSourceRgbaExactly()
    {
        var root = FindAssets();
        var renderer = new WorkRenderer(root);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "skeletal/work/work.json")));
        var records = metadata.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(42, records.Length);
        Parallel.ForEach(records, frame =>
        {
            var file = frame.GetProperty("file").GetString()!;
            Assert.True(renderer.Contains(file));
            var rgba = renderer.Render(file);
            Assert.Equal(512 * 512 * 4, rgba.Length);
            Assert.Equal(frame.GetProperty("rgbaSha256").GetString(),
                Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant());
        });
    }

    [Fact]
    public void ReturnedFramesDoNotMutateSharedSkeletonAttachments()
    {
        var renderer = new WorkRenderer(FindAssets());
        var file = renderer.Files.First();
        var expected = renderer.Render(file);
        var mutable = renderer.Render(file);
        Array.Fill(mutable, (byte)17);
        Assert.Equal(expected, renderer.Render(file));
    }

    [Fact]
    public void DamagedSkeletonArchiveFailsBeforeRendering()
    {
        using var temporary = new TestDirectory();
        var directory = Path.Combine(temporary.Root, "skeletal/work");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(FindAssets(), "skeletal/work");
        File.Copy(Path.Combine(source, "work.json"), Path.Combine(directory, "work.json"));
        var bytes = File.ReadAllBytes(Path.Combine(source, "work.bin"));
        bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(Path.Combine(directory, "work.bin"), bytes);
        Assert.Throws<InvalidDataException>(() => new WorkRenderer(temporary.Root));
    }

    private static string FindAssets()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var root = Path.Combine(directory.FullName, "assets/strawberry-cat");
            if (File.Exists(Path.Combine(root, "skeletal/work/work.json"))) return root;
        }
        throw new DirectoryNotFoundException("Production work skeleton assets were not found.");
    }
}
