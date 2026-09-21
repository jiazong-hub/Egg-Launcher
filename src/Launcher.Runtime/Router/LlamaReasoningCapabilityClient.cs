using System.Net;
using System.Text.Json;

namespace Launcher.Runtime.Router;

/// <summary>
/// Reads native llama.cpp chat-template capabilities without inferring levels that the
/// server did not explicitly report.
/// </summary>
public sealed class LlamaReasoningCapabilityClient(HttpClient httpClient)
{
    private static readonly HashSet<string> ChatGptReasoningLevels =
        new(
            ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "persistent"],
            StringComparer.Ordinal);

    public async Task<LlamaReasoningCapability> ProbeAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        LlamaModelManagementClient.ValidateBaseUri(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (modelId.Length > 512 || modelId.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("模型 ID 无效。", nameof(modelId));
        }

        using var response = await httpClient.GetAsync(
            new Uri(baseUri, "props?model=" + Uri.EscapeDataString(modelId)),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return Unknown();
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    internal static LlamaReasoningCapability Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Unknown();
        }

        var caps = root.TryGetProperty("chat_template_caps", out var capsNode)
            && capsNode.ValueKind == JsonValueKind.Object
                ? capsNode
                : default;
        var explicitlySupported = ReadOptionalBoolean(caps, "supports_reasoning_effort")
            ?? ReadOptionalBoolean(root, "supports_reasoning_effort");
        if (explicitlySupported == false)
        {
            return new LlamaReasoningCapability(
                LlamaReasoningCapabilityStatus.Unsupported,
                Array.Empty<string>(),
                null);
        }

        var rawLevels = ReadStringArray(caps, "supported_reasoning_levels")
            ?? ReadStringArray(caps, "reasoning_effort_levels")
            ?? ReadStringArray(root, "supported_reasoning_levels")
            ?? ReadStringArray(root, "reasoning_effort_levels");
        if (rawLevels is { Count: > 0 })
        {
            var normalized = rawLevels
                .Select(level => level.Trim().ToLowerInvariant())
                .ToArray();
            if (normalized.Length <= ChatGptReasoningLevels.Count
                && normalized.All(ChatGptReasoningLevels.Contains)
                && normalized.Distinct(StringComparer.Ordinal).Count() == normalized.Length)
            {
                var defaultLevel = ReadOptionalString(caps, "default_reasoning_level")
                    ?? ReadOptionalString(caps, "default_reasoning_effort")
                    ?? ReadOptionalString(root, "default_reasoning_level")
                    ?? ReadOptionalString(root, "default_reasoning_effort");
                defaultLevel = defaultLevel?.Trim().ToLowerInvariant();
                if (defaultLevel is not null && !normalized.Contains(defaultLevel, StringComparer.Ordinal))
                {
                    defaultLevel = null;
                }

                return new LlamaReasoningCapability(
                    LlamaReasoningCapabilityStatus.Verified,
                    normalized,
                    defaultLevel);
            }
        }

        return explicitlySupported == true
            ? new LlamaReasoningCapability(
                LlamaReasoningCapabilityStatus.SupportedLevelsUnknown,
                Array.Empty<string>(),
                null)
            : Unknown();
    }

    private static LlamaReasoningCapability Unknown() =>
        new(LlamaReasoningCapabilityStatus.Unknown, Array.Empty<string>(), null);

    private static bool? ReadOptionalBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string>? ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var levels = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return Array.Empty<string>();
            }

            levels.Add(item.GetString()!);
            if (levels.Count > 16)
            {
                return Array.Empty<string>();
            }
        }

        return levels;
    }
}
