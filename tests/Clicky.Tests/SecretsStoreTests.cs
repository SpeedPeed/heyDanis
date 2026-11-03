using System;
using System.IO;
using System.Security.Cryptography;
using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

public class SecretsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public SecretsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClickyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "secrets.bin");
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
    public void Write_Read_RoundTrip_ReturnsSameValue()
    {
        var store = new SecretsStore(_tempFile);
        store.Write("test_key", "super-secret-value-123");

        var result = store.Read("test_key");

        Assert.Equal("super-secret-value-123", result);
    }

    [Fact]
    public void Read_MissingKey_ReturnsNull()
    {
        var store = new SecretsStore(_tempFile);

        var result = store.Read("nonexistent_key");

        Assert.Null(result);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        var store = new SecretsStore(Path.Combine(_tempDir, "nonexistent", "secrets.bin"));

        var result = store.Read("any_key");

        Assert.Null(result);
    }

    [Fact]
    public void Exists_ReturnsTrueForWrittenKey()
    {
        var store = new SecretsStore(_tempFile);
        store.Write("test_key", "value");

        Assert.True(store.Exists("test_key"));
        Assert.False(store.Exists("other_key"));
    }

    [Fact]
    public void Delete_RemovesKey()
    {
        var store = new SecretsStore(_tempFile);
        store.Write("test_key", "value");
        Assert.True(store.Exists("test_key"));

        store.Delete("test_key");

        Assert.False(store.Exists("test_key"));
        Assert.Null(store.Read("test_key"));
    }

    [Fact]
    public void Delete_NonexistentKey_DoesNotThrow()
    {
        var store = new SecretsStore(_tempFile);

        var ex = Record.Exception(() => store.Delete("nonexistent"));

        Assert.Null(ex);
    }

    [Fact]
    public void Write_MultipleKeys_AllPersist()
    {
        var store = new SecretsStore(_tempFile);

        store.Write(SecretsStore.AnthropicApiKey, "sk-ant-123");
        store.Write(SecretsStore.OpenAiApiKey, "openai-234");
        store.Write(SecretsStore.ZaiApiKey, "zai-456");
        store.Write(SecretsStore.AssemblyAiApiKey, "aai-789");
        store.Write(SecretsStore.ElevenLabsApiKey, "el-abc");

        Assert.Equal("sk-ant-123", store.Read(SecretsStore.AnthropicApiKey));
        Assert.Equal("openai-234", store.Read(SecretsStore.OpenAiApiKey));
        Assert.Equal("zai-456", store.Read(SecretsStore.ZaiApiKey));
        Assert.Equal("aai-789", store.Read(SecretsStore.AssemblyAiApiKey));
        Assert.Equal("el-abc", store.Read(SecretsStore.ElevenLabsApiKey));
    }

    [Fact]
    public void Write_OverwritesExistingValue()
    {
        var store = new SecretsStore(_tempFile);
        store.Write("key", "old_value");
        store.Write("key", "new_value");

        Assert.Equal("new_value", store.Read("key"));
    }

    [Fact]
    public void Persistence_NewInstanceReadsWrittenValues()
    {
        var store1 = new SecretsStore(_tempFile);
        store1.Write("key", "persisted_value");

        var store2 = new SecretsStore(_tempFile);
        Assert.Equal("persisted_value", store2.Read("key"));
    }

    [Fact]
    public void File_IsEncrypted_NotPlaintext()
    {
        var store = new SecretsStore(_tempFile);
        store.Write("key", "my-secret-api-key");

        var rawBytes = File.ReadAllBytes(_tempFile);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);

        // The file should not contain the plaintext key
        Assert.DoesNotContain("my-secret-api-key", rawText);
    }

    [Fact]
    public void CorruptedFile_ReturnsNull()
    {
        // Write some garbage bytes that aren't valid DPAPI output
        File.WriteAllBytes(_tempFile, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var store = new SecretsStore(_tempFile);
        var result = store.Read("key");

        Assert.Null(result);
    }

    [Fact]
    public void AutoCreatesDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "sub", "dir", "secrets.bin");
        var store = new SecretsStore(nestedPath);

        store.Write("key", "value");

        Assert.True(File.Exists(nestedPath));
        Assert.Equal("value", store.Read("key"));
    }
}
