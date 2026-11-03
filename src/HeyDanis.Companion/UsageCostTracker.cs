using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HeyDanis.Api;

namespace HeyDanis.Companion;

/// <summary>
/// Claude list prices in US dollars per million tokens (as of 2026-11).
/// Only used for the cost estimate shown in the app — the real bill is on
/// console.anthropic.com.
/// </summary>
public static class ClaudePricing
{
    private sealed record Price(decimal InputPerMillion, decimal OutputPerMillion, decimal CacheReadPerMillion);

    private static readonly Dictionary<string, Price> PricesByModelPrefix = new()
    {
        ["claude-haiku-5-5"] = new(0.10m, 0.50m, 0.01m),
        ["claude-sonnet-5-5"] = new(2.00m, 10.00m, 0.20m),
        ["claude-opus-5-5"] = new(4.00m, 20.00m, 0.20m),
    };

    /// <summary>Fallback for unknown models: assume Sonnet pricing so we never under-report.</summary>
    private static readonly Price UnknownModelPrice = new(2.00m, 10.00m, 0.20m);

    public static decimal EstimateCostUsd(LlmUsage usage)
    {
        var price = PricesByModelPrefix
            .FirstOrDefault(entry => usage.Model.StartsWith(entry.Key, StringComparison.Ordinal))
            .Value ?? UnknownModelPrice;

        // Cache writes are billed slightly above normal input; counting them as input is close enough.
        var regularInputTokens = usage.InputTokens + usage.CacheCreationInputTokens;
        return (regularInputTokens * price.InputPerMillion
                + usage.OutputTokens * price.OutputPerMillion
                + usage.CacheReadInputTokens * price.CacheReadPerMillion) / 1_000_000m;
    }
}

/// <summary>
/// Adds up Claude usage per calendar month in <c>%APPDATA%\heyDanis\usage.json</c>
/// so the user always sees what heyDanis has cost this month.
/// </summary>
public sealed class UsageCostTracker
{
    private readonly string _filePath;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private UsageData _data;

    /// <summary>Raised (on a background thread) after every recorded request.</summary>
    public event EventHandler? Changed;

    public UsageCostTracker()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "heyDanis", "usage.json"))
    {
    }

    internal UsageCostTracker(string filePath, Func<DateTime>? now = null)
    {
        _filePath = filePath;
        _now = now ?? (() => DateTime.Now);
        _data = Load();
    }

    public decimal CurrentMonthCostUsd
    {
        get { lock (_lock) { RollOverIfNewMonth(); return _data.CostUsd; } }
    }

    public int CurrentMonthRequestCount
    {
        get { lock (_lock) { RollOverIfNewMonth(); return _data.RequestCount; } }
    }

    public void Record(LlmUsage usage)
    {
        lock (_lock)
        {
            RollOverIfNewMonth();
            _data.CostUsd += ClaudePricing.EstimateCostUsd(usage);
            _data.InputTokens += usage.InputTokens + usage.CacheCreationInputTokens + usage.CacheReadInputTokens;
            _data.OutputTokens += usage.OutputTokens;
            _data.RequestCount++;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>E.g. "0,42 $ (318 Fragen)" — dollars, because that's how Anthropic bills.</summary>
    public string FormatCurrentMonth()
    {
        decimal cost;
        int requests;
        lock (_lock)
        {
            RollOverIfNewMonth();
            cost = _data.CostUsd;
            requests = _data.RequestCount;
        }

        var german = CultureInfo.GetCultureInfo("de-AT");
        var costText = cost < 0.01m && cost > 0
            ? "< 0,01 $"
            : cost.ToString("0.00", german) + " $";
        var questionsText = requests == 1 ? "1 Frage" : $"{requests} Fragen";
        return $"{costText} ({questionsText})";
    }

    private string CurrentMonthKey => _now().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private void RollOverIfNewMonth()
    {
        if (_data.Month == CurrentMonthKey) return;
        _data = new UsageData { Month = CurrentMonthKey };
        Save();
    }

    private UsageData Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<UsageData>(File.ReadAllText(_filePath));
                if (loaded is not null && loaded.Month == CurrentMonthKey)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UsageCostTracker: failed to load usage: {ex.Message}");
        }
        return new UsageData { Month = CurrentMonthKey };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UsageCostTracker: failed to save usage: {ex.Message}");
        }
    }

    internal sealed class UsageData
    {
        [JsonPropertyName("month")] public string Month { get; set; } = "";
        [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
        [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
        [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
        [JsonPropertyName("requestCount")] public int RequestCount { get; set; }
    }
}
