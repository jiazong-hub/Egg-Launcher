using System.Net;
using System.Text;
using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class LlamaReasoningCapabilityClientTests
{
    [Fact]
    public async Task ProbeAsync_WhenNativeSupportIsFalse_ReturnsUnsupported()
    {
        using var http = CreateHttp("""{"chat_template_caps":{"supports_reasoning_effort":false}}""");

        var result = await new LlamaReasoningCapabilityClient(http).ProbeAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local/coder");

        Assert.Equal(LlamaReasoningCapabilityStatus.Unsupported, result.Status);
        Assert.Empty(result.SupportedLevels);
    }

    [Fact]
    public async Task ProbeAsync_WhenSupportIsTrueButLevelsAreMissing_DoesNotGuessLevels()
    {
        using var http = CreateHttp("""{"chat_template_caps":{"supports_reasoning_effort":true}}""");

        var result = await new LlamaReasoningCapabilityClient(http).ProbeAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local/coder");

        Assert.Equal(LlamaReasoningCapabilityStatus.SupportedLevelsUnknown, result.Status);
        Assert.Empty(result.SupportedLevels);
    }

    [Fact]
    public async Task ProbeAsync_WhenExactRecognizedLevelsAreReported_ReturnsVerifiedLevels()
    {
        using var http = CreateHttp(
            """{"chat_template_caps":{"supports_reasoning_effort":true,"supported_reasoning_levels":["low","medium","high"],"default_reasoning_level":"medium"}}""");

        var result = await new LlamaReasoningCapabilityClient(http).ProbeAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local/coder");

        Assert.Equal(LlamaReasoningCapabilityStatus.Verified, result.Status);
        Assert.Equal(["low", "medium", "high"], result.SupportedLevels);
        Assert.Equal("medium", result.DefaultLevel);
    }

    [Fact]
    public async Task ProbeAsync_WhenAnyReportedLevelIsUnknown_DoesNotPublishPartialList()
    {
        using var http = CreateHttp(
            """{"chat_template_caps":{"supports_reasoning_effort":true,"supported_reasoning_levels":["low","vendor-ultra"]}}""");

        var result = await new LlamaReasoningCapabilityClient(http).ProbeAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local/coder");

        Assert.Equal(LlamaReasoningCapabilityStatus.SupportedLevelsUnknown, result.Status);
        Assert.Empty(result.SupportedLevels);
    }

    private static HttpClient CreateHttp(string json) => new(new StubHandler(request =>
    {
        Assert.Equal("/props", request.RequestUri?.AbsolutePath);
        Assert.Equal("?model=local%2Fcoder", request.RequestUri?.Query);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
