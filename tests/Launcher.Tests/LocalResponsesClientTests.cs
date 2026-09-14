using System.Net;
using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class LocalResponsesClientTests
{
    [Fact]
    public async Task CreateAsync_ExtractsOutputTextFromResponsesEnvelope()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("http://127.0.0.1:8080/v1/responses", request.RequestUri?.AbsoluteUri);
            var requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.DoesNotContain("reasoning", requestJson, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"resp_1\",\"model\":\"local-coder\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"LOCAL_OK\"}]}]}"),
            };
        }));
        var client = new LocalResponsesClient(httpClient);

        var result = await client.CreateAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local-coder",
            "Reply LOCAL_OK");

        Assert.True(result.Succeeded);
        Assert.Equal("resp_1", result.ResponseId);
        Assert.Equal("LOCAL_OK", result.OutputText);
    }

    [Fact]
    public async Task CreateAsync_RejectsNonLoopbackEndpoint()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => throw new InvalidOperationException()));
        var client = new LocalResponsesClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync(
            new Uri("http://example.com/"),
            "local-coder",
            "Reply LOCAL_OK"));
    }

    [Fact]
    public async Task CreateAsync_AllowsBoundedReasoningHeadroomButRejectsLargerOutputs()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            var requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"max_output_tokens\":1024", requestJson, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"resp_1\",\"output_text\":\"LOCAL_OK\",\"output\":[]}"),
            };
        }));
        var client = new LocalResponsesClient(httpClient);

        var result = await client.CreateAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local-coder",
            "Reply LOCAL_OK",
            maxOutputTokens: 1024);

        Assert.True(result.Succeeded);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateAsync(
            new Uri("http://127.0.0.1:8080/"),
            "local-coder",
            "Reply LOCAL_OK",
            maxOutputTokens: 1025));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
