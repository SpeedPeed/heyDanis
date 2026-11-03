using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace Clicky.Audio;

/// <summary>
/// Enumerates the system's active input (capture) and output (render) audio
/// endpoints so the user can pick a specific microphone or speaker from
/// SettingsWindow rather than relying on the Windows default device.
/// </summary>
public static class AudioDevices
{
    /// <summary>A single selectable audio endpoint.</summary>
    public readonly record struct DeviceInfo(string Id, string FriendlyName);

    /// <summary>
    /// Sentinel <see cref="DeviceInfo.Id"/> used by the UI to mean
    /// "use whatever Windows considers the default device right now".
    /// An empty string is treated equivalently throughout the capture /
    /// playback code.
    /// </summary>
    public const string DefaultDeviceId = "";

    /// <summary>Enumerates active input (microphone) devices plus a leading "System default" entry.</summary>
    public static IReadOnlyList<DeviceInfo> EnumerateInputDevices()
        => EnumerateEndpoints(DataFlow.Capture);

    /// <summary>Enumerates active output (speaker/headphone) devices plus a leading "System default" entry.</summary>
    public static IReadOnlyList<DeviceInfo> EnumerateOutputDevices()
        => EnumerateEndpoints(DataFlow.Render);

    private static IReadOnlyList<DeviceInfo> EnumerateEndpoints(DataFlow flow)
    {
        var list = new List<DeviceInfo>
        {
            new(DefaultDeviceId, "System default"),
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    list.Add(new DeviceInfo(device.ID, device.FriendlyName));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // Enumeration can fail on some virtualized audio stacks — fall back
            // to just the default entry so the UI still renders.
        }

        return list;
    }

    /// <summary>
    /// Resolves a user-selected device ID to an <see cref="MMDevice"/>, or
    /// returns <c>null</c> if the ID is empty (meaning "use default") or the
    /// device no longer exists. Caller is responsible for disposing the
    /// returned device.
    /// </summary>
    public static MMDevice? TryResolveDevice(string? deviceId, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        try
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                var device = enumerator.GetDevice(deviceId);
                // Reject devices that aren't the right flow or aren't active —
                // happens when a removable device has been unplugged since the
                // user saved the setting.
                if (device.DataFlow != flow || device.State != DeviceState.Active)
                {
                    device.Dispose();
                    enumerator.Dispose();
                    return null;
                }
                // Hand the device to the caller; we intentionally leak the
                // enumerator here because disposing it before the returned
                // device can invalidate the COM wrapper.
                return device;
            }
            catch
            {
                enumerator.Dispose();
                return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
