using System.Net;
using System.Text;
using System.Text.Json;
using Launcher.Runtime.Router;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ChatTemplateRulesTests
{
    [Theory]
    [InlineData("qwen38-27b.jinja", "qwen-strict-system")]
    [InlineData("unsloth-qwen38-27b.jinja", "unsloth-merged-system")]
    public void RecognizesActualTemplatesAndPreservesToolAndReasoningSections(string file, string rule)
    {
        var original = Fixture(file);
        var analysis = ChatTemplateRules.Analyze(original);
        Assert.Equal(ChatTemplateMatchKind.Repairable, analysis.Kind);
        Assert.Equal(rule, analysis.RuleId);
        Assert.DoesNotContain("raise_exception('System message must be at the beginning.')", analysis.RepairedTemplate);
        var originalToolStart = original.IndexOf("{%- elif message.role == \"assistant\" %}", StringComparison.Ordinal);
        Assert.EndsWith(original[originalToolStart..], analysis.RepairedTemplate!, StringComparison.Ordinal);
        Assert.Contains("message.role == 'developer'", analysis.RepairedTemplate);
        Assert.Contains("'<|im_start|>system\\n' + content", analysis.RepairedTemplate);
    }

    [Theory]
    [InlineData("qwen38-27b.jinja")]
    [InlineData("unsloth-qwen38-27b.jinja")]
    public void AcceptsCrLfAndRejectsRepeatedGuards(string file)
    {
        var template = Fixture(file);
        Assert.Equal(ChatTemplateMatchKind.Repairable, ChatTemplateRules.Analyze(template.Replace("\n", "\r\n")).Kind);
        Assert.Equal(ChatTemplateMatchKind.Ambiguous, ChatTemplateRules.Analyze(template + template).Kind);
    }

    [Fact]
    public void MatchingOnlyTheErrorTextDoesNotSelectARule() =>
        Assert.Equal(ChatTemplateMatchKind.NotMatched, ChatTemplateRules.Analyze("System message must be at the beginning.").Kind);

    [Fact]
    public void UnslothRuleRequiresItsMergeAndLoopBoundary()
    {
        var template = Fixture("unsloth-qwen38-27b.jinja").Replace("if loop.index0 >= num_sys", "if true");
        Assert.Equal(ChatTemplateMatchKind.Ambiguous, ChatTemplateRules.Analyze(template).Kind);
    }

    [Fact]
    public void OwnedOutputDetectsManualEdits()
    {
        var template = Fixture("unsloth-qwen38-27b.jinja");
        var output = CodexChatTemplateCompatibility.CreateOwnedOutput(template, ChatTemplateRules.Analyze(template).RepairedTemplate!);
        Assert.True(CodexChatTemplateCompatibility.IsUnmodifiedOwnedOutput(output));
        Assert.False(CodexChatTemplateCompatibility.IsUnmodifiedOwnedOutput(output + "{# edited #}"));
        Assert.False(CodexChatTemplateCompatibility.IsUnmodifiedOwnedOutput(CodexChatTemplateCompatibility.OwnershipMarker + "\nuser text"));
    }

    [Fact]
    public async Task NativeJinjaFailureIsDistinguishedFromServiceFailure()
    {
        using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.InternalServerError)
        { Content = new StringContent("Jinja Exception: System message must be at the beginning.") }));
        var result = await new LlamaChatTemplateValidationClient(http).ValidateAsync(new Uri("http://127.0.0.1:1234/"), "model", CancellationToken.None);
        Assert.False(result.Passed);
        Assert.False(result.Unavailable);
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(503, true)]
    [InlineData(500, true)]
    public async Task NativeFailureIsNotMarkedAsPassed(int statusCode, bool unavailable)
    {
        using var http = new HttpClient(new ResponseHandler(_ => new((HttpStatusCode)statusCode)));
        var result = await new LlamaChatTemplateValidationClient(http).ValidateAsync(new Uri("http://127.0.0.1:1234/"), "model", CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Equal(unavailable, result.Unavailable);
    }

    [Fact]
    public async Task NativeCheckRejectsMissingOrReorderedMessages()
    {
        using var http = new HttpClient(new ResponseHandler(_ => JsonResponse("USR_A SYS_A")));
        var result = await new LlamaChatTemplateValidationClient(http).ValidateAsync(new Uri("http://127.0.0.1:1234/"), "model", CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains("reordered", result.Details);
    }

    [Fact]
    public async Task NativeCheckCoversAllSixCasesIncludingToolArguments()
    {
        var calls = 0;
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            calls++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            var parts = new List<string>();
            foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
            {
                parts.Add(message.GetProperty("content").GetString()!);
                if (message.TryGetProperty("tool_calls", out var tools))
                    parts.Add(tools[0].GetProperty("function").GetProperty("arguments").GetProperty("text").GetString()!);
            }
            return JsonResponse(string.Join(" ", parts) + " <tool_call> <tool_response>");
        }));
        var result = await new LlamaChatTemplateValidationClient(http).ValidateAsync(new Uri("http://127.0.0.1:1234/"), "model", CancellationToken.None,
            ["<tool_call>", "<tool_response>"]);
        Assert.True(result.Passed);
        Assert.Equal(6, calls);
    }

    private static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChatTemplates", file));
    private static HttpResponseMessage JsonResponse(string prompt) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { prompt }), Encoding.UTF8, "application/json") };
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
