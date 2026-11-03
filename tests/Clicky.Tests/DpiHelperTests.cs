using System.Drawing;
using Clicky.Overlay;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// Tests for <see cref="DpiHelper"/> — physical pixel ↔ DIP conversions.
/// These are pure math tests and don't need a WPF dispatcher or real display.
/// </summary>
public class DpiHelperTests
{
    // ── Round-trip at 100% (identity) ───────────────────────────────

    [Fact]
    public void ToDips_At100Percent_IsIdentity()
    {
        var physical = new System.Drawing.Point(1920, 1080);
        var dips = DpiHelper.ToDips(physical, 1.0, 1.0);

        Assert.Equal(1920.0, dips.X, 6);
        Assert.Equal(1080.0, dips.Y, 6);
    }

    [Fact]
    public void RoundTrip_At100Percent_IsIdentity()
    {
        var original = new System.Windows.Point(1920, 1080);
        var dips = DpiHelper.ToDips(original, 1.0, 1.0);
        var back = DpiHelper.ToPhysical(dips, 1.0, 1.0);

        Assert.Equal(1920, back.X);
        Assert.Equal(1080, back.Y);
    }

    // ── 125% scaling ────────────────────────────────────────────────

    [Fact]
    public void ToDips_At125Percent_ScalesCorrectly()
    {
        // 1920 physical px / 1.25 = 1536 DIPs
        var physical = new System.Drawing.Point(1920, 1080);
        var dips = DpiHelper.ToDips(physical, 1.25, 1.25);

        Assert.Equal(1536.0, dips.X, 6);
        Assert.Equal(864.0, dips.Y, 6);
    }

    [Fact]
    public void RoundTrip_At125Percent_IsIdentity()
    {
        var original = new System.Windows.Point(1536, 864);
        var back = DpiHelper.ToPhysical(original, 1.25, 1.25);
        var dips = DpiHelper.ToDips(back, 1.25, 1.25);

        Assert.Equal(1536.0, dips.X, 0);
        Assert.Equal(864.0, dips.Y, 0);
    }

    // ── 150% scaling ────────────────────────────────────────────────

    [Fact]
    public void ToDips_At150Percent_ScalesCorrectly()
    {
        // 1920 physical px / 1.5 = 1280 DIPs
        var physical = new System.Drawing.Point(1920, 1080);
        var dips = DpiHelper.ToDips(physical, 1.5, 1.5);

        Assert.Equal(1280.0, dips.X, 6);
        Assert.Equal(720.0, dips.Y, 6);
    }

    [Fact]
    public void RoundTrip_At150Percent_IsIdentity()
    {
        var original = new System.Windows.Point(1280, 720);
        var back = DpiHelper.ToPhysical(original, 1.5, 1.5);
        var dips = DpiHelper.ToDips(back, 1.5, 1.5);

        Assert.Equal(1280.0, dips.X, 0);
        Assert.Equal(720.0, dips.Y, 0);
    }

    // ── 200% scaling ────────────────────────────────────────────────

    [Fact]
    public void ToDips_At200Percent_ScalesCorrectly()
    {
        // 3840 physical px / 2.0 = 1920 DIPs
        var physical = new System.Drawing.Point(3840, 2160);
        var dips = DpiHelper.ToDips(physical, 2.0, 2.0);

        Assert.Equal(1920.0, dips.X, 6);
        Assert.Equal(1080.0, dips.Y, 6);
    }

    [Fact]
    public void RoundTrip_At200Percent_IsIdentity()
    {
        var original = new System.Windows.Point(1920, 1080);
        var back = DpiHelper.ToPhysical(original, 2.0, 2.0);
        var dips = DpiHelper.ToDips(back, 2.0, 2.0);

        Assert.Equal(1920.0, dips.X, 0);
        Assert.Equal(1080.0, dips.Y, 0);
    }

    // ── Rectangle conversion ────────────────────────────────────────

    [Fact]
    public void ToDips_Rectangle_At150Percent_ConvertsAllFields()
    {
        var physicalBounds = new Rectangle(0, 0, 1920, 1080);
        var dipBounds = DpiHelper.ToDips(physicalBounds, 1.5, 1.5);

        Assert.Equal(0.0, dipBounds.X, 6);
        Assert.Equal(0.0, dipBounds.Y, 6);
        Assert.Equal(1280.0, dipBounds.Width, 6);
        Assert.Equal(720.0, dipBounds.Height, 6);
    }

