using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Runtime.Router;

namespace Launcher.Runtime.Monitoring;

public sealed partial class LlamaTelemetryClient(HttpClient httpClient)
{
    public async Task<LlamaTelemetrySnapshot> ReadAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        LlamaModelManagementClient.ValidateBaseUri(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var query = "?model=" + Uri.EscapeDataString(modelId) + "&autoload=false";
        try
        {
            using var metricsResponse = await httpClient.GetAsync(new Uri(baseUri, "metrics" + query), cancellationToken).ConfigureAwait(false);
            using var slotsResponse = await httpClient.GetAsync(new Uri(baseUri, "slots" + query), cancellationToken).ConfigureAwait(false);
            if (!metricsResponse.IsSuccessStatusCode || !slotsResponse.IsSuccessStatusCode)
            {
                return Empty($"llama 监控端点不可用（metrics {(int)metricsResponse.StatusCode} / slots {(int)slotsResponse.StatusCode}）。");
            }

            var metricsText = await metricsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var metrics = ParseMetrics(metricsText);
            var promptTokens = ReadLongMetric(metrics, "prompt_tokens_total");
            var predictedTokens = ReadLongMetric(metrics, "tokens_predicted_total");
            var promptRate = ReadMetric(metrics, "prompt_tokens_seconds")
                ?? Rate(promptTokens, ReadMetric(metrics, "prompt_seconds_total"));
            var predictedRate = ReadMetric(metrics, "predicted_tokens_seconds")
                ?? Rate(predictedTokens, ReadMetric(metrics, "tokens_predicted_seconds_total"));
            await using var slotsStream = await slotsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var slotsDocument = await JsonDocument.ParseAsync(slotsStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var slots = ParseSlots(slotsDocument.RootElement);
            return new LlamaTelemetrySnapshot(
                slots.Count,
                slots.Processing,
                slots.Capacity,
                slots.Used,
                ReadMetric(metrics, "kv_cache_usage_ratio"),
                promptTokens,
                predictedTokens,
                promptRate,
                predictedRate,
                ReadIntMetric(metrics, "requests_processing"),
                ReadIntMetric(metrics, "requests_deferred"),
                null);
        }
        catch (HttpRequestException exception)
        {
            return Empty(exception.Message);
        }
        catch (JsonException)
        {
            return Empty("llama /slots 返回了无法识别的数据。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Empty("llama 运行状态读取超时。");
        }
    }

    public static IReadOnlyDictionary<string, double> ParseMetrics(string text)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (text.Length > 2 * 1024 * 1024)
        {
            return values;
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var match = MetricRegex().Match(line.Trim());
            if (!match.Success
                || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
            {
                continue;
            }

            var rawName = match.Groups["name"].Value;
            var normalized = rawName[(rawName.LastIndexOf(':') + 1)..];
            values[normalized] = value;
        }

        return values;
    }

    public static (int Count, int Processing, int? Capacity, int? Used) ParseSlots(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return (0, 0, null, null);
        }

        var count = 0;
        var processing = 0;
        var candidates = new List<(bool Processing, int? Capacity, int? Used)>();
        foreach (var slot in root.EnumerateArray().Take(128))
        {
            count++;
            var isProcessing = slot.TryGetProperty("is_processing", out var processingNode)
                && processingNode.ValueKind == JsonValueKind.True;
            if (isProcessing)
            {
                processing++;
            }

            int? capacity = TryReadInt(slot, "n_ctx", out var nctx) && nctx >= 0 ? nctx : null;
            int? used = (TryReadInt(slot, "n_past", out var npast) || TryReadInt(slot, "n_tokens", out npast)) && npast >= 0
                ? npast
                : null;
            candidates.Add((isProcessing, capacity, used));
        }

        // /slots represents several independent conversations. Showing their sum as one
        // context would be misleading, so prefer the active slot, then the fullest slot.
        var selected = candidates
            .OrderByDescending(candidate => candidate.Processing)
            .ThenByDescending(candidate => candidate.Used ?? -1)
            .FirstOrDefault();
        return (count, processing, selected.Capacity, selected.Used);
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var node)
            && node.TryGetInt32(out value);
    }

    private static double? ReadMetric(IReadOnlyDictionary<string, double> metrics, string suffix) =>
        metrics.FirstOrDefault(pair => pair.Key.EndsWith(suffix, StringComparison.Ordinal)).Value is var value
        && metrics.Keys.Any(key => key.EndsWith(suffix, StringComparison.Ordinal)) ? value : null;

    private static long? ReadLongMetric(IReadOnlyDictionary<string, double> metrics, string suffix) =>
        ReadMetric(metrics, suffix) is { } value ? (long)Math.Round(value) : null;

    private static int? ReadIntMetric(IReadOnlyDictionary<string, double> metrics, string suffix) =>
        ReadMetric(metrics, suffix) is { } value ? (int)Math.Round(value) : null;

    private static double? Rate(long? tokens, double? seconds) =>
        tokens is > 0 && seconds is > 0 ? tokens.Value / seconds.Value : null;

    private static LlamaTelemetrySnapshot Empty(string diagnostic) =>
        new(0, 0, null, null, null, null, null, null, null, null, null, diagnostic);

    [GeneratedRegex(@"^(?<name>[A-Za-z_:][A-Za-z0-9_:]*)(?:\{[^\r\n]*\})?\s+(?<value>[-+0-9.eE]+)(?:\s+\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MetricRegex();
}
