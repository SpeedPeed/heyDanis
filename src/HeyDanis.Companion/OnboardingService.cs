using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace HeyDanis.Companion;

/// <summary>
/// Manages the onboarding state via the Windows registry
/// (HKCU\Software\HeyDanis\onboarded). Mirrors Mac's
/// hasCompletedOnboarding UserDefaults key.
/// </summary>
public static class OnboardingService
{
    private const string RegistryKeyPath = @"Software\heyDanis";
    private const string OnboardedValueName = "onboarded";

    /// <summary>
    /// Returns <c>true</c> if HKCU\Software\HeyDanis\onboarded is set to 1.
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
    /// Sets HKCU\Software\HeyDanis\onboarded to 1.
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
