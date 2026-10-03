using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Launcher.Runtime.Router;

public sealed record ChatTemplateRenderResult(bool Passed, bool Unavailable, string Details);

/// <summary>Exercises native rendering only. This does not certify inference or Codex tool execution.</summary>
public sealed class LlamaChatTemplateValidationClient(HttpClient httpClient)
{
    public async Task<ChatTemplateRenderResult> ValidateAsync(Uri baseUri, string modelId, CancellationToken cancellationToken,
        IReadOnlyList<string>? toolMarkers = null)
    {
        LlamaModelManagementClient.ValidateBaseUri(baseUri);
        var conversations = new[]
        {
            new[] { Message("system", "SYS_A"), Message("user", "USR_A") },
            new[] { Message("developer", "DEV_A"), Message("user", "USR_A") },
            new[] { Message("system", "SYS_A"), Message("developer", "DEV_A"), Message("system", "SYS_B"), Message("user", "USR_A") },
            new[] { Message("system", "SYS_A"), Message("user", "USR_A"), Message("assistant", "AST_A"), Message("system", "SYS_B"), Message("developer", "DEV_A"), Message("user", "USR_B") },
            new[] { Message("user", "USR_A"), Message("assistant", "AST_A"), Message("user", "USR_B") },
        };
        foreach (var messages in conversations)
        {
            var result = await CheckAsync(baseUri, new { model = modelId, messages },
                messages.Select(message => message.content).ToArray(), cancellationToken);
            if (!result.Passed) return result;
        }
        object[] toolMessages =
        [
            Message("system", "SYS_A"), Message("user", "USR_A"),
            new { role = "assistant", content = "", tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "echo", arguments = new { text = "ARG_A" } } } } },
            new { role = "tool", content = "RESULT_A", tool_call_id = "call_1" },
            Message("user", "USR_B"),
        ];
        return await CheckAsync(baseUri, new
        {
            model = modelId,
            messages = toolMessages,
            tools = new[] { new { type = "function", function = new { name = "echo", description = "Echo text", parameters = new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" } } } } },
        }, ["SYS_A", "USR_A", "ARG_A", "RESULT_A", "USR_B"], cancellationToken, toolMarkers);
    }

    private static ChatMessage Message(string role, string content) => new(role, content);
    private sealed record ChatMessage(string role, string content);

    private async Task<ChatTemplateRenderResult> CheckAsync(Uri baseUri, object request, string[] sentinels,
        CancellationToken cancellationToken, IReadOnlyList<string>? toolMarkers = null)
    {
        using var response = await httpClient.PostAsJsonAsync(new Uri(baseUri, "apply-template"), request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            return new(false, true, "llama.cpp does not provide /apply-template for this model.");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var templateError = body.Contains("Jinja", StringComparison.OrdinalIgnoreCase)
                && (body.Contains("exception", StringComparison.OrdinalIgnoreCase) || body.Contains("error", StringComparison.OrdinalIgnoreCase));
            return new(false, !templateError, $"Native template check returned HTTP {(int)response.StatusCode}."
                + (templateError ? " Jinja rendering failed." : " Rendering could not be verified."));
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("prompt", out var promptNode) || promptNode.ValueKind != JsonValueKind.String)
            return new(false, true, "llama.cpp did not return a rendered prompt.");
        var prompt = promptNode.GetString()!;
        var previous = -1;
        foreach (var sentinel in sentinels)
        {
            var index = prompt.IndexOf(sentinel, StringComparison.Ordinal);
            if (index <= previous || prompt.IndexOf(sentinel, index + sentinel.Length, StringComparison.Ordinal) >= 0)
                return new(false, false, "Rendered prompt omitted, duplicated or reordered a message.");
            previous = index;
        }
        if (toolMarkers is not null && toolMarkers.Any(marker => !prompt.Contains(marker, StringComparison.Ordinal)))
            return new(false, false, "Rendered prompt did not preserve the expected tool markers.");
        return new(true, false, "Native rendering checks passed; inference is not verified.");
    }
}
