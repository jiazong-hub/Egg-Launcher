using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace Launcher.App;

public sealed class CircularProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(
            CreateFrozenBrush(Color.FromRgb(24, 72, 101)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush),
        typeof(Brush),
        typeof(CircularProgressRing),
        new FrameworkPropertyMetadata(
            CreateFrozenBrush(Color.FromRgb(24, 213, 248)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var thickness = Math.Max(1d, StrokeThickness);
        var diameter = Math.Max(0d, Math.Min(ActualWidth, ActualHeight) - thickness);
        if (diameter <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2d, ActualHeight / 2d);
        var radius = diameter / 2d;
        var trackPen = CreatePen(TrackBrush, thickness);
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        if (!double.IsFinite(Value))
        {
            return;
        }

        var percentage = Math.Clamp(Value, 0d, 100d);
        if (percentage <= 0d)
        {
            return;
        }

        var progressPen = CreatePen(ProgressBrush, thickness);
        if (percentage >= 100d)
        {
            drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        const double startAngle = -90d;
        var endAngle = startAngle + percentage * 3.6d;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, endAngle);
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            0d,
            percentage > 50d,
            SweepDirection.Clockwise,
            true));

        drawingContext.DrawGeometry(null, progressPen, new PathGeometry([figure]));
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
