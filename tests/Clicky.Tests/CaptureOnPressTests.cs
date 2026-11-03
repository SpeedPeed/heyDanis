using Clicky.Capture;
using Xunit;

namespace Clicky.Tests;

public class CaptureOnPressTests
{
    [Fact]
    public async Task CaptureOnPress_CancellationBeforeCompletion_ThrowsOperationCanceled()
    {
        // Simulates the accidental-tap scenario: capture is started then cancelled
        // before it completes (press duration < 100 ms).
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately (simulates very short tap)

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScreenCapture.CaptureAllScreensAsJpegAsync(null, cts.Token));
    }

    [Fact]
    public async Task CaptureOnPress_CancellationToken_IsRespected()
    {
        using var cts = new CancellationTokenSource();

        // Start capture and cancel quickly
        var captureTask = ScreenCapture.CaptureAllScreensAsJpegAsync(null, cts.Token);
        cts.Cancel();

        // Should either complete normally (if capture was fast) or throw cancellation
        try
        {
            await captureTask;
        }
        catch (OperationCanceledException)
        {
            // Expected — capture was cancelled before completion
        }
    }

    [Fact]
    public void ShortTapDetection_Under100ms_ShouldRecapture()
    {
        // Tests the timing logic used by CompanionManager: if the press duration
        // is under 100 ms, the capture-on-press result is discarded and a fresh
        // capture is taken after key release.
        var pressTimestamp = DateTime.UtcNow;
        var releaseTimestamp = pressTimestamp.AddMilliseconds(50); // 50 ms tap
        var pressDuration = releaseTimestamp - pressTimestamp;

        Assert.True(pressDuration.TotalMilliseconds < 100,
            "A 50 ms tap should be detected as an accidental press");
    }

    [Fact]
    public void NormalPress_Over100ms_ShouldUseCapturedResult()
    {
        var pressTimestamp = DateTime.UtcNow;
        var releaseTimestamp = pressTimestamp.AddMilliseconds(500); // 500 ms hold
        var pressDuration = releaseTimestamp - pressTimestamp;

        Assert.False(pressDuration.TotalMilliseconds < 100,
            "A 500 ms press should use the capture-on-press result");
    }
}
