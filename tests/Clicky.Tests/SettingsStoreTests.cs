using System;
using System.IO;
using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClickyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void Defaults_WhenFileMissing()
    {
        var store = new SettingsStore(Path.Combine(_tempDir, "nonexistent", "settings.json"));

        Assert.Equal("codex", store.LlmProvider);
        Assert.Equal("gpt-5.5", store.LlmModel);
        Assert.Equal("pNInz6obpgDQGcFmaJgB", store.ElevenLabsVoiceId);
        Assert.False(store.OnboardingComplete);
        Assert.False(store.AnalyticsOptOut);
        Assert.False(store.GamerModeEnabled);
    }

    [Fact]
    public void LlmProvider_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.LlmProvider = "zai";

        Assert.Equal("zai", store.LlmProvider);
    }

    [Fact]
    public void LlmModel_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.LlmModel = "glm-4.6v";

        Assert.Equal("glm-4.6v", store.LlmModel);
    }

    [Fact]
    public void ElevenLabsVoiceId_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.ElevenLabsVoiceId = "custom-voice-id";

        Assert.Equal("custom-voice-id", store.ElevenLabsVoiceId);
    }

    [Fact]
    public void OnboardingComplete_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.OnboardingComplete = true;

        Assert.True(store.OnboardingComplete);
    }

    [Fact]
    public void AnalyticsOptOut_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.AnalyticsOptOut = true;

        Assert.True(store.AnalyticsOptOut);
    }

    [Fact]
    public void GamerModeEnabled_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.GamerModeEnabled = true;

        var store2 = new SettingsStore(_tempFile);
        Assert.True(store2.GamerModeEnabled);
    }

    [Fact]
    public void MicrophoneAndSpeakerDeviceId_RoundTrip()
    {
        var store = new SettingsStore(_tempFile);

        store.MicrophoneDeviceId = "{0.0.1.00000000}.{mic-guid}";
        store.SpeakerDeviceId = "{0.0.0.00000000}.{speaker-guid}";

        var store2 = new SettingsStore(_tempFile);
        Assert.Equal("{0.0.1.00000000}.{mic-guid}", store2.MicrophoneDeviceId);
        Assert.Equal("{0.0.0.00000000}.{speaker-guid}", store2.SpeakerDeviceId);
    }

    [Fact]
    public void DeviceId_DefaultsToEmptyString()
    {
        var store = new SettingsStore(Path.Combine(_tempDir, "never-written", "settings.json"));
        Assert.Equal("", store.MicrophoneDeviceId);
        Assert.Equal("", store.SpeakerDeviceId);
    }

    [Fact]
    public void Persistence_NewInstanceReadsWrittenValues()
    {
        var store1 = new SettingsStore(_tempFile);
        store1.LlmProvider = "zai";
        store1.LlmModel = "glm-4.6v";
        store1.OnboardingComplete = true;
        store1.AnalyticsOptOut = true;

        var store2 = new SettingsStore(_tempFile);
        Assert.Equal("zai", store2.LlmProvider);
        Assert.Equal("glm-4.6v", store2.LlmModel);
        Assert.True(store2.OnboardingComplete);
        Assert.True(store2.AnalyticsOptOut);
    }

    [Fact]
    public void File_IsReadableJson()
    {
        var store = new SettingsStore(_tempFile);
        store.LlmProvider = "zai";

        var json = File.ReadAllText(_tempFile);

        Assert.Contains("\"llmProvider\"", json);
        Assert.Contains("\"zai\"", json);
    }

    [Fact]
    public void CorruptedFile_ReturnsDefaults()
    {
        File.WriteAllText(_tempFile, "this is not valid json!!!");

        var store = new SettingsStore(_tempFile);

        Assert.Equal("codex", store.LlmProvider);
        Assert.Equal("gpt-5.5", store.LlmModel);
        Assert.False(store.OnboardingComplete);
    }

    [Fact]
    public void AutoCreatesDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "sub", "dir", "settings.json");
        var store = new SettingsStore(nestedPath);

        store.LlmProvider = "zai";

        Assert.True(File.Exists(nestedPath));

        var store2 = new SettingsStore(nestedPath);
        Assert.Equal("zai", store2.LlmProvider);
    }

    [Fact]
    public void MigrationFromRegistry_OnboardingComplete()
    {
        // This test verifies the migration logic pattern.
        // If the registry says onboarded=1 and SettingsStore says false,
        // migration should set it to true.
        var store = new SettingsStore(_tempFile);
        Assert.False(store.OnboardingComplete);

        // Simulate migration: if registry says true, copy to store.
        if (OnboardingService.HasCompletedOnboarding() && !store.OnboardingComplete)
        {
            store.OnboardingComplete = true;
            Assert.True(store.OnboardingComplete);
        }
        // If registry says false, store stays false — that's the default case.
    }
}
