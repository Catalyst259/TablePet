using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TablePet.App;

/// <summary>Three cached vector letters in the same 512-unit canvas as the pet artwork.</summary>
internal sealed class SleepIndicatorVisual : FrameworkElement
{
    private const int CycleMs = 3600;
    private readonly ContainerVisual _canvas = new();
    private readonly ScaleTransform _canvasScale = new();
    private readonly Letter[] _letters;
    private long _lastTime = -1;

    public SleepIndicatorVisual()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _canvas.Transform = _canvasScale;
        AddVisualChild(_canvas);

        var fill = new SolidColorBrush(Color.FromRgb(0x76, 0x68, 0x8e));
        var outline = new SolidColorBrush(Color.FromRgb(0xff, 0xf8, 0xef));
        fill.Freeze();
        outline.Freeze();
        var pen = new Pen(outline, 3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        _letters =
        [
            CreateLetter(392, 236, 26, fill, pen),
            CreateLetter(421, 207, 34, fill, pen),
            CreateLetter(454, 176, 44, fill, pen)
        ];
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _canvas : throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _canvasScale.ScaleX = ActualWidth / 512;
        _canvasScale.ScaleY = ActualHeight / 512;
    }

    public void Update(long elapsedMs)
    {
        var time = Math.Max(0, elapsedMs) % CycleMs;
        if (time == _lastTime) return;
        _lastTime = time;
        var fadeOut = 1 - Smooth((time - 2700) / 600d);
        for (var i = 0; i < _letters.Length; i++)
        {
            var letter = _letters[i];
            var start = 100 + i * 550;
            var fadeIn = Smooth((time - start) / 300d);
            var drift = Math.Clamp((time - start) / (3300d - start), 0, 1);
            letter.Visual.Opacity = 0.94 * fadeIn * fadeOut;
            letter.Scale.ScaleX = letter.Scale.ScaleY = 0.94 + 0.06 * fadeIn;
            letter.Position.X = letter.X + 5 * drift;
            letter.Position.Y = letter.Y - 5 * drift;
        }
    }

    private Letter CreateLetter(double x, double y, double emSize, Brush fill, Pen outline)
    {
        var text = new FormattedText("Z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Comic Sans MS"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            emSize, fill, 1);
        var geometry = text.BuildGeometry(new Point());
        var bounds = geometry.Bounds;
        // Centre the visible glyph, retaining the preview's slight optical baseline offset.
        geometry.Transform = new TranslateTransform(-bounds.X - bounds.Width / 2,
            -bounds.Y - bounds.Height / 2 + emSize * 0.04);
        geometry.Freeze();

        var scale = new ScaleTransform(0.94, 0.94);
        var rotation = new RotateTransform(-0.07 * 180 / Math.PI);
        rotation.Freeze();
        var position = new TranslateTransform(x, y);
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(rotation);
        transform.Children.Add(position);
        var visual = new DrawingVisual { Transform = transform, Opacity = 0 };
        using (var drawing = visual.RenderOpen())
        {
            // Outline first, fill second matches the approved canvas preview.
            drawing.DrawGeometry(null, outline, geometry);
            drawing.DrawGeometry(fill, null, geometry);
        }
        _canvas.Children.Add(visual);
        return new Letter(visual, scale, position, x, y);
    }

    private static double Smooth(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private sealed record Letter(DrawingVisual Visual, ScaleTransform Scale,
        TranslateTransform Position, double X, double Y);
}
