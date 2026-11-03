using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Clicky.Companion;

/// <summary>
/// Encrypts API keys at rest using Windows DPAPI (per-user scope).
/// Secrets are stored as a single encrypted blob at %APPDATA%\Clicky\secrets.bin.
/// </summary>
public class SecretsStore
{
    /// <summary>Standardized key constants for each service.</summary>
    public const string AnthropicApiKey = "anthropic_api_key";
    public const string OpenAiApiKey = "openai_api_key";
    public const string ZaiApiKey = "zai_api_key";
    public const string AssemblyAiApiKey = "assemblyai_api_key";
    public const string ElevenLabsApiKey = "elevenlabs_api_key";

    private readonly string _filePath;
    private readonly object _lock = new();

    public SecretsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Clicky", "secrets.bin"))
    {
    }

    internal SecretsStore(string filePath)
    {
        _filePath = filePath;
    }

    public void Write(string key, string value)
    {
        lock (_lock)
        {
            var dict = LoadDictionary();
            dict[key] = value;
            SaveDictionary(dict);
        }
    }

    public string? Read(string key)
    {
        lock (_lock)
        {
            var dict = LoadDictionary();
            return dict.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Delete(string key)
    {
        lock (_lock)
        {
            var dict = LoadDictionary();
            if (dict.Remove(key))
            {
                SaveDictionary(dict);
            }
        }
    }

    public bool Exists(string key)
    {
        lock (_lock)
        {
            var dict = LoadDictionary();
            return dict.ContainsKey(key);
        }
    }

    private Dictionary<string, string> LoadDictionary()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, string>();

            var encrypted = File.ReadAllBytes(_filePath);
            if (encrypted.Length == 0)
                return new Dictionary<string, string>();

            var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch (CryptographicException ex)
        {
            Debug.WriteLine($"SecretsStore: DPAPI decryption failed (different user profile?): {ex.Message}");
            return new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SecretsStore: failed to load secrets: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    private void SaveDictionary(Dictionary<string, string> dict)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(dict);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, encrypted);
    }
}
