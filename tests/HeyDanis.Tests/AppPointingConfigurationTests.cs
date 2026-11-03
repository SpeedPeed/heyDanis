using Xunit;

namespace HeyDanis.Tests;

public class AppPointingConfigurationTests
{
    [Fact]
    public void ProductionApp_EnablesRedesignedPointingByDefault()
    {
        Assert.True(global::HeyDanis.App.App.UseRedesignedPointingProtocolByDefault);
    }
}
