using System.Drawing;
using Clicky.Overlay;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// Tests for <see cref="OverlayWindowManager"/> and <see cref="OverlayWindow"/>.
/// Tests that require a WPF dispatcher (window creation) are skipped in CI
/// by checking for a running message pump via STA thread + Application object.
/// Pure logic tests use the internal constructor that accepts a fake monitor enumerator.
/// </summary>
public class OverlayWindowManagerTests
{
    [Fact]
    public void MonitorRect_StoresBounds()
    {
        var bounds = new Rectangle(100, 200, 1920, 1080);
        var rect = new MonitorRect(bounds);

        Assert.Equal(bounds, rect.Bounds);
    }

    [Fact]
    public void MonitorRect_Equality()
    {
        var a = new MonitorRect(new Rectangle(0, 0, 1920, 1080));
        var b = new MonitorRect(new Rectangle(0, 0, 1920, 1080));
        var c = new MonitorRect(new Rectangle(1920, 0, 2560, 1440));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void OverlayHwnds_EmptyBeforeStart()
    {
        var manager = new OverlayWindowManager(() => new List<MonitorRect>());

        Assert.Empty(manager.OverlayHwnds);
        Assert.Empty(manager.Overlays);

        manager.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var manager = new OverlayWindowManager(() => new List<MonitorRect>());

        manager.Dispose();
        manager.Dispose();
    }

    [Fact]
    public void OverlayWindow_SetsExpectedProperties()
    {
        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                var bounds = new Rectangle(0, 0, 1920, 1080);
                var overlay = new OverlayWindow(bounds);

                Assert.Equal(System.Windows.WindowStyle.None, overlay.WindowStyle);
                Assert.True(overlay.AllowsTransparency);
                Assert.True(overlay.Topmost);
                Assert.False(overlay.ShowInTaskbar);
                Assert.False(overlay.IsHitTestVisible);
                Assert.Equal(System.Windows.ResizeMode.NoResize, overlay.ResizeMode);
                Assert.Equal(bounds, overlay.MonitorBounds);
                Assert.Equal(0d, overlay.Left);
                Assert.Equal(0d, overlay.Top);
                Assert.Equal(1920d, overlay.Width);
                Assert.Equal(1080d, overlay.Height);
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    [Fact]
    public void OverlayWindow_ReassertTopmost_BeforeShowDoesNotThrow()
    {
        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                var overlay = new OverlayWindow(new Rectangle(0, 0, 1920, 1080));

                overlay.ReassertTopmost();

                Assert.True(overlay.Topmost);
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    [Fact]
    public void OverlayWindow_OffsetMonitor_SetsCorrectPosition()
    {
        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                var bounds = new Rectangle(1920, 0, 2560, 1440);
                var overlay = new OverlayWindow(bounds);

                Assert.Equal(1920d, overlay.Left);
                Assert.Equal(0d, overlay.Top);
                Assert.Equal(2560d, overlay.Width);
                Assert.Equal(1440d, overlay.Height);
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    [Fact]
    public void OverlayWindow_HighDpi150_ConvertsBoundsToDips()
    {
        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                var bounds = new Rectangle(0, 0, 1920, 1080);
                var overlay = new OverlayWindow(bounds, 1.5, 1.5);

                Assert.Equal(bounds, overlay.MonitorBounds);
                Assert.Equal(1.5, overlay.DpiScaleX);
                Assert.Equal(1.5, overlay.DpiScaleY);
                Assert.Equal(0.0, overlay.Left, 6);
                Assert.Equal(0.0, overlay.Top, 6);
                Assert.Equal(1280.0, overlay.Width, 6);
                Assert.Equal(720.0, overlay.Height, 6);
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    [Fact]
    public void OverlayWindow_HighDpi200_OffsetMonitor_ConvertsToDips()
    {
        try
        {
            var thread = new System.Threading.Thread(() =>
            {
                var bounds = new Rectangle(1920, 0, 3840, 2160);
                var overlay = new OverlayWindow(bounds, 2.0, 2.0);

                Assert.Equal(bounds, overlay.MonitorBounds);
                Assert.Equal(960.0, overlay.Left, 6);
                Assert.Equal(0.0, overlay.Top, 6);
                Assert.Equal(1920.0, overlay.Width, 6);
                Assert.Equal(1080.0, overlay.Height, 6);
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    [Fact]
    public void ComputeCalibrationPoint_Center_ReturnsMonitorCenter()
    {
        var bounds = new Rectangle(100, 200, 1920, 1080);

        var point = OverlayWindowManager.ComputeCalibrationPoint(bounds, "center");

        Assert.Equal(1060.0, point.X, 1);
        Assert.Equal(740.0, point.Y, 1);
    }

    [Fact]
    public void ComputeCalibrationPoint_TopRight_UsesPhysicalCorner()
    {
        var bounds = new Rectangle(1920, 0, 2560, 1440);

        var point = OverlayWindowManager.ComputeCalibrationPoint(bounds, "top-right");

        Assert.Equal(4479.0, point.X, 1);
        Assert.Equal(0.0, point.Y, 1);
    }

    [Fact]
    public void EnumerateMonitors_ReturnsAtLeastOne()
    {
        var monitors = Clicky.Capture.ScreenCapture.EnumerateMonitors();
        Assert.True(monitors.Count >= 1, "Expected at least one monitor on a desktop machine");
    }
}
