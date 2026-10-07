using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Runtime.Transport;

/// <summary>Applies an explicit model-owned native thinking switch; never maps effort levels.</summary>
public static class ThinkingRequestPolicy
{
    public static byte[] Apply(byte[] body, bool enabled)
    {
        if (JsonNode.Parse(body) is not JsonObject request) return body;
        if (request["chat_template_kwargs"] is not null && request["chat_template_kwargs"] is not JsonObject)
            throw new JsonException("chat_template_kwargs must be an object.");
        var kwargs = request["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        kwargs["enable_thinking"] = enabled;
        if (kwargs.Parent is null) request["chat_template_kwargs"] = kwargs;
        // llama.cpp treats 'none' as a separate switch and gives it precedence over kwargs.
        // An explicitly enabled model switch must not be defeated by a client placeholder.
        // Positive effort values are always forwarded byte-for-value, with no aliases/mapping.
        if (enabled)
        {
            if (request["reasoning"] is JsonObject reasoning && reasoning["effort"]?.ToString() == "none") reasoning.Remove("effort");
            if (request["reasoning_effort"]?.ToString() == "none") request.Remove("reasoning_effort");
        }
        return JsonSerializer.SerializeToUtf8Bytes(request);
    }
}
