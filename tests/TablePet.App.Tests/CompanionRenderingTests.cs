using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TablePet.Core;
using Xunit;

namespace TablePet.App.Tests;

public sealed class CompanionRenderingTests
{
    [Theory]
    [InlineData(120, 96)]
    [InlineData(120, 144)]
    [InlineData(120, 192)]
    [InlineData(240, 96)]
    [InlineData(240, 144)]
    [InlineData(240, 192)]
    [InlineData(480, 96)]
    [InlineData(480, 144)]
    [InlineData(480, 192)]
    public void EveryBundledLineRendersInsideThePetAtSupportedSizesAndDpi(int size, int dpi) => Sta.Run(() =>
    {
        var visual = new SpeechBubbleVisual();
        visual.Measure(new Size(size, size));
        visual.Arrange(new Rect(0, 0, size, size));
        visual.UpdateLayout();
        var dimension = size * dpi / 96;
        var longest = CompanionContent.Default.Texts.MaxBy(line => CompanionContent.VisibleLength(line.ForHeight(size)))!;
        foreach (var line in CompanionContent.Default.Texts)
        {
            var text = line.ForHeight(size);
            visual.Update(500, text, line.DurationMs(size));
            var bitmap = new RenderTargetBitmap(dimension, dimension, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[dimension * dimension * 4];
            bitmap.CopyPixels(pixels, dimension * 4, 0);
            var visible = 0;
            for (var y = 0; y < dimension; y++)
                for (var x = 0; x < dimension; x++)
                {
                    var alpha = pixels[(y * dimension + x) * 4 + 3];
                    if (x == 0 || y == 0 || x == dimension - 1 || y == dimension - 1)
                        Assert.True(alpha == 0, $"{line.Id} reaches the {size} DIP / {dpi} DPI overlay boundary at ({x},{y}).");
                    if (alpha > 8) visible++;
                }
            Assert.True(visible > dimension * dimension * 0.05, $"{line.Id} did not render a readable bubble.");
            Assert.Equal(text, visual.Text);
            // Optional review artifact; normal test runs do not write into the checkout.
            if (line.Id == longest.Id && Environment.GetEnvironmentVariable("TABLEPET_COMPANION_RENDER_DIR") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                using var output = File.Create(Path.Combine(folder, $"companion-{size}dip-{dpi}dpi.png"));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }
        }
    });
}
