using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Clicky.Companion;

/// <summary>
/// Manages the onboarding state via the Windows registry
/// (HKCU\Software\Clicky\onboarded). Mirrors Mac's
/// hasCompletedOnboarding UserDefaults key.
/// </summary>
public static class OnboardingService
{
    private const string RegistryKeyPath = @"Software\Clicky";
    private const string OnboardedValueName = "onboarded";

    /// <summary>
    /// Returns <c>true</c> if HKCU\Software\Clicky\onboarded is set to 1.
    /// </summary>
    public static bool HasCompletedOnboarding()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            if (key is null) return false;
            var value = key.GetValue(OnboardedValueName);
            return value is int intVal && intVal == 1;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read onboarding state: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets HKCU\Software\Clicky\onboarded to 1.
    /// </summary>
    public static void MarkOnboardingComplete()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            key.SetValue(OnboardedValueName, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set onboarding state: {ex.Message}");
        }
    }
}