    [Fact]
    public void ToDips_Rectangle_WithOffset_At150Percent_ConvertsOriginToo()
    {
        // Secondary monitor at physical offset (1920, 0)
        var physicalBounds = new Rectangle(1920, 0, 2560, 1440);
        var dipBounds = DpiHelper.ToDips(physicalBounds, 1.5, 1.5);

        Assert.Equal(1280.0, dipBounds.X, 6);
        Assert.Equal(0.0, dipBounds.Y, 6);
        Assert.Equal(2560.0 / 1.5, dipBounds.Width, 6);
        Assert.Equal(960.0, dipBounds.Height, 6);
    }

    // ── WPF Point overload ──────────────────────────────────────────

    [Fact]
    public void ToDips_WpfPoint_At150Percent_ScalesCorrectly()
    {
        var physical = new System.Windows.Point(1500, 900);
        var dips = DpiHelper.ToDips(physical, 1.5, 1.5);

        Assert.Equal(1000.0, dips.X, 6);
        Assert.Equal(600.0, dips.Y, 6);
    }

    // ── Overlay-local DIP coordinate computation (simulates BlueCursorControl.FlyTo) ──

    [Fact]
    public void OverlayLocalDips_At150Percent_TargetMapsCorrectly()
    {
        // Monitor at physical (0,0), 1920x1080, 150% DPI
        var monitorBounds = new Rectangle(0, 0, 1920, 1080);
        double dpiScale = 1.5;

        // Target at physical (960, 540) — center of the physical screen
        var targetPhysical = new System.Windows.Point(960, 540);

        // Expected: overlay-local DIPs = (960 - 0) / 1.5, (540 - 0) / 1.5 = (640, 360)
        // which is the center of the DIP overlay (1280x720)
        var localDipX = (targetPhysical.X - monitorBounds.X) / dpiScale;
        var localDipY = (targetPhysical.Y - monitorBounds.Y) / dpiScale;

        Assert.Equal(640.0, localDipX, 6);
        Assert.Equal(360.0, localDipY, 6);

        // Verify this IS the center of the DIP overlay
        var dipBounds = DpiHelper.ToDips(monitorBounds, dpiScale, dpiScale);
        Assert.Equal(localDipX, dipBounds.Width / 2.0, 6);
        Assert.Equal(localDipY, dipBounds.Height / 2.0, 6);
    }

    [Fact]
    public void OverlayLocalDips_At150Percent_OffsetMonitor_MapsCorrectly()
    {
        // Secondary monitor at physical (1920, 0), 2560x1440, 150% DPI
        var monitorBounds = new Rectangle(1920, 0, 2560, 1440);
        double dpiScale = 1.5;

        // Target at physical (3200, 720) — center of the secondary monitor
        // = physical (1920, 0) + (1280, 720)
        var targetPhysical = new System.Windows.Point(3200, 720);

        // Local physical = (3200-1920, 720-0) = (1280, 720)
        // Local DIPs = (1280/1.5, 720/1.5) = (853.33, 480)
        var localDipX = (targetPhysical.X - monitorBounds.X) / dpiScale;
        var localDipY = (targetPhysical.Y - monitorBounds.Y) / dpiScale;

        Assert.Equal(1280.0 / 1.5, localDipX, 2);
        Assert.Equal(480.0, localDipY, 6);

        // And that is the center of the DIP overlay
        var dipBounds = DpiHelper.ToDips(monitorBounds, dpiScale, dpiScale);
        Assert.Equal(localDipX, dipBounds.Width / 2.0, 2);
        Assert.Equal(localDipY, dipBounds.Height / 2.0, 6);
    }

    // ── MonitorRect carries DPI ─────────────────────────────────────

    [Fact]
    public void MonitorRect_DefaultDpiScale_IsOne()
    {
        var rect = new MonitorRect(new Rectangle(0, 0, 1920, 1080));
        Assert.Equal(1.0, rect.DpiScaleX);
        Assert.Equal(1.0, rect.DpiScaleY);
    }

    [Fact]
    public void MonitorRect_WithDpiScale_StoresValues()
    {
        var rect = new MonitorRect(new Rectangle(0, 0, 1920, 1080), 1.5, 1.5);
        Assert.Equal(1.5, rect.DpiScaleX);
        Assert.Equal(1.5, rect.DpiScaleY);
    }
}
