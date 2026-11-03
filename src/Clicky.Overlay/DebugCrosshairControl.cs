using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Clicky.Overlay;

/// <summary>
/// Exact-point marker used during pointer debugging.
/// </summary>
public sealed class DebugCrosshairControl
{
    private const double CrosshairRadius = 8.0;
    private const double DotRadius = 3.0;

    private readonly Canvas _root;
    private readonly Canvas _crosshair;

    public DebugCrosshairControl()
    {
        _crosshair = new Canvas
        {
            Width = CrosshairRadius * 2,
            Height = CrosshairRadius * 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));

        _crosshair.Children.Add(new Line
        {
            X1 = CrosshairRadius,
            Y1 = 0,
            X2 = CrosshairRadius,
            Y2 = CrosshairRadius * 2,
            Stroke = stroke,
            StrokeThickness = 1.5,
            SnapsToDevicePixels = true,
        });

        _crosshair.Children.Add(new Line
        {
            X1 = 0,
            Y1 = CrosshairRadius,
            X2 = CrosshairRadius * 2,
            Y2 = CrosshairRadius,
            Stroke = stroke,
            StrokeThickness = 1.5,
            SnapsToDevicePixels = true,
        });

        var dot = new Ellipse
        {
            Width = DotRadius * 2,
            Height = DotRadius * 2,
            Fill = stroke,
        };
        _crosshair.Children.Add(dot);
        Canvas.SetLeft(dot, CrosshairRadius - DotRadius);
        Canvas.SetTop(dot, CrosshairRadius - DotRadius);

        _root = new Canvas
        {
            Visibility = Visibility.Visible,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_crosshair);
    }

    public UIElement Visual => _root;

    public void ShowAt(Point targetPoint, System.Drawing.Rectangle overlayBounds, double dpiScaleX, double dpiScaleY)
    {
        var localX = (targetPoint.X - overlayBounds.X) / dpiScaleX;
        var localY = (targetPoint.Y - overlayBounds.Y) / dpiScaleY;

        Canvas.SetLeft(_crosshair, localX - CrosshairRadius);
        Canvas.SetTop(_crosshair, localY - CrosshairRadius);
        _crosshair.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        _crosshair.Visibility = Visibility.Collapsed;
    }
}
