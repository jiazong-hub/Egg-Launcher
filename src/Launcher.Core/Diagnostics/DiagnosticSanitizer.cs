using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Launcher.Core.Diagnostics;

/// <summary>
/// Applies a common privacy and size boundary to Launcher-owned diagnostic data.
/// llama.cpp's native output is retained separately because it is often needed to
/// diagnose backend failures; this sanitizer is for structured Launcher events.
/// </summary>
public static partial class DiagnosticSanitizer
{
    private const int MaximumTextCharacters = 4_000;
    private const int MaximumExceptionChainLength = 8;

    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "apiKey", "token", "accessToken", "refreshToken", "cookie", "set-cookie",
        "password", "secret", "prompt", "input", "content", "text", "body", "requestBody",
        "responseBody", "summary", "messages", "headers",
    };

    public static string SanitizeText(string? value, int maximumCharacters = MaximumTextCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        sanitized = BearerTokenRegex().Replace(sanitized, "Bearer <redacted>");
        sanitized = SecretAssignmentRegex().Replace(sanitized, "$1=<redacted>");
        sanitized = UserProfileRegex().Replace(sanitized, "<user-profile>");
        sanitized = LongOpaqueValueRegex().Replace(sanitized, "<redacted-value>");
        sanitized = sanitized.Trim();

        if (maximumCharacters < 1)
        {
            return string.Empty;
        }

        return sanitized.Length <= maximumCharacters
            ? sanitized
            : sanitized[..maximumCharacters] + "…<truncated>";
    }

    public static IReadOnlyDictionary<string, object?> SanitizeProperties(
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return new Dictionary<string, object?>();
        }

        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in properties.Take(100))
        {
            sanitized[SanitizeText(key, 100)] = SanitizeValue(key, value, depth: 0);
        }

        return sanitized;
    }

    public static JsonNode? SanitizeJsonNode(JsonNode? node)
    {
        var safeValue = SanitizeNode(node, depth: 0);
        return JsonSerializer.SerializeToNode(safeValue);
    }

    public static Dictionary<string, object?> CreateExceptionProperties(
        Exception exception,
        bool includeStackTrace)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var chain = new List<Dictionary<string, object?>>();
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        while (pending.Count > 0 && chain.Count < MaximumExceptionChainLength)
        {
            var current = pending.Dequeue();
            chain.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["exceptionType"] = current.GetType().FullName ?? current.GetType().Name,
                ["hResult"] = $"0x{current.HResult:X8}",
                ["message"] = SanitizeText(current.Message),
                ["stackTrace"] = includeStackTrace ? SanitizeText(current.StackTrace, 12_000) : null,
            });

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["hResult"] = $"0x{exception.HResult:X8}",
            ["message"] = SanitizeText(exception.Message),
            ["exceptionChain"] = chain,
            ["stackTrace"] = includeStackTrace ? SanitizeText(exception.StackTrace, 12_000) : null,
            ["innerExceptionCount"] = Math.Max(0, chain.Count - 1),
        };
    }

    private static object? SanitizeValue(string propertyName, object? value, int depth)
    {
        if (SensitivePropertyNames.Contains(propertyName))
        {
            return value is null ? null : "<redacted>";
        }

        if (value is null || depth >= 4)
        {
            return depth >= 4 ? "<nested-value-omitted>" : null;
        }

        if (value is string text)
        {
            return SanitizeText(text, string.Equals(propertyName, "stackTrace", StringComparison.OrdinalIgnoreCase)
                ? 12_000
                : 2_000);
        }

        try
        {
            var node = JsonSerializer.SerializeToNode(value);
            return SanitizeNode(node, depth);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            return "<unsupported-value>";
        }
    }

    private static object? SanitizeNode(JsonNode? node, int depth, string? propertyName = null)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return SanitizeText(text, string.Equals(propertyName, "stackTrace", StringComparison.OrdinalIgnoreCase)
                    ? 12_000
                    : 2_000);
            }

            return value.DeepClone();
        }

        if (depth >= 4)
        {
            return "<nested-value-omitted>";
        }

        if (node is JsonObject jsonObject)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in jsonObject.Take(100))
            {
                result[property.Key] = SensitivePropertyNames.Contains(property.Key)
                    ? property.Value is null ? null : "<redacted>"
                    : SanitizeNode(property.Value, depth + 1, property.Key);
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            return jsonArray.Take(100).Select(item => SanitizeNode(item, depth + 1)).ToArray();
        }

        return null;
    }

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s,;""']+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)\b(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|token|cookie|set-cookie|password|secret|prompt|input|content|text|body|summary)\s*[:=]\s*[^,;\r\n]+")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\Users\\[^\\\s]+")]
    private static partial Regex UserProfileRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9_+/=-]{96,}\b")]
    private static partial Regex LongOpaqueValueRegex();
}
