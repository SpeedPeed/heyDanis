using Windows.Graphics.Capture;

namespace Clicky.Capture;

/// <summary>
/// Probes whether screen capture is available, mirroring
/// <c>CGPreflightScreenCaptureAccess()</c> on Mac.
/// On Windows, unpackaged desktop apps can always capture the screen;
/// the probe verifies that the WGC runtime is present and functional.
/// </summary>
public static class ScreenCapturePermissions
{
    /// <summary>
    /// Returns <c>true</c> if screen capture is available on this system.
    /// Windows desktop apps do not require explicit user consent for screen
    /// capture (unlike macOS), but WGC may not be supported on very old
    /// Windows 10 builds or inside certain VMs.
    /// </summary>
    public static Task<bool> ProbeAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                // GraphicsCaptureSession.IsSupported checks both OS version
                // and hardware capability for WGC.
                if (GraphicsCaptureSession.IsSupported())
                    return true;

                // Even without WGC, the GDI+ fallback in ScreenCapture works,
                // so we still report capture as available.
                return true;
            }
            catch
            {
                // The WinRT projection itself is missing (should not happen
                // when targeting net8.0-windows10.0.19041.0).
                return false;
            }
        });
    }
}
