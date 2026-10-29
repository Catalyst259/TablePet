using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TablePet.App;

/// <summary>A cached vector bubble in the artwork's 512-unit coordinate system.</summary>
internal sealed class SpeechBubbleVisual : FrameworkElement
{
    private readonly ContainerVisual _canvas = new();
    private readonly ScaleTransform _canvasScale = new();
    private readonly DrawingVisual _bubble = new();
    private readonly ScaleTransform _pop = new(0.92, 0.92, 8, 94);
    private long _lastTime = -1;

    public SpeechBubbleVisual()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _canvas.Transform = _canvasScale;
        AddVisualChild(_canvas);
        var transform = new TransformGroup();
        transform.Children.Add(_pop);
        transform.Children.Add(new TranslateTransform(385, 110));
        _bubble.Transform = transform;
        _bubble.Opacity = 0;
        _canvas.Children.Add(_bubble);

        var fill = Brush(0xff, 0xff, 0xf8, 0xef);
        var outline = Brush(0xff, 0x6e, 0x52, 0x50);
        var ink = Brush(0xff, 0x4a, 0x35, 0x32);
        var shadow = Brush(0x18, 0, 0, 0);
        var pen = new Pen(outline, 3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        var tail = new StreamGeometry();
        using (var geometry = tail.Open())
        {
            geometry.BeginFigure(new Point(18, 68), true, true);
            geometry.LineTo(new Point(8, 94), true, false);
            geometry.LineTo(new Point(42, 68), true, false);
        }
        tail.Freeze();
        var shape = Geometry.Combine(new RectangleGeometry(new Rect(0, 0, 106, 76), 24, 24),
            tail, GeometryCombineMode.Union, null);
        shape.Freeze();
        var text = new FormattedText("喵", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            36, ink, 1);
        var glyph = text.BuildGeometry(new Point());
        var glyphBounds = glyph.Bounds;
        glyph.Transform = new TranslateTransform(53 - glyphBounds.X - glyphBounds.Width / 2,
            38 - glyphBounds.Y - glyphBounds.Height / 2);
        glyph.Freeze();
        using var drawing = _bubble.RenderOpen();
        drawing.PushTransform(new TranslateTransform(2, 4));
        drawing.DrawGeometry(shadow, null, shape);
        drawing.Pop();
        drawing.DrawGeometry(fill, pen, shape);
        drawing.DrawGeometry(ink, null, glyph);
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
        var time = Math.Max(0, elapsedMs);
        if (time == _lastTime) return;
        _lastTime = time;
        var appear = Smooth((time - 180) / 120d);
        var disappear = Smooth((time - 1100) / 300d);
        _bubble.Opacity = appear * (1 - disappear);
        _pop.ScaleX = _pop.ScaleY = 0.92 + 0.08 * appear;
    }

    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static double Smooth(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
