using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Runtime.Router;

/// <summary>Checks the loaded template and the Responses wire path, without modifying either.</summary>
public sealed class LlamaReasoningNativeProbe(HttpClient http, NativePromptEvidence? evidence = null)
{
    private const string InvalidEffort = "launcher_invalid_effort_probe";
    private const string ProbeText = "Reply with OK.";

    public async Task<LlamaReasoningCapability> ProbeAsync(Uri baseUri, string model, string template,
        CancellationToken cancellationToken)
    {
        LlamaModelManagementClient.ValidateBaseUri(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var descriptor = ReasoningTemplateDescriptor.Read(template);
        var levels = descriptor.Levels;
        var details = new List<string>();
        if (levels.Count == 0)
        {
            // Native explicit lists are another source; a missing list is not a negative capability.
            var metadata = await new LlamaReasoningCapabilityClient(http).ProbeAsync(baseUri, model, cancellationToken);
            levels = metadata.SupportedLevels;
        }
        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        var on = await RenderAsync(baseUri, model, null, true, cancellationToken);
        var off = await RenderAsync(baseUri, model, null, false, cancellationToken);
        bool? supportsThinking = null;
        var thinkingResponses = false;
        if (on is not null && off is not null && on != off)
        {
            thinkingResponses = await ResponseMatchesAsync(baseUri, model, null, true, on, cancellationToken)
                && await ResponseMatchesAsync(baseUri, model, null, false, off, cancellationToken);
            if (thinkingResponses) supportsThinking = true;
            details.Add(thinkingResponses ? "思考开关：原生渲染与 Responses 实际提示词校验通过。" : "思考开关：渲染不同，但 Responses 实际提示词尚未确认。");
        }
        else details.Add("思考开关：未取得可验证的开启／关闭差异。");

        var invalid = await RenderAsync(baseUri, model, InvalidEffort, true, cancellationToken);
        var rejectedInvalid = invalid is null && _lastTemplateRejected;
        var allRendered = levels.Count > 0 && rejectedInvalid;
        foreach (var level in levels)
        {
            var prompt = await RenderAsync(baseUri, model, level, true, cancellationToken);
            if (prompt is null) { allRendered = false; break; }
            rendered[level] = prompt;
        }
        // Accepted but identical prompts are not evidence of separate effort controls.
        if (rendered.Values.Distinct(StringComparer.Ordinal).Count() != levels.Count) allRendered = false;
        var responsesVerified = allRendered && await RejectsInvalidResponsesAsync(baseUri, model, cancellationToken);
        if (responsesVerified)
        {
            foreach (var level in levels)
            {
                if (!await ResponseMatchesAsync(baseUri, model, level, true, rendered[level], cancellationToken))
                { responsesVerified = false; break; }
            }
        }
        var defaultPrompt = await RenderAsync(baseUri, model, null, null, cancellationToken);
        string? defaultLevel = null;
        // Keep enable_thinking consistent when detecting the omitted effort value.
        // A template whose default is thinking-off can still have a default effort.
        if (allRendered && on is not null)
            defaultLevel = rendered.FirstOrDefault(pair => pair.Value == on).Key;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (allRendered)
            foreach (var pair in descriptor.Aliases)
                if (await RenderAsync(baseUri, model, pair.Key, true, cancellationToken) == rendered[pair.Value]) aliases[pair.Key] = pair.Value;
        details.Add(!rejectedInvalid ? "档位：运行时未明确拒绝无效档位，不能确认参数被应用。"
            : !allRendered ? "档位：未识别完整声明，或未取得独立档位的有效渲染差异。"
            : responsesVerified ? "档位：逐档渲染、无效值对照与 Responses 实际提示词校验通过。"
            : "档位：原生渲染已确认，但 Responses 传递尚未验证通过。");
        if (evidence is null) details.Add("运行时未提供隔离提示词记录，不能确认 Responses 参数生效。");
        bool? defaultThinking = null;
        if (supportsThinking == true)
            defaultThinking = defaultPrompt == on ? true : defaultPrompt == off ? false : null;
        return new(allRendered ? LlamaReasoningCapabilityStatus.Verified : LlamaReasoningCapabilityStatus.Unknown,
            allRendered ? levels.ToArray() : [], defaultLevel)
        {
            SupportsThinkingSwitch = supportsThinking,
            DefaultThinkingEnabled = defaultThinking,
            ResponsesVerified = responsesVerified,
            Aliases = aliases,
            Details = string.Join("\n", details),
        };
    }

    private bool _lastTemplateRejected;
    private async Task<string?> RenderAsync(Uri uri, string model, string? effort, bool? thinking, CancellationToken token)
    {
        var body = Body(model, effort, thinking, responses: false);
        using var response = await http.PostAsJsonAsync(new Uri(uri, "apply-template"), body, token);
        var text = await response.Content.ReadAsStringAsync(token);
        _lastTemplateRejected = !response.IsSuccessStatusCode && IsEffortError(text);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String
            ? prompt.GetString() : null;
    }

    private Task<bool> ResponseMatchesAsync(Uri uri, string model, string? effort, bool thinking, string expected, CancellationToken token)
    {
        if (evidence is null) return Task.FromResult(false);
        return evidence.MatchesAsync(expected, async () =>
        {
            using var response = await http.PostAsJsonAsync(new Uri(uri, "v1/responses"), Body(model, effort, thinking, true), token);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }, token);
    }

    private async Task<bool> RejectsInvalidResponsesAsync(Uri uri, string model, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(new Uri(uri, "v1/responses"), Body(model, InvalidEffort, true, true), token);
        return !response.IsSuccessStatusCode && IsEffortError(await response.Content.ReadAsStringAsync(token));
    }

    private static bool IsEffortError(string text) => text.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
        && (text.Contains("effort", StringComparison.OrdinalIgnoreCase))
        && (text.Contains("unexpected", StringComparison.OrdinalIgnoreCase) || text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unsupported", StringComparison.OrdinalIgnoreCase));

    private static JsonObject Body(string model, string? effort, bool? thinking, bool responses)
    {
        var body = new JsonObject { ["model"] = model };
        if (responses)
        {
            body["input"] = ProbeText; body["max_output_tokens"] = 1; body["stream"] = false; body["store"] = false;
            if (effort is not null) body["reasoning"] = new JsonObject { ["effort"] = effort };
        }
        else
        {
            body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = ProbeText });
            if (effort is not null) body["reasoning_effort"] = effort;
        }
        if (thinking.HasValue) body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = thinking.Value };
        return body;
    }
}
