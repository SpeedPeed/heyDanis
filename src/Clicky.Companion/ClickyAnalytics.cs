using System;
using Microsoft.Win32;
using PostHog;

namespace Clicky.Companion;

/// <summary>
/// Wraps the PostHog .NET SDK with Configure() and TrackAppOpened() entry points,
/// mirroring ClickyAnalytics.swift in the Mac reference implementation.
/// Respects a HKCU\Software\Clicky\analyticsOptOut registry flag.
/// </summary>
public static class ClickyAnalytics
{
    // Same API key and host as the Mac app (ClickyAnalytics.swift).
    private const string PostHogApiKey = "phc_xcQPygmhTMzzYh8wNW92CCwoXmnzqyChAixh8zgpqC3C";
    private const string PostHogHost = "https://us.i.posthog.com";

    private const string RegistrySubKey = @"Software\Clicky";
    private const string OptOutValueName = "analyticsOptOut";

    private static PostHogClient? _client;

    /// <summary>
    /// Returns true if the user has opted out of analytics via the
    /// HKCU\Software\Clicky\analyticsOptOut registry flag.
    /// </summary>
    public static bool IsOptedOut()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: false);
            if (key is null) return false;
            var value = key.GetValue(OptOutValueName);
            return value is int intVal && intVal != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Configures the PostHog SDK. Must be called once at app startup
    /// before any Track* calls. No-op if the user has opted out.
    /// </summary>
    public static void Configure()
    {
        if (IsOptedOut()) return;

        try
        {
            _client = new PostHogClient(new PostHogOptions
            {
                ProjectApiKey = PostHogApiKey,
                HostUrl = new Uri(PostHogHost),
            });
        }
        catch
        {
            // Never block startup on analytics failure.
            _client = null;
        }
    }

    /// <summary>
    /// Tracks the 'app_opened' event, mirroring ClickyAnalytics.trackAppOpened()
    /// in the Mac reference. Includes the app version as a property.
    /// </summary>
    public static void TrackAppOpened()
    {
        try
        {
            _client?.Capture(
                GetDistinctId(),
                "app_opened",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["app_version"] = ReadAppVersion(),
                    ["platform"] = "windows",
                });
        }
        catch
        {
            // Never block on analytics failure.
        }
    }

    /// <summary>
    /// Tracks the 'onboarding_completed' event when the user finishes the first-run
    /// settings flow and saves valid API keys for the first time.
    /// </summary>
    public static void TrackOnboardingCompleted()
    {
        try
        {
            _client?.Capture(
                GetDistinctId(),
                "onboarding_completed",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["platform"] = "windows",
                });
        }
        catch
        {
            // Never block on analytics failure.
        }
    }

    /// <summary>
    /// Tracks the 'settings_opened' event when the user opens the Settings window
    /// from the tray menu.
    /// </summary>
    public static void TrackSettingsOpened()
    {
        try
        {
            _client?.Capture(
                GetDistinctId(),
                "settings_opened",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["platform"] = "windows",
                });
        }
        catch
        {
            // Never block on analytics failure.
        }
    }

    /// <summary>
    /// Tracks the 'provider_test_run' event when the user clicks a Test button
    /// in the Settings window. Includes the service name, success/failure, and
    /// an error category if applicable.
    /// </summary>
    public static void TrackProviderTestRun(string service, bool success, string? errorCategory = null)
    {
        try
        {
            var props = new System.Collections.Generic.Dictionary<string, object>
            {
                ["service"] = service,
                ["success"] = success,
                ["platform"] = "windows",
            };
            if (errorCategory is not null)
                props["error_category"] = errorCategory;

            _client?.Capture(GetDistinctId(), "provider_test_run", props);
        }
        catch
        {
            // Never block on analytics failure.
        }
    }

    /// <summary>
    /// Tracks the 'model_switched' event when the user changes LLM provider/model
    /// via the tray menu. Respects the analyticsOptOut flag.
    /// </summary>
    public static void TrackModelSwitched(string fromProvider, string fromModel, string toProvider, string toModel)
    {
        try
        {
            _client?.Capture(
                GetDistinctId(),
                "model_switched",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["from_provider"] = fromProvider,
                    ["from_model"] = fromModel,
                    ["to_provider"] = toProvider,
                    ["to_model"] = toModel,
                    ["platform"] = "windows",
                });
        }
        catch
        {
            // Never block on analytics failure.
        }
    }

    private static string ReadAppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            return asm?.GetName().Version?.ToString() ?? "0.0.1";
        }
        catch
        {
            return "0.0.1";
        }
    }

    /// <summary>
    /// Generates a stable anonymous distinct ID for this machine/user,
    /// stored in the registry so it persists across launches.
    /// </summary>
    private static string GetDistinctId()
    {
        const string distinctIdValueName = "analyticsDistinctId";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey);
            var existing = key.GetValue(distinctIdValueName) as string;
            if (!string.IsNullOrEmpty(existing)) return existing;

            var newId = Guid.NewGuid().ToString();
            key.SetValue(distinctIdValueName, newId, RegistryValueKind.String);
            return newId;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Flushes any pending events and disposes the client.
    /// Call from App.OnExit.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _client?.Dispose();
        }
        catch
        {
            // Ignore shutdown errors.
        }
        _client = null;
    }
}
