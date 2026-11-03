using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HeyDanis.Overlay;

/// <summary>
/// A fullscreen, transparent, click-through, always-on-top overlay window
/// for a single monitor. Mirrors <c>OverlayWindow</c> from the Mac reference
/// implementation (OverlayWindow.swift).
///
/// Hosts a <see cref="BuddyCursorControl"/> for rendering the animated blue cursor.
/// </summary>
public class OverlayWindow : Window
{
    /// <summary>The HWND of this overlay, available after the window is shown.</summary>
    public IntPtr Hwnd { get; private set; }

    /// <summary>The monitor bounds this overlay covers (in physical pixels, for matching against PointDirective.DisplayBounds).</summary>
    public Rectangle MonitorBounds { get; }

    /// <summary>The DPI scale factors for this monitor (1.0 at 100%, 1.5 at 150%, etc.).</summary>
    /// <remarks>
    /// Updated from the window's real DPI once it sits on its monitor — on mixed-DPI setups the
    /// value queried before creation can differ from what WPF actually renders with.
    /// </remarks>
    public double DpiScaleX { get; private set; }
    public double DpiScaleY { get; private set; }

    /// <summary>The blue cursor control hosted on this overlay.</summary>
    public BuddyCursorControl BlueCursor { get; }

    /// <summary>The exact-point debug crosshair hosted on this overlay.</summary>
    public DebugCrosshairControl DebugCrosshair { get; }

    /// <summary>Temporary gamer-mode annotation renderer.</summary>
    public DrawingOverlayControl DrawingOverlay { get; }

    /// <summary>Speech bubble showing the answer as text next to the mouse.</summary>
    public ResponseBubbleControl ResponseBubble { get; }

    public OverlayWindow(Rectangle monitorBounds, double dpiScaleX = 1.0, double dpiScaleY = 1.0)
    {
        MonitorBounds = monitorBounds;
        DpiScaleX = dpiScaleX;
        DpiScaleY = dpiScaleY;

        // Borderless, transparent, always-on-top, no taskbar entry, not hit-testable
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;

        // Position and size to cover the monitor.
        // WPF Left/Top/Width/Height are always in DIPs (device-independent pixels at 96 DPI).
        // Monitor bounds from GetMonitorInfo are physical pixels, so we must divide by the
        // monitor's DPI scale to get correct DIP values.
        var dipBounds = DpiHelper.ToDips(monitorBounds, dpiScaleX, dpiScaleY);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = dipBounds.X;
        Top = dipBounds.Y;
        Width = dipBounds.Width;
        Height = dipBounds.Height;

        // Host the blue cursor control on a Canvas that fills the overlay
        BlueCursor = new BuddyCursorControl();
        DebugCrosshair = new DebugCrosshairControl();
        DrawingOverlay = new DrawingOverlayControl();
        ResponseBubble = new ResponseBubbleControl();
        var canvas = new Canvas
        {
            IsHitTestVisible = false
        };
        canvas.Children.Add(DebugCrosshair.Visual);
        canvas.Children.Add(DrawingOverlay.Visual);
        canvas.Children.Add(BlueCursor.Visual);
        canvas.Children.Add(ResponseBubble.Visual);
        Content = canvas;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var helper = new WindowInteropHelper(this);
        Hwnd = helper.Handle;

        // Set extended window styles so the overlay:
        //  - WS_EX_TRANSPARENT: mouse clicks pass through to windows beneath
        //  - WS_EX_TOOLWINDOW: never appears in Alt+Tab
        //  - WS_EX_NOACTIVATE: never steals focus
        var exStyle = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_TOOLWINDOW
                 | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        PlaceOnMonitorInPhysicalPixels();
        ReassertTopmost();
    }

    /// <summary>
    /// With monitors at different scaling (e.g. 100 % + 150 %), WPF converts Left/Top/Width/Height
    /// with the DPI of the monitor the window is created on, so an overlay meant for the second
    /// monitor ends up shifted or squeezed — the pointer and circles then miss on that monitor.
    /// Placing the window with physical pixels via Win32 is DPI-independent; afterwards we read
    /// the DPI WPF really uses for this window.
    /// </summary>
    private void PlaceOnMonitorInPhysicalPixels()
    {
        NativeMethods.SetWindowPos(
            Hwnd,
            NativeMethods.HWND_TOPMOST,
            MonitorBounds.X,
            MonitorBounds.Y,
            MonitorBounds.Width,
            MonitorBounds.Height,
            NativeMethods.SWP_NOACTIVATE);

        var dpi = VisualTreeHelper.GetDpi(this);
        DpiScaleX = dpi.DpiScaleX;
        DpiScaleY = dpi.DpiScaleY;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        DpiScaleX = newDpi.DpiScaleX;
        DpiScaleY = newDpi.DpiScaleY;
        // WPF resizes the window to its "suggested" rectangle on a DPI change; put it back
        // exactly over the monitor.
        if (Hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                Hwnd,
                NativeMethods.HWND_TOPMOST,
                MonitorBounds.X,
                MonitorBounds.Y,
                MonitorBounds.Width,
                MonitorBounds.Height,
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Reasserts the Win32 topmost z-order without activating the overlay.
    /// Transparent no-activate WPF windows can drift behind other topmost-ish UI,
    /// so callers refresh this immediately before showing the cursor.
    /// </summary>
    public void ReassertTopmost()
    {
        if (Hwnd == IntPtr.Zero)
            return;

        Topmost = true;
        NativeMethods.SetWindowPos(
            Hwnd,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Disposes the hosted BuddyCursorControl when the window closes.</summary>
    protected override void OnClosed(EventArgs e)
    {
        BlueCursor.Dispose();
        DrawingOverlay.Dispose();
        base.OnClosed(e);
    }
}
