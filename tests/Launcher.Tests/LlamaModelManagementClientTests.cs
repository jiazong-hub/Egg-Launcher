using System.Net;
using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class LlamaModelManagementClientTests
{
    [Fact]
    public async Task ListAsync_ParsesRouterStateAndMetadataWithoutStatusArguments()
    {
        using var http = new HttpClient(new StubHandler(_ => JsonResponse("""
            {"object":"list","data":[{
              "id":"org/model:Q4_K_M","path":"D:/llama/models/model.gguf",
              "in_cache":true,"can_remove":true,"source":"cache",
              "status":{"value":"loaded","args":["secret-ignored"]},
              "meta":{"size":123,"n_params":456,"n_ctx_train":32768},
              "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}
            }]}
            """)));

        var models = await new LlamaModelManagementClient(http).ListAsync(new Uri("http://127.0.0.1:8123/"));

        var model = Assert.Single(models);
        Assert.Equal("org/model:Q4_K_M", model.Id);
        Assert.True(model.CanRemove);
        Assert.Equal("loaded", model.Status);
        Assert.Equal(123, model.SizeBytes);
        Assert.Equal(456, model.ParameterCount);
        Assert.Equal(32768, model.TrainingContextSize);
        Assert.Equal(["text", "image"], model.InputModalities);
    }

    [Fact]
    public async Task RemoveCachedAsync_UsesDeleteAndEscapedQuery()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("{\"success\":true}");
        }));

        var result = await new LlamaModelManagementClient(http).RemoveCachedAsync(
            new Uri("http://127.0.0.1:8123/"),
            "org/model:Q4_K_M");

        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Delete, captured!.Method);
        Assert.Equal("/models?model=org%2Fmodel%3AQ4_K_M", captured.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ListAsync_RejectsNonLoopbackEndpoint()
    {
        using var http = new HttpClient(new StubHandler(_ => JsonResponse("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LlamaModelManagementClient(http).ListAsync(new Uri("http://example.com/")));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
