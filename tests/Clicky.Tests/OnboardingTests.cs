using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

public class OnboardingTests
{
    [Fact]
    public void IsOnboardingVisible_TrueWhenNotOnboardedAndNoPermissions()
    {
        var vm = new CompanionViewModel();
        // Default: HasCompletedOnboarding=false, no permissions
        Assert.True(vm.IsOnboardingVisible);
    }

    [Fact]
    public void IsOnboardingVisible_TrueWhenNotOnboardedButAllPermissionsGranted()
    {
        var vm = new CompanionViewModel
        {
            HasMicrophonePermission = true,
            HasScreenCapturePermission = true,
        };
        // Not yet onboarded → still visible
        Assert.True(vm.IsOnboardingVisible);
    }

    [Fact]
    public void IsOnboardingVisible_FalseWhenOnboardedAndAllPermissionsGranted()
    {
        var vm = new CompanionViewModel
        {
            HasMicrophonePermission = true,
            HasScreenCapturePermission = true,
            HasCompletedOnboarding = true,
        };
        Assert.False(vm.IsOnboardingVisible);
    }

    [Fact]
    public void IsOnboardingVisible_TrueWhenOnboardedButMicrophoneRevoked()
    {
        var vm = new CompanionViewModel
        {
            HasMicrophonePermission = true,
            HasScreenCapturePermission = true,
            HasCompletedOnboarding = true,
        };
        Assert.False(vm.IsOnboardingVisible);

        // Simulate permission revocation
        vm.HasMicrophonePermission = false;
        Assert.True(vm.IsOnboardingVisible);
    }

    [Fact]
    public void IsOnboardingVisible_TrueWhenOnboardedButCaptureRevoked()
    {
        var vm = new CompanionViewModel
        {
            HasMicrophonePermission = true,
            HasScreenCapturePermission = true,
            HasCompletedOnboarding = true,
        };
        Assert.False(vm.IsOnboardingVisible);

        vm.HasScreenCapturePermission = false;
        Assert.True(vm.IsOnboardingVisible);
    }

    [Fact]
    public void AllPermissionsGranted_RequiresBothPermissions()
    {
        var vm = new CompanionViewModel();
        Assert.False(vm.AllPermissionsGranted);

        vm.HasMicrophonePermission = true;
        Assert.False(vm.AllPermissionsGranted);

        vm.HasScreenCapturePermission = true;
        Assert.True(vm.AllPermissionsGranted);
    }

    [Fact]
    public void HasCompletedOnboarding_NotifiesIsOnboardingVisible()
    {
        var vm = new CompanionViewModel
        {
            HasMicrophonePermission = true,
            HasScreenCapturePermission = true,
        };
        Assert.True(vm.IsOnboardingVisible);

        var notifiedProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                notifiedProperties.Add(e.PropertyName);
        };

        vm.HasCompletedOnboarding = true;

        Assert.Contains(nameof(CompanionViewModel.HasCompletedOnboarding), notifiedProperties);
        Assert.Contains(nameof(CompanionViewModel.IsOnboardingVisible), notifiedProperties);
        Assert.False(vm.IsOnboardingVisible);
    }

    [Fact]
    public void PermissionChange_NotifiesIsOnboardingVisible()
    {
        var vm = new CompanionViewModel { HasCompletedOnboarding = true };

        var notifiedProperties = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                notifiedProperties.Add(e.PropertyName);
        };

        vm.HasMicrophonePermission = true;

        Assert.Contains(nameof(CompanionViewModel.IsOnboardingVisible), notifiedProperties);
    }

    [Fact]
    public void OnboardingService_RoundTrip()
    {
        // This test verifies that OnboardingService reads/writes to the
        // registry. It's safe to run because it uses HKCU and only affects
        // the Clicky key. We read the current state, and if onboarding was
        // not complete we mark it and verify, then restore the original state.
        var wasOnboarded = OnboardingService.HasCompletedOnboarding();

        if (!wasOnboarded)
        {
            OnboardingService.MarkOnboardingComplete();
            Assert.True(OnboardingService.HasCompletedOnboarding());

            // Restore: delete the key value by writing 0 so we don't
            // permanently affect the dev machine.
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Clicky", writable: true);
                key?.DeleteValue("onboarded", throwOnMissingValue: false);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
        else
        {
            // Already onboarded — just verify the read
            Assert.True(OnboardingService.HasCompletedOnboarding());
        }
    }
}
