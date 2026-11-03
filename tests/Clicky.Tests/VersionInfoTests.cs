using Clicky.App;
using Xunit;

namespace Clicky.Tests;

public class VersionInfoTests
{
    [Fact]
    public void Current_ReturnsNonEmptyString()
    {
        var version = VersionInfo.Current;

        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void Current_DoesNotContainCommitHashSuffix()
    {
        var version = VersionInfo.Current;

        Assert.DoesNotContain("+", version);
    }
}
