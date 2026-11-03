using Xunit;

namespace Clicky.Tests;

public class AppPointingConfigurationTests
{
    [Fact]
    public void ProductionApp_EnablesRedesignedPointingByDefault()
    {
        Assert.True(global::Clicky.App.App.UseRedesignedPointingProtocolByDefault);
    }
}
