using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clicky.App;

/// <summary>
/// Wraps WinSparkle (https://winsparkle.org) for automatic update checking,
/// mirroring the Sparkle integration in the Mac reference (leanring_buddyApp.swift).
///
/// WinSparkle checks an appcast.xml feed for new versions and shows a native
/// update dialog when one is available. The check runs in a background thread
/// and never blocks the UI.
/// </summary>
internal sealed class AutoUpdateService : IDisposable
{
    private bool _initialized;

    /// <summary>
    /// Initializes WinSparkle with the given appcast URL and app metadata,
    /// then starts a background update check.
    ///
    /// Failures are logged to Debug output but never block startup.
    /// </summary>
    /// <param name="appcastUrl">
    /// URL of the Sparkle appcast feed. Placeholder is fine for initial setup —
    /// the maintainer must replace this with a real Windows appcast URL.
    /// </param>
    public void Initialize(string appcastUrl)
    {
        try
        {
            // Configure app metadata before calling init.
            var appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.1";
            WinSparkleNative.win_sparkle_set_app_details(
                "Clicky",           // company name
                "Clicky",           // app name
                appVersion);        // app version

            // Point at the appcast feed.
            // NOTE: Maintainer must replace this placeholder URL with a real
            // Windows-specific appcast.xml feed URL before shipping.
            WinSparkleNative.win_sparkle_set_appcast_url(appcastUrl);

            // Disable automatic periodic checks — we do one explicit check
            // per launch to match the Mac behavior (Sparkle's startUpdater
            // is currently commented out in leanring_buddyApp.swift).
            WinSparkleNative.win_sparkle_set_automatic_check_for_updates(0);

            // Initialize WinSparkle (non-blocking).
            WinSparkleNative.win_sparkle_init();
            _initialized = true;

            // Run one background check. This is non-blocking and shows
            // the update UI only if an update is actually found.
            WinSparkleNative.win_sparkle_check_update_without_ui();
        }
        catch (Exception ex)
        {
            // WinSparkle.dll may not be present (e.g. dev builds without
            // the native binary). Log and continue — updates are optional.
            Debug.WriteLine($"[AutoUpdateService] WinSparkle initialization failed: {ex.Message}");
            _initialized = false;
        }
    }

    public void Dispose()
    {
        if (!_initialized) return;
        try
        {
            WinSparkleNative.win_sparkle_cleanup();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoUpdateService] WinSparkle cleanup failed: {ex.Message}");
        }
        _initialized = false;
    }

    /// <summary>
    /// P/Invoke bindings for WinSparkle native DLL.
    /// See https://winsparkle.org and winsparkle.h for documentation.
    /// </summary>
    private static class WinSparkleNative
    {
        private const string DllName = "WinSparkle";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void win_sparkle_init();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void win_sparkle_cleanup();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern void win_sparkle_set_app_details(
            [MarshalAs(UnmanagedType.LPWStr)] string companyName,
            [MarshalAs(UnmanagedType.LPWStr)] string appName,
            [MarshalAs(UnmanagedType.LPWStr)] string appVersion);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern void win_sparkle_set_appcast_url(
            [MarshalAs(UnmanagedType.LPStr)] string url);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void win_sparkle_set_automatic_check_for_updates(int state);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void win_sparkle_check_update_without_ui();
    }
}
