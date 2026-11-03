using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Clicky.Overlay;

/// <summary>
/// A fullscreen, transparent, click-through, always-on-top overlay window
/// for a single monitor. Mirrors <c>OverlayWindow</c> from the Mac reference
/// implementation (OverlayWindow.swift).
///
/// Hosts a <see cref="BlueCursorControl"/> for rendering the animated blue cursor.
/// </summary>
public class OverlayWindow : Window
{
    /// <summary>The HWND of this overlay, available after the window is shown.</summary>
    public IntPtr Hwnd { get; private set; }

    /// <summary>The monitor bounds this overlay covers (in physical pixels, for matching against PointDirective.DisplayBounds).</summary>
    public Rectangle MonitorBounds { get; }

    /// <summary>The DPI scale factors for this monitor (1.0 at 100%, 1.5 at 150%, etc.).</summary>
    public double DpiScaleX { get; }
    public double DpiScaleY { get; }

    /// <summary>The blue cursor control hosted on this overlay.</summary>
    public BlueCursorControl BlueCursor { get; }

    /// <summary>The exact-point debug crosshair hosted on this overlay.</summary>
    public DebugCrosshairControl DebugCrosshair { get; }

    /// <summary>Temporary gamer-mode annotation renderer.</summary>
    public DrawingOverlayControl DrawingOverlay { get; }

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
        BlueCursor = new BlueCursorControl();
        DebugCrosshair = new DebugCrosshairControl();
        DrawingOverlay = new DrawingOverlayControl();
        var canvas = new Canvas
        {
            IsHitTestVisible = false
        };
        canvas.Children.Add(DebugCrosshair.Visual);
        canvas.Children.Add(DrawingOverlay.Visual);
        canvas.Children.Add(BlueCursor.Visual);
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
        ReassertTopmost();
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

    /// <summary>Disposes the hosted BlueCursorControl when the window closes.</summary>
    protected override void OnClosed(EventArgs e)
    {
        BlueCursor.Dispose();
        DrawingOverlay.Dispose();
        base.OnClosed(e);
    }
}
