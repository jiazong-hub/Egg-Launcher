using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Runtime.Router;

namespace Launcher.Runtime.Monitoring;

public sealed partial class LlamaTelemetryClient(HttpClient httpClient)
{
    private readonly object _sampleGate = new();
    private readonly Dictionary<int, SlotCounter> _previousSlots = [];
    private string? _sampleTarget;
    private long _previousSampleTimestamp;
    private long? _previousPredictedTokens;
    private double? _smoothedPredictedRate;

    public async Task<LlamaTelemetrySnapshot> ReadAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        LlamaModelManagementClient.ValidateBaseUri(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var query = "?model=" + Uri.EscapeDataString(modelId) + "&autoload=false";

        var metricsTask = ReadMetricsAsync(new Uri(baseUri, "metrics" + query), cancellationToken);
        var slotsTask = ReadSlotsAsync(new Uri(baseUri, "slots" + query), cancellationToken);
        await Task.WhenAll(metricsTask, slotsTask).ConfigureAwait(false);

        var metricsResult = await metricsTask.ConfigureAwait(false);
        var slotsResult = await slotsTask.ConfigureAwait(false);
        var metrics = metricsResult.Values;
        var slots = slotsResult.Value;
        var promptTokens = ReadLongMetric(metrics, "prompt_tokens_total");
        var predictedTokens = ReadLongMetric(metrics, "tokens_predicted_total");
        var promptGauge = ReadMetric(metrics, "prompt_tokens_seconds");
        var promptRate = promptGauge is > 0
            ? promptGauge
            : Rate(promptTokens, ReadMetric(metrics, "prompt_seconds_total"));
        var requestsProcessing = ReadIntMetric(metrics, "requests_processing");
        var predictedRate = UpdatePredictedRate(
            MakeSampleTarget(baseUri, modelId),
            slots,
            predictedTokens,
            requestsProcessing,
            ReadMetric(metrics, "predicted_tokens_seconds"));

        var diagnostic = slots is null && metrics.Count == 0
            ? string.Join(" ", new[] { slotsResult.Diagnostic, metricsResult.Diagnostic }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : null;

        return new LlamaTelemetrySnapshot(
            slots?.Count ?? 0,
            slots?.Processing ?? requestsProcessing ?? 0,
            slots?.Capacity,
            slots?.Used,
            ReadMetric(metrics, "kv_cache_usage_ratio"),
            promptTokens,
            predictedTokens,
            promptRate,
            predictedRate,
            requestsProcessing,
            ReadIntMetric(metrics, "requests_deferred"),
            diagnostic);
    }

    public void ResetSamples()
    {
        lock (_sampleGate)
        {
            ResetSamplesCore();
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
        var parsed = ParseSlotData(root);
        return parsed is null
            ? (0, 0, null, null)
            : (parsed.Count, parsed.Processing, parsed.Capacity, parsed.Used);
    }

    private static ParsedSlots? ParseSlotData(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var samples = new List<SlotSample>();
        var candidates = new List<(bool Processing, int? Capacity, int? Used)>();
        var processing = 0;
        var fallbackId = 0;
        foreach (var slot in root.EnumerateArray().Take(128))
        {
            var isProcessing = slot.TryGetProperty("is_processing", out var processingNode)
                && processingNode.ValueKind == JsonValueKind.True;
            if (isProcessing)
            {
                processing++;
            }

            var id = TryReadInt(slot, "id", out var nativeId) ? nativeId : fallbackId;
            var taskId = TryReadLong(slot, "id_task", out var nativeTaskId) ? nativeTaskId : null;
            int? capacity = TryReadInt(slot, "n_ctx", out var nctx) && nctx >= 0 ? nctx : null;
            int? used = TryReadNonNegativeInt(slot, "n_prompt_tokens")
                ?? TryReadNonNegativeInt(slot, "n_past")
                ?? TryReadNonNegativeInt(slot, "n_tokens");
            var decoded = ReadDecodedTokens(slot);
            samples.Add(new SlotSample(id, taskId, isProcessing, decoded));
            candidates.Add((isProcessing, capacity, used));
            fallbackId++;
        }

        // Slots are independent conversations. Prefer the active slot, then the fullest
        // retained slot; summing their contexts would misrepresent per-slot capacity.
        var selected = candidates
            .OrderByDescending(candidate => candidate.Processing)
            .ThenByDescending(candidate => candidate.Used ?? -1)
            .FirstOrDefault();
        return new ParsedSlots(samples.Count, processing, selected.Capacity, selected.Used, samples);
    }

    private double? UpdatePredictedRate(
        string target,
        ParsedSlots? slots,
        long? predictedTokens,
        int? requestsProcessing,
        double? reportedGauge)
    {
        var timestamp = Stopwatch.GetTimestamp();
        lock (_sampleGate)
        {
            if (!string.Equals(_sampleTarget, target, StringComparison.Ordinal))
            {
                ResetSamplesCore();
                _sampleTarget = target;
            }

            var elapsedSeconds = _previousSampleTimestamp == 0
                ? 0
                : Stopwatch.GetElapsedTime(_previousSampleTimestamp, timestamp).TotalSeconds;
            var isProcessing = slots?.Processing > 0 || requestsProcessing > 0;
            double? observedRate = null;

            if (isProcessing && elapsedSeconds is > 0.05 and < 30)
            {
                long decodedDelta = 0;
                var matchedActiveSlot = false;
                if (slots is not null)
                {
                    foreach (var current in slots.Samples.Where(sample => sample.Processing && sample.Decoded is not null))
                    {
                        if (!_previousSlots.TryGetValue(current.Id, out var previous)
                            || previous.Decoded is null
                            || current.Decoded < previous.Decoded
                            || (current.TaskId is not null && previous.TaskId is not null && current.TaskId != previous.TaskId))
                        {
                            continue;
                        }

                        decodedDelta += current.Decoded.GetValueOrDefault() - previous.Decoded.GetValueOrDefault();
                        matchedActiveSlot = true;
                    }
                }

                if (matchedActiveSlot)
                {
                    observedRate = decodedDelta / elapsedSeconds;
                }
                else if (predictedTokens is not null
                         && _previousPredictedTokens is not null
                         && predictedTokens >= _previousPredictedTokens)
                {
                    observedRate = (predictedTokens.Value - _previousPredictedTokens.Value) / elapsedSeconds;
                }
                else if (reportedGauge is > 0)
                {
                    observedRate = reportedGauge;
                }
            }

            _previousSlots.Clear();
            if (slots is not null)
            {
                foreach (var sample in slots.Samples)
                {
                    _previousSlots[sample.Id] = new SlotCounter(sample.TaskId, sample.Decoded);
                }
            }

            _previousPredictedTokens = predictedTokens;
            _previousSampleTimestamp = timestamp;
            if (!isProcessing)
            {
                _smoothedPredictedRate = null;
                return null;
            }

            if (observedRate is null || !double.IsFinite(observedRate.Value) || observedRate < 0)
            {
                return _smoothedPredictedRate;
            }

            // A short exponential window removes 0/1-token sampling jitter while remaining
            // responsive enough for the sidebar's half-second active refresh cadence.
            _smoothedPredictedRate = _smoothedPredictedRate is null
                ? observedRate
                : (_smoothedPredictedRate.Value * 0.55) + (observedRate.Value * 0.45);
            return _smoothedPredictedRate;
        }
    }

    private static int? ReadDecodedTokens(JsonElement slot)
    {
        var direct = TryReadNonNegativeInt(slot, "n_decoded");
        if (direct is not null)
        {
            return direct;
        }

        if (!slot.TryGetProperty("next_token", out var nextToken))
        {
            return null;
        }

        if (nextToken.ValueKind == JsonValueKind.Object)
        {
            return TryReadNonNegativeInt(nextToken, "n_decoded");
        }

        if (nextToken.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nextToken.EnumerateArray())
            {
                var decoded = TryReadNonNegativeInt(item, "n_decoded");
                if (decoded is not null)
                {
                    return decoded;
                }
            }
        }

        return null;
    }

    private static int? TryReadNonNegativeInt(JsonElement element, string name) =>
        TryReadInt(element, name, out var value) && value >= 0 ? value : null;

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var node)
            && node.TryGetInt32(out value);
    }

    private static bool TryReadLong(JsonElement element, string name, out long? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var node)
            || !node.TryGetInt64(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private async Task<MetricsReadResult> ReadMetricsAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new MetricsReadResult(
                    new Dictionary<string, double>(StringComparer.Ordinal),
                    $"llama /metrics 不可用（{(int)response.StatusCode}）。");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new MetricsReadResult(ParseMetrics(text), null);
        }
        catch (HttpRequestException exception)
        {
            return new MetricsReadResult(new Dictionary<string, double>(StringComparer.Ordinal), exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MetricsReadResult(
                new Dictionary<string, double>(StringComparer.Ordinal),
                "llama /metrics 读取超时。");
        }
    }

    private async Task<SlotsReadResult> ReadSlotsAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SlotsReadResult(null, $"llama /slots 不可用（{(int)response.StatusCode}）。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var value = ParseSlotData(document.RootElement);
            return value is null
                ? new SlotsReadResult(null, "llama /slots 返回了无法识别的数据。")
                : new SlotsReadResult(value, null);
        }
        catch (HttpRequestException exception)
        {
            return new SlotsReadResult(null, exception.Message);
        }
        catch (JsonException)
        {
            return new SlotsReadResult(null, "llama /slots 返回了无法识别的数据。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SlotsReadResult(null, "llama /slots 读取超时。");
        }
    }

    private void ResetSamplesCore()
    {
        _sampleTarget = null;
        _previousSampleTimestamp = 0;
        _previousPredictedTokens = null;
        _smoothedPredictedRate = null;
        _previousSlots.Clear();
    }

    private static string MakeSampleTarget(Uri baseUri, string modelId) =>
        $"{baseUri.AbsoluteUri}|{modelId}";

    private static double? ReadMetric(IReadOnlyDictionary<string, double> metrics, string suffix) =>
        metrics.FirstOrDefault(pair => pair.Key.EndsWith(suffix, StringComparison.Ordinal)).Value is var value
        && metrics.Keys.Any(key => key.EndsWith(suffix, StringComparison.Ordinal)) ? value : null;

    private static long? ReadLongMetric(IReadOnlyDictionary<string, double> metrics, string suffix) =>
        ReadMetric(metrics, suffix) is { } value ? (long)Math.Round(value) : null;

    private static int? ReadIntMetric(IReadOnlyDictionary<string, double> metrics, string suffix) =>
        ReadMetric(metrics, suffix) is { } value ? (int)Math.Round(value) : null;

    private static double? Rate(long? tokens, double? seconds) =>
        tokens is > 0 && seconds is > 0 ? tokens.Value / seconds.Value : null;

    private sealed record ParsedSlots(
        int Count,
        int Processing,
        int? Capacity,
        int? Used,
        IReadOnlyList<SlotSample> Samples);

    private sealed record SlotSample(int Id, long? TaskId, bool Processing, int? Decoded);

    private sealed record SlotCounter(long? TaskId, int? Decoded);

    private sealed record MetricsReadResult(
        IReadOnlyDictionary<string, double> Values,
        string? Diagnostic);

    private sealed record SlotsReadResult(ParsedSlots? Value, string? Diagnostic);

    [GeneratedRegex(@"^(?<name>[A-Za-z_:][A-Za-z0-9_:]*)(?:\{[^\r\n]*\})?\s+(?<value>[-+0-9.eE]+)(?:\s+\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex MetricRegex();
}
