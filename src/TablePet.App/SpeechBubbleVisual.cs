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
    private readonly TranslateTransform _position = new(385, 110);
    private readonly ScaleTransform _fit = new();
    private long _lastTime = -1;
    private int _durationMs = 1400;
    private double _fontSize = 36;
    private string _text = "喵";

    internal string Text => _text;

    public SpeechBubbleVisual()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _canvas.Transform = _canvasScale;
        AddVisualChild(_canvas);
        var transform = new TransformGroup();
        transform.Children.Add(_pop);
        transform.Children.Add(_fit);
        transform.Children.Add(_position);
        _bubble.Transform = transform;
        _bubble.Opacity = 0;
        _canvas.Children.Add(_bubble);

        DrawBubble();
    }

    private void DrawBubble()
    {

        var fill = Brush(0xff, 0xff, 0xf8, 0xef);
        var outline = Brush(0xff, 0x6e, 0x52, 0x50);
        var ink = Brush(0xff, 0x4a, 0x35, 0x32);
        var shadow = Brush(0x18, 0, 0, 0);
        var pen = new Pen(outline, 3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        var text = new FormattedText(_text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            _fontSize, ink, 1);
        var legacy = _text == "喵";
        if (!legacy)
        {
            // The full bubble stays inside the pet's existing overlay, including at screen edges.
            // Short Chinese work messages fit on one line; other text wraps without an ellipsis.
            text.MaxTextWidth = 444;
            text.TextAlignment = TextAlignment.Center;
        }
        var width = legacy ? 106 : Math.Min(480, Math.Max(106, Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 36));
        var height = legacy ? 76 : Math.Max(76, Math.Ceiling(text.Height) + 28);
        var fit = Math.Min(1, 390 / (height + 24));
        _fit.ScaleX = _fit.ScaleY = fit;
        _position.X = 491 - width * fit;
        _position.Y = 110;
        var tailTip = Math.Max(8, width - 98);
        _pop.CenterX = tailTip;
        _pop.CenterY = height + 18;

        var tail = new StreamGeometry();
        using (var geometry = tail.Open())
        {
            geometry.BeginFigure(new Point(tailTip + 10, height - 8), true, true);
            geometry.LineTo(new Point(tailTip, height + 18), true, false);
            geometry.LineTo(new Point(tailTip + 34, height - 8), true, false);
        }
        tail.Freeze();
        var shape = Geometry.Combine(new RectangleGeometry(new Rect(0, 0, width, height), 24, 24),
            tail, GeometryCombineMode.Union, null);
        shape.Freeze();
        var glyph = text.BuildGeometry(new Point());
        var glyphBounds = glyph.Bounds;
        glyph.Transform = new TranslateTransform(width / 2 - glyphBounds.X - glyphBounds.Width / 2,
            height / 2 - glyphBounds.Y - glyphBounds.Height / 2);
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
        UpdateFontSize();
    }

    private void UpdateFontSize()
    {
        // Keep short work messages readable even at the smallest supported pet size.
        var fontSize = _text == "喵" || ActualWidth <= 0 ? 36 : Math.Max(36, 12 * 512 / ActualWidth);
        if (Math.Abs(fontSize - _fontSize) < 0.01) return;
        _fontSize = fontSize;
        DrawBubble();
    }

    internal static int AppearanceDelay(int durationMs) => Math.Min(180, Math.Max(0, durationMs / 5));

    public void Update(long elapsedMs) => Update(elapsedMs, "喵", 1400);

    public void Update(long elapsedMs, string text, int durationMs)
    {
        var changed = _text != text || _durationMs != durationMs;
        if (_text != text)
        {
            _text = text;
            UpdateFontSize();
            DrawBubble();
        }
        _durationMs = durationMs;
        var time = Math.Max(0, elapsedMs);
        if (time == _lastTime && !changed) return;
        _lastTime = time;
        var appearMs = Math.Max(1, Math.Min(120, durationMs / 5));
        var fadeMs = Math.Max(1, Math.Min(300, durationMs / 4));
        var appear = Smooth((time - AppearanceDelay(durationMs)) / (double)appearMs);
        var disappear = Smooth((time - (durationMs - fadeMs)) / (double)fadeMs);
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
