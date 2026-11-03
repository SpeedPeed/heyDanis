using System.Drawing;

namespace Clicky.Overlay;

/// <summary>
/// Queries per-monitor DPI and converts between physical pixels and WPF DIPs.
/// WPF's coordinate system uses device-independent pixels (96 DPI baseline).
/// On a 150% display, 1 DIP = 1.5 physical pixels, so a 1920px monitor is 1280 DIPs wide.
/// </summary>
public static class DpiHelper
{
    /// <summary>Baseline DPI for WPF DIPs.</summary>
    private const double BaseDpi = 96.0;

    /// <summary>
    /// Gets the DPI scale factors (scaleX, scaleY) for the given monitor handle.
    /// At 100% scaling both values are 1.0; at 150% they are 1.5, etc.
    /// Falls back to 1.0 if the query fails (e.g., on older Windows versions).
    /// </summary>
    public static (double ScaleX, double ScaleY) GetDpiScale(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
            return (1.0, 1.0);

        int hr = NativeMethods.GetDpiForMonitor(
            hMonitor,
            NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
            out uint dpiX,
            out uint dpiY);

        if (hr != 0) // S_OK
            return (1.0, 1.0);

        return (dpiX / BaseDpi, dpiY / BaseDpi);
    }

    /// <summary>
    /// Converts a physical-pixel point to WPF DIPs using the given scale factors.
    /// </summary>
    public static System.Windows.Point ToDips(System.Drawing.Point physical, double scaleX, double scaleY)
    {
        return new System.Windows.Point(physical.X / scaleX, physical.Y / scaleY);
    }

    /// <summary>
    /// Converts a physical-pixel point (as <see cref="System.Windows.Point"/>) to DIPs.
    /// </summary>
    public static System.Windows.Point ToDips(System.Windows.Point physical, double scaleX, double scaleY)
    {
        return new System.Windows.Point(physical.X / scaleX, physical.Y / scaleY);
    }

    /// <summary>
    /// Converts a physical-pixel rectangle to a DIP rectangle.
    /// </summary>
    public static System.Windows.Rect ToDips(Rectangle physicalBounds, double scaleX, double scaleY)
    {
        return new System.Windows.Rect(
            physicalBounds.X / scaleX,
            physicalBounds.Y / scaleY,
            physicalBounds.Width / scaleX,
            physicalBounds.Height / scaleY);
    }

    /// <summary>
    /// Converts a DIP point back to physical pixels.
    /// </summary>
    public static System.Drawing.Point ToPhysical(System.Windows.Point dips, double scaleX, double scaleY)
    {
        return new System.Drawing.Point(
            (int)Math.Round(dips.X * scaleX),
            (int)Math.Round(dips.Y * scaleY));
    }
}
