using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.App.Services;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class CompanionMemoryIllustrationTests
{
    [Fact]
    public void PictureIsCachedFrozenAndDoesNotKeepItsFileOpen() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        var path = directory.FilePath("first_day.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96,
            PixelFormats.Bgra32, null, new byte[16], 8)));
        using (var output = File.Create(path)) encoder.Save(output);
        var illustrations = new CompanionMemoryIllustrations(directory.Root);

        var picture = Assert.IsAssignableFrom<BitmapSource>(illustrations.Find("first_day"));
        Assert.True(picture.IsFrozen);
        Assert.True(picture.PixelWidth > 0);
        File.Delete(path);
        Assert.Same(picture, illustrations.Find("first_day"));
    });

    [Fact]
    public void MissingCorruptAndUnknownPicturesAreOptional() => Sta.Run(() =>
    {
        using var directory = new TestDirectory();
        var illustrations = new CompanionMemoryIllustrations(directory.Root);
        File.WriteAllText(directory.FilePath("first_day.png"), "not a PNG");

        Assert.Null(illustrations.Find("first_day"));
        Assert.Null(illustrations.Find("first_return"));
        Assert.Null(illustrations.Find("future_memory"));
        Assert.Null(illustrations.Find("../fallback"));
        // Corrupt files are cached as unavailable, too; refreshing the panel stays cheap.
        Assert.Null(illustrations.Find("first_day"));
    });

    [Fact]
    public void EveryBundledMemoryHasADecodablePackagedPicture() => Sta.Run(() =>
    {
        Assert.NotEmpty(CompanionContent.Default.Memories);
        foreach (var memory in CompanionContent.Default.Memories)
        {
            var picture = CompanionMemoryIllustrations.Default.Find(memory.Id);
            Assert.True(picture is not null, $"Missing or invalid packaged memory illustration: {memory.Id}");
            Assert.True(picture.IsFrozen);
        }
    });
}
