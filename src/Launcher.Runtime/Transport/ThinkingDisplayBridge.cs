using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Runtime.Transport;

/// <summary>Mirrors native reasoning into the summary channel rendered by Desktop.
/// Original reasoning remains intact; identical display copies are removed from subsequent input.</summary>
public sealed class ThinkingDisplayBridge : IDisposable
{
    private const int MaximumFrameBytes = 4 * 1024 * 1024;
    private readonly MemoryStream _frame = new();
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nativeSummary = new(StringComparer.Ordinal);
    private bool _passthrough;
    private byte _previous;
    private byte _beforePrevious;

    public byte[] Observe(ReadOnlySpan<byte> bytes)
    {
        using var output = new MemoryStream();
        foreach (var value in bytes)
        {
            if (_passthrough) output.WriteByte(value);
            else _frame.WriteByte(value);
            var end = value == '\n' && (_previous == '\n' || (_previous == '\r' && _beforePrevious == '\n'));
            _beforePrevious = _previous;
            _previous = value;
            if (end)
            {
                if (!_passthrough) output.Write(Transform(_frame.ToArray()));
                _frame.SetLength(0);
                _passthrough = false;
                _previous = _beforePrevious = 0;
            }
            else if (_frame.Length >= MaximumFrameBytes)
            {
                _frame.WriteTo(output);
                _frame.SetLength(0);
                _passthrough = true;
            }
        }
        return output.ToArray();
    }

    public byte[] Complete() => _frame.ToArray();
    public void Dispose() => _frame.Dispose();

    private byte[] Transform(byte[] original)
    {
        var frame = Encoding.UTF8.GetString(original);
        var data = string.Join("\n", frame.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].TrimStart(' ').TrimEnd('\r')));
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return original;
        try
        {
            if (JsonNode.Parse(data) is not JsonObject message) return original;
            var type = message["type"]?.ToString();
            if (type?.StartsWith("response.reasoning_summary", StringComparison.Ordinal) == true)
            {
                if (_nativeSummary.Count < 1024 && message["item_id"] is { } nativeId) _nativeSummary.Add(nativeId.ToString());
                return original;
            }
            if (type == "response.reasoning_text.delta" && message["item_id"] is { } idNode && message["delta"] is { } delta)
            {
                var id = idNode.ToString();
                if (_nativeSummary.Contains(id)) return original;
                var prefix = "";
                if (_started.Count < 1024 && _started.Add(id))
                    prefix = Event("response.reasoning_summary_part.added", new JsonObject
                    {
                        ["item_id"] = id,
                        ["output_index"] = message["output_index"]?.DeepClone() ?? JsonValue.Create(0),
                        ["summary_index"] = 0,
                        ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" }
                    });
                var copy = new JsonObject
                {
                    ["item_id"] = id,
                    ["output_index"] = message["output_index"]?.DeepClone() ?? JsonValue.Create(0),
                    ["summary_index"] = 0,
                    ["delta"] = delta.DeepClone()
                };
                return Encoding.UTF8.GetBytes(frame + prefix + Event("response.reasoning_summary_text.delta", copy));
            }
            if (type == "response.output_item.done" && message["item"] is JsonObject item && Mirror(item))
                return Encoding.UTF8.GetBytes(Event(type, message));
            if (type == "response.completed" && message["response"]?["output"] is JsonArray items)
            {
                var changed = false;
                foreach (var output in items.OfType<JsonObject>()) changed |= Mirror(output);
                if (changed) return Encoding.UTF8.GetBytes(Event(type, message));
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { /* Unrecognized upstream events are forwarded unchanged. */ }
        return original;
    }

    private static bool Mirror(JsonObject item)
    {
        if (item["type"]?.ToString() != "reasoning" || item["summary"] is JsonArray { Count: > 0 }) return false;
        var text = RawText(item);
        if (text.Length == 0) return false;
        item["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = text });
        return true;
    }

    private static string RawText(JsonObject item) => item["content"] is JsonArray content
        ? string.Concat(content.OfType<JsonObject>().Where(part => part["type"]?.ToString() == "reasoning_text").Select(part => part["text"]?.ToString())) : "";

    private static string Event(string type, JsonObject message)
    {
        message["type"] = type;
        return $"event: {type}\ndata: {message.ToJsonString()}\n\n";
    }

    public static byte[] RemoveDisplayCopies(byte[] body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject request || request["input"] is not JsonArray input) return body;
            var changed = false;
            foreach (var item in input.OfType<JsonObject>())
            {
                if (item["type"]?.ToString() != "reasoning" || item["summary"] is not JsonArray { Count: > 0 } summary) continue;
                var raw = RawText(item);
                if (raw.Length == 0 || summary.OfType<JsonObject>().Any(part => part["type"]?.ToString() != "summary_text")
                    || string.Concat(summary.OfType<JsonObject>().Select(part => part["text"]?.ToString())) != raw) continue;
                item["summary"] = new JsonArray();
                changed = true;
            }
            return changed ? JsonSerializer.SerializeToUtf8Bytes(request) : body;
        }
        catch (JsonException) { return body; }
    }
}
