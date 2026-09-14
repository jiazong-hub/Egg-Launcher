using System.Text.Json;

namespace Launcher.ChatGPT.Processes;

public static class CodexCliJsonTranscript
{
    public static string? FindThreadId(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        using var reader = new StringReader(jsonLines);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var eventType)
                    && eventType.GetString() == "thread.started"
                    && root.TryGetProperty("thread_id", out var threadId)
                    && threadId.ValueKind == JsonValueKind.String
                    && Guid.TryParse(threadId.GetString(), out var parsed))
                {
                    return parsed.ToString();
                }
            }
            catch (JsonException)
            {
                // Tolerate incidental non-JSON stdout diagnostics.
            }
        }

        return null;
    }

    public static int CountSuccessfulCommandExecutions(string jsonLines)
    {
        return Analyze(jsonLines).SuccessfulCommandExecutions;
    }

    public static CodexCliTranscriptSummary Analyze(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);
        var attempts = 0;
        var succeeded = 0;
        var policyBlocked = 0;
        using var reader = new StringReader(jsonLines);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var eventType)
                    && eventType.GetString() == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType)
                    && itemType.GetString() == "command_execution")
                {
                    attempts++;
                    if (item.TryGetProperty("exit_code", out var exitCode)
                        && exitCode.ValueKind == JsonValueKind.Number
                        && exitCode.GetInt32() == 0)
                    {
                        succeeded++;
                    }

                    if (ContainsPolicyBlock(item))
                    {
                        policyBlocked++;
                    }
                }
            }
            catch (JsonException)
            {
                // stderr is kept separately; tolerate incidental non-JSON stdout diagnostics.
            }
        }

        return new CodexCliTranscriptSummary(attempts, succeeded, policyBlocked);
    }

    private static bool ContainsPolicyBlock(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                return value?.Contains("blocked by policy", StringComparison.OrdinalIgnoreCase) == true
                    || value?.Contains("rejected by policy", StringComparison.OrdinalIgnoreCase) == true
                    || value?.Contains("denied by policy", StringComparison.OrdinalIgnoreCase) == true;
            case JsonValueKind.Object:
                return element.EnumerateObject().Any(property => ContainsPolicyBlock(property.Value));
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(ContainsPolicyBlock);
            default:
                return false;
        }
    }
}
