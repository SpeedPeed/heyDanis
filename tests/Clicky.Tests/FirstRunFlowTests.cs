using System;
using System.IO;
using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// Tests for US-023 first-run setup flow logic:
/// - HasRequiredKeys checks
/// - SettingsViewModel field validation
/// - CompanionViewModel error banner
/// </summary>
public class FirstRunFlowTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SecretsStore _secrets;
    private readonly SettingsStore _settings;

    public FirstRunFlowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClickyFirstRunTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _secrets = new SecretsStore(Path.Combine(_tempDir, "secrets.bin"));
        _settings = new SettingsStore(Path.Combine(_tempDir, "settings.json"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { }
    }

    // -- HasRequiredKeys tests --

    [Fact]
    public void HasRequiredKeys_FalseWhenNoSecrets()
    {
        // Fresh stores with no keys.
        Assert.False(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_TrueWhenAllAnthropicKeysPresent()
    {
        _settings.LlmProvider = "anthropic";
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test");
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.True(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_TrueWhenAllZaiKeysPresent()
    {
        _settings.LlmProvider = "zai";
        _secrets.Write(SecretsStore.ZaiApiKey, "zai-test");
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.True(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_TrueWhenAllOpenAiKeysPresent()
    {
        _settings.LlmProvider = "openai";
        _secrets.Write(SecretsStore.OpenAiApiKey, "openai-test");
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.True(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_FalseWhenLlmKeyPresentButAssemblyAiMissing()
    {
        _settings.LlmProvider = "anthropic";
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test");
        // AssemblyAI key missing.
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.False(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_FalseWhenLlmKeyPresentButElevenLabsMissing()
    {
        _settings.LlmProvider = "anthropic";
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test");
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        // ElevenLabs key missing.

        Assert.False(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_TrueWhenAudioKeysPresentWithoutLlmKeyForCodexOAuth()
    {
        _settings.LlmProvider = "anthropic";
        // No Anthropic key.
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.True(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_TrueWhenLegacyZaiProviderButAudioKeysPresent()
    {
        _settings.LlmProvider = "zai";
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test"); // Wrong provider's key.
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.True(App.App.HasRequiredKeys(_secrets, _settings));
    }

    [Fact]
    public void HasRequiredKeys_TrueWhenLegacyOpenAiProviderButAudioKeysPresent()
    {
        _settings.LlmProvider = "openai";
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test");
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        Assert.True(App.App.HasRequiredKeys(_secrets, _settings));
    }

    // -- ValidateRequiredFields tests --

    [Fact]
    public void ValidateRequiredFields_MarksAnthropicKeyMissingWhenSelectedProvider()
    {
        var vm = new SettingsViewModel(_secrets, _settings);
        vm.SelectedProvider = "anthropic";

        vm.ValidateRequiredFields();

        Assert.Null(vm.AnthropicKeyError);
        Assert.Null(vm.OpenAiKeyError);
        Assert.Null(vm.ZaiKeyError); // Not required for anthropic provider.
        Assert.NotNull(vm.AssemblyAiKeyError);
        Assert.NotNull(vm.ElevenLabsKeyError);
    }

    [Fact]
    public void ValidateRequiredFields_MarksZaiKeyMissingWhenSelectedProvider()
    {
        var vm = new SettingsViewModel(_secrets, _settings);
        vm.SelectedProvider = "zai";

        vm.ValidateRequiredFields();

        Assert.Null(vm.AnthropicKeyError); // Not required for zai provider.
        Assert.Null(vm.OpenAiKeyError);
        Assert.Null(vm.ZaiKeyError);
        Assert.NotNull(vm.AssemblyAiKeyError);
        Assert.NotNull(vm.ElevenLabsKeyError);
    }

    [Fact]
    public void ValidateRequiredFields_MarksOpenAiKeyMissingWhenSelectedProvider()
    {
        var vm = new SettingsViewModel(_secrets, _settings);
        vm.SelectedProvider = "openai";

        vm.ValidateRequiredFields();

        Assert.Null(vm.AnthropicKeyError);
        Assert.Null(vm.OpenAiKeyError);
        Assert.Null(vm.ZaiKeyError);
        Assert.NotNull(vm.AssemblyAiKeyError);
        Assert.NotNull(vm.ElevenLabsKeyError);
    }

    [Fact]
    public void ValidateRequiredFields_NoErrorsWhenAllKeysPresent()
    {
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test");
        _secrets.Write(SecretsStore.AssemblyAiApiKey, "aai-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        var vm = new SettingsViewModel(_secrets, _settings);
        vm.SelectedProvider = "anthropic";

        vm.ValidateRequiredFields();

        Assert.Null(vm.AnthropicKeyError);
        Assert.Null(vm.AssemblyAiKeyError);
        Assert.Null(vm.ElevenLabsKeyError);
    }

    [Fact]
    public void ValidateRequiredFields_PartialConfig_OnlyMissingFieldsHighlighted()
    {
        // LLM key present, but AssemblyAI missing.
        _secrets.Write(SecretsStore.AnthropicApiKey, "sk-test");
        _secrets.Write(SecretsStore.ElevenLabsApiKey, "el-test");

        var vm = new SettingsViewModel(_secrets, _settings);
        vm.SelectedProvider = "anthropic";

        vm.ValidateRequiredFields();

        Assert.Null(vm.AnthropicKeyError); // Present, no error.
        Assert.NotNull(vm.AssemblyAiKeyError); // Missing, highlighted.
        Assert.Null(vm.ElevenLabsKeyError); // Present, no error.
    }

    [Fact]
    public void ValidateRequiredFields_ErrorClearsWhenKeyEntered()
    {
        var vm = new SettingsViewModel(_secrets, _settings);
        vm.SelectedProvider = "anthropic";
        vm.ValidateRequiredFields();

        Assert.Null(vm.AnthropicKeyError);

        // User enters the key — error should clear.
        vm.AnthropicApiKey = "sk-new";

        Assert.Null(vm.AnthropicKeyError);
    }

    // -- CompanionViewModel LastError tests --

    [Fact]
    public void LastError_InitiallyNull()
    {
        var vm = new CompanionViewModel();
        Assert.Null(vm.LastError);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void LastError_SetAndClear()
    {
        var vm = new CompanionViewModel();

        vm.LastError = "Your API key is missing or invalid. Open Settings to fix.";
        Assert.True(vm.HasError);
        Assert.Equal("Your API key is missing or invalid. Open Settings to fix.", vm.LastError);

        vm.ClearError();
        Assert.False(vm.HasError);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public void LastError_RaisesPropertyChanged()
    {
        var vm = new CompanionViewModel();
        var lastErrorChanged = false;
        var hasErrorChanged = false;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompanionViewModel.LastError)) lastErrorChanged = true;
            if (e.PropertyName == nameof(CompanionViewModel.HasError)) hasErrorChanged = true;
        };

        vm.LastError = "test error";

        Assert.True(lastErrorChanged);
        Assert.True(hasErrorChanged);
    }
}
