using System.Net;
using System.Net.Http.Headers;
using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class RouterModelDownloadClientTests
{
    [Fact]
    public async Task DownloadAsync_UsesNativeRouterEventsAndReturnsCachePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var modelPath = Path.Combine(root, "model-Q4_K_M.gguf");
            await File.WriteAllBytesAsync(modelPath, [1, 2, 3]);
            var getModelsCount = 0;
            using var httpClient = new HttpClient(new StubHandler(request =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/models/sse")
                {
                    return EventResponse("""
                        data: {"model":"org/model:Q4_K_M","event":"download_progress","data":{"https://host/model.gguf":{"done":50,"total":100}}}

                        data: {"model":"org/model:Q4_K_M","event":"download_finished"}

                        """);
                }

                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/models")
                {
                    return JsonResponse("{\"success\":true}");
                }

                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/models")
                {
                    getModelsCount++;
                    return getModelsCount == 1
                        ? JsonResponse("{\"data\":[]}")
                        : JsonResponse($"{{\"data\":[{{\"id\":\"org/model:Q4_K_M\",\"path\":{System.Text.Json.JsonSerializer.Serialize(modelPath)}}}]}}");
                }

                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }));
            var client = new RouterModelDownloadClient(httpClient);
            var observer = new CaptureProgress();

            var result = await client.DownloadAsync(
                new Uri("http://127.0.0.1:8080/"),
                "org/model:Q4_K_M",
                observer);

            Assert.False(result.WasAlreadyCached);
            Assert.Equal(Path.GetFullPath(modelPath), result.ModelPath);
            var update = Assert.Single(observer.Updates);
            Assert.Equal(50, update.DownloadedBytes);
            Assert.Equal(100, update.TotalBytes);
            Assert.Equal(0.5, update.Fraction);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_DoesNotStartDownloadWhenModelIsAlreadyCached()
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var modelPath = Path.Combine(root, "cached.gguf");
            await File.WriteAllBytesAsync(modelPath, [1]);
            using var httpClient = new HttpClient(new StubHandler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return JsonResponse($"{{\"data\":[{{\"id\":\"org/model:Q4_K_M\",\"path\":{System.Text.Json.JsonSerializer.Serialize(modelPath)}}}]}}");
            }));
            var client = new RouterModelDownloadClient(httpClient);

            var result = await client.DownloadAsync(
                new Uri("http://127.0.0.1:8080/"),
                "org/model:Q4_K_M");

            Assert.True(result.WasAlreadyCached);
            Assert.Equal(Path.GetFullPath(modelPath), result.ModelPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_AcceptsNativeCacheRegistrationWithoutPath()
    {
        var getModelsCount = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/models/sse")
            {
                return EventResponse("""
                    data: {"model":"org/model:Q4_K_M","event":"download_finished"}

                    """);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/models")
            {
                return JsonResponse("{\"success\":true}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/models")
            {
                getModelsCount++;
                return getModelsCount == 1
                    ? JsonResponse("{\"data\":[]}")
                    : JsonResponse("{\"data\":[{\"id\":\"org/model:Q4_K_M\",\"source\":\"cache\"}]}");
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }));
        var client = new RouterModelDownloadClient(httpClient);

        var result = await client.DownloadAsync(
            new Uri("http://127.0.0.1:8080/"),
            "org/model:Q4_K_M");

        Assert.False(result.WasAlreadyCached);
        Assert.True(result.RegistrationObserved);
        Assert.Null(result.ModelPath);
    }

    [Fact]
    public async Task DownloadAsync_WhenNativeEventStreamStalls_FailsInsteadOfWaitingForever()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/models/sse")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new NeverEndingReadStream()),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return response;
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/models")
            {
                return JsonResponse("{\"success\":true}");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/models")
            {
                return JsonResponse("{\"data\":[]}");
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }));
        var client = new RouterModelDownloadClient(
            httpClient,
            controlRequestTimeout: TimeSpan.FromSeconds(1),
            eventInactivityTimeout: TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadAsync(
            new Uri("http://127.0.0.1:8080/"),
            "org/model:Q4_K_M"));

        Assert.Contains("无限停留在 0%", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage EventResponse(string events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(events, System.Text.Encoding.UTF8, "text/event-stream"),
    };

    private sealed class CaptureProgress : IProgress<RouterModelDownloadProgress>
    {
        public List<RouterModelDownloadProgress> Updates { get; } = new();

        public void Report(RouterModelDownloadProgress value) => Updates.Add(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class NeverEndingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(
                _ => 0,
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
    }
}
