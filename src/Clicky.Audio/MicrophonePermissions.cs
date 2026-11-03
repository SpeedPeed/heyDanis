using System;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace Clicky.Audio;

/// <summary>
/// Determines whether Clicky can actually read from the default microphone.
/// Mirrors the Mac one-shot AVCaptureDevice.requestAccess probe in
/// BuddyDictationManager.swift — on Windows 10/11 the privacy gate causes
/// WasapiCapture.StartRecording to throw when mic access is blocked for
/// the app, so opening and immediately closing the device is a reliable
/// signal.
///
/// A packaged (MSIX) build could also call
/// Windows.Security.Authorization.AppCapabilityAccess.AppCapability but
/// that requires a package identity, which Clicky does not have in the
/// current dev loop. The probe approach works for both packaged and
/// unpackaged builds.
/// </summary>
public static class MicrophonePermissions
{
    /// <summary>
    /// Briefly opens the default WASAPI capture device to verify the
    /// microphone privacy gate allows access. Returns <c>true</c> on
    /// success, <c>false</c> if any exception is thrown.
    /// </summary>
    public static Task<bool> ProbeAsync()
    {
        return Task.Run(() =>
        {
            WasapiCapture? capture = null;
            try
            {
                capture = new WasapiCapture();
                capture.StartRecording();
                capture.StopRecording();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                capture?.Dispose();
            }
        });
    }
}
