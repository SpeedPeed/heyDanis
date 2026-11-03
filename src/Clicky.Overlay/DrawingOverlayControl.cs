using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clicky.Overlay;

/// <summary>
/// Renders temporary gamer-mode annotations on the transparent overlay.
/// </summary>
public sealed class DrawingOverlayControl : IDisposable
{
    private const double StrokeWidth = 4.0;
    private const double LingerSeconds = 4.0;

    private readonly Canvas _canvas;
    private DispatcherTimer? _clearTimer;
    private bool _disposed;

    public DrawingOverlayControl()
    {
        _canvas = new Canvas
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
    }

    public UIElement Visual => _canvas;

    public void DrawAnnotations(
        IReadOnlyList<OverlayAnnotationTarget> annotations,
        System.Drawing.Rectangle overlayBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        Clear();

        foreach (var annotation in annotations)
        {
            if (annotation.Kind == OverlayAnnotationKind.Circle)
            {
                DrawCircle(annotation, overlayBounds, dpiScaleX, dpiScaleY);
            }
            else
            {
                DrawArrow(annotation, overlayBounds, dpiScaleX, dpiScaleY);
            }
        }

        if (_canvas.Children.Count == 0)
            return;

        _canvas.Visibility = Visibility.Visible;
        _canvas.Opacity = 1.0;

        _clearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(LingerSeconds),
        };
        _clearTimer.Tick += OnClearTimerTick;
        _clearTimer.Start();
    }

    public void Clear()
    {
        StopTimer();
        _canvas.Children.Clear();
        _canvas.Visibility = Visibility.Collapsed;
        _canvas.Opacity = 1.0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimer();
    }

    private void DrawCircle(
        OverlayAnnotationTarget annotation,
        System.Drawing.Rectangle overlayBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        var center = ToLocal(annotation.Point, overlayBounds, dpiScaleX, dpiScaleY);
        var radius = Math.Max(12.0, annotation.Radius / ((dpiScaleX + dpiScaleY) / 2.0));
        var ellipse = new Ellipse
        {
            Width = radius * 2.0,
            Height = radius * 2.0,
            Stroke = AnnotationBrush(),
            StrokeThickness = StrokeWidth,
            Fill = Brushes.Transparent,
            Effect = Glow(),
        };

        Canvas.SetLeft(ellipse, center.X - radius);
        Canvas.SetTop(ellipse, center.Y - radius);
        _canvas.Children.Add(ellipse);
    }

    private void DrawArrow(
        OverlayAnnotationTarget annotation,
        System.Drawing.Rectangle overlayBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        var start = ToLocal(annotation.StartPoint, overlayBounds, dpiScaleX, dpiScaleY);
        var end = ToLocal(annotation.EndPoint, overlayBounds, dpiScaleX, dpiScaleY);

        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = AnnotationBrush(),
            StrokeThickness = StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = Glow(),
        };
        _canvas.Children.Add(line);

        var head = BuildArrowHead(start, end);
        head.Fill = AnnotationBrush();
        head.Effect = Glow();
        _canvas.Children.Add(head);
    }

    private static Polygon BuildArrowHead(Point start, Point end)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        const double length = 18.0;
        const double spread = Math.PI / 7.0;

        var p1 = new Point(
            end.X - length * Math.Cos(angle - spread),
            end.Y - length * Math.Sin(angle - spread));
        var p2 = new Point(
            end.X - length * Math.Cos(angle + spread),
            end.Y - length * Math.Sin(angle + spread));

        return new Polygon
        {
            Points = new PointCollection { end, p1, p2 },
        };
    }

    private static Point ToLocal(
        System.Windows.Point screenPoint,
        System.Drawing.Rectangle overlayBounds,
        double dpiScaleX,
        double dpiScaleY) =>
        new(
            (screenPoint.X - overlayBounds.X) / dpiScaleX,
            (screenPoint.Y - overlayBounds.Y) / dpiScaleY);

    private static SolidColorBrush AnnotationBrush() =>
        new(Color.FromArgb(245, 52, 199, 89));

    private static System.Windows.Media.Effects.DropShadowEffect Glow() =>
        new()
        {
            Color = Color.FromRgb(52, 199, 89),
            BlurRadius = 14,
            ShadowDepth = 0,
            Opacity = 0.95,
        };

    private void OnClearTimerTick(object? sender, EventArgs e)
    {
        Clear();
    }

    private void StopTimer()
    {
        if (_clearTimer is null)
            return;

        _clearTimer.Stop();
        _clearTimer.Tick -= OnClearTimerTick;
        _clearTimer = null;
    }
}

public enum OverlayAnnotationKind
{
    Circle,
    Arrow,
}

public sealed record OverlayAnnotationTarget
{
    public required OverlayAnnotationKind Kind { get; init; }
    public required System.Drawing.Rectangle DisplayBounds { get; init; }
    public required string Label { get; init; }
    public System.Windows.Point Point { get; init; }
    public double Radius { get; init; }
    public System.Windows.Point StartPoint { get; init; }
    public System.Windows.Point EndPoint { get; init; }
}
