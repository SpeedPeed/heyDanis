using Xunit;
using Clicky.Companion;

namespace Clicky.Tests;

public class ClickyAnalyticsTests
{
    [Fact]
    public void Configure_DoesNotThrow()
    {
        // Configure should never throw, even without network access.
        var ex = Record.Exception(() => ClickyAnalytics.Configure());
        Assert.Null(ex);
    }

    [Fact]
    public void TrackAppOpened_DoesNotThrow_BeforeConfigure()
    {
        // TrackAppOpened should be safe even if Configure hasn't been called
        // (the internal client will be null and the call is a no-op).
        var ex = Record.Exception(() => ClickyAnalytics.TrackAppOpened());
        Assert.Null(ex);
    }

    [Fact]
    public void Shutdown_DoesNotThrow()
    {
        var ex = Record.Exception(() => ClickyAnalytics.Shutdown());
        Assert.Null(ex);
    }

    [Fact]
    public void IsOptedOut_DefaultsFalse()
    {
        // Without the registry key set, opt-out should default to false.
        // This test assumes the test environment doesn't have
        // HKCU\Software\Clicky\analyticsOptOut set.
        Assert.False(ClickyAnalytics.IsOptedOut());
    }

    [Fact]
    public void Configure_TrackAppOpened_Shutdown_FullLifecycle()
    {
        // Full lifecycle should not throw.
        var ex = Record.Exception(() =>
        {
            ClickyAnalytics.Configure();
            ClickyAnalytics.TrackAppOpened();
            ClickyAnalytics.Shutdown();
        });
        Assert.Null(ex);
    }
}
