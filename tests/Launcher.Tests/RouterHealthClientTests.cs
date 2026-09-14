using System.Net;
using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class RouterHealthClientTests
{
    [Fact]
    public async Task ProbeResponsesRouteAsync_TreatsModelValidationFailureAsAvailableRoute()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:8080/v1/responses", request.RequestUri?.AbsoluteUri);
            Assert.Equal("{}", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"model is required\"}}"),
            };
        }));
        var client = new RouterHealthClient(httpClient);

        var result = await client.ProbeResponsesRouteAsync(new Uri("http://127.0.0.1:8080/"));

        Assert.True(result.IsAvailable);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task ProbeResponsesRouteAsync_RejectsHtmlOrUnstructuredServerErrors()
    {
        using var htmlClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("<html>proxy error</html>"),
            }));
        using var serverErrorClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"message\":\"failed\"}}"),
            }));

        var htmlResult = await new RouterHealthClient(htmlClient)
            .ProbeResponsesRouteAsync(new Uri("http://127.0.0.1:8080/"));
        var serverErrorResult = await new RouterHealthClient(serverErrorClient)
            .ProbeResponsesRouteAsync(new Uri("http://127.0.0.1:8080/"));

        Assert.False(htmlResult.IsAvailable);
        Assert.False(serverErrorResult.IsAvailable);
    }

    [Fact]
    public async Task ProbeResponsesRouteAsync_AcceptsStructuredSuccessResponse()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"resp_probe\",\"object\":\"response\"}"),
            }));

        var result = await new RouterHealthClient(httpClient)
            .ProbeResponsesRouteAsync(new Uri("http://127.0.0.1:8080/"));

        Assert.True(result.IsAvailable);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ProbeResponsesRouteAsync_RejectsMissingRoute()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new RouterHealthClient(httpClient);

        var result = await client.ProbeResponsesRouteAsync(new Uri("http://127.0.0.1:8080/"));

        Assert.False(result.IsAvailable);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task UnloadModelAsync_PostsExactModelId()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:8080/models/unload", request.RequestUri?.AbsoluteUri);
            Assert.Equal(
                "{\"model\":\"local/coder\"}",
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var client = new RouterHealthClient(httpClient);

        var result = await client.UnloadModelAsync(new Uri("http://127.0.0.1:8080/"), "local/coder");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ProbeAsync_ReadsOpenAiModelList()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/health" => "{\"status\":\"ok\"}",
                "/models" => "{\"data\":[{\"id\":\"local/coder\"}]}",
                _ => throw new InvalidOperationException(),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        var client = new RouterHealthClient(httpClient);

        var result = await client.ProbeAsync(new Uri("http://127.0.0.1:18080/"));

        Assert.True(result.IsHealthy);
        Assert.Equal(new[] { "local/coder" }, result.ModelIds);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

}
