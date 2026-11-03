using Xunit;
using Clicky.App;

namespace Clicky.Tests;

public class AutoUpdateServiceTests
{
    [Fact]
    public void Initialize_DoesNotThrow_WhenDllMissing()
    {
        // WinSparkle.dll may not be present in the test environment.
        // Initialize should catch the DllNotFoundException and continue gracefully.
        using var service = new AutoUpdateService();
        var ex = Record.Exception(() => service.Initialize("https://example.com/appcast.xml"));
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenNotInitialized()
    {
        var service = new AutoUpdateService();
        var ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenCalledTwice()
    {
        var service = new AutoUpdateService();
        service.Initialize("https://example.com/appcast.xml");
        var ex = Record.Exception(() =>
        {
            service.Dispose();
            service.Dispose();
        });
        Assert.Null(ex);
    }
}
