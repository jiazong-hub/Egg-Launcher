using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Launcher.Runtime.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Launcher.Tests;

public sealed class LoopbackSafetyProxyTests
{
    [Fact]
    public async Task SafetyProxy_WhenPortZeroRequested_BindsAndReportsAnOwnedLoopbackPort()
    {
        var upstreamPort = ReserveAvailableLoopbackPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.MapGet("/v1/models", () => Results.Json(new { data = Array.Empty<object>() }));
        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy();
            await proxy.StartAsync(
                new Uri("http://127.0.0.1:0/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));

            Assert.NotNull(proxy.PublicBaseUri);
            Assert.True(proxy.PublicBaseUri.Port > 0);
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(proxy.PublicBaseUri.Host)));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(new Uri(proxy.PublicBaseUri, "v1/models"));
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            await upstream.StopAsync();
        }
    }

    [Fact]
    public async Task SafetyProxy_AfterAddressConflict_CanBindAReplacementPort()
    {
        var upstreamPort = ReserveAvailableLoopbackPort();
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.MapGet("/v1/models", () => Results.Json(new { data = Array.Empty<object>() }));
        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy();
            await Assert.ThrowsAnyAsync<IOException>(() => proxy.StartAsync(
                new Uri($"http://127.0.0.1:{occupiedPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/")));
            Assert.False(proxy.IsRunning);

            await proxy.StartAsync(
                new Uri("http://127.0.0.1:0/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));

            Assert.True(proxy.IsRunning);
            Assert.NotNull(proxy.PublicBaseUri);
            Assert.NotEqual(occupiedPort, proxy.PublicBaseUri.Port);
        }
        finally
        {
            occupied.Stop();
            await upstream.StopAsync();
        }
    }


    [Theory]
    [InlineData("gzip")]
    [InlineData("zstd")]
    public async Task SafetyProxy_DecodesTransportWithoutChangingJsonAndStripsCredentials(string contentEncoding)
    {
        var root = CreateTemporaryDirectory();
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var diagnosticPath = Path.Combine(root, "proxy.jsonl");
        var capturedRequest = new TaskCompletionSource<CapturedRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.MapPost("/v1/responses", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            capturedRequest.TrySetResult(new CapturedRequest(
                await reader.ReadToEndAsync(context.RequestAborted),
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers.ContentEncoding.ToString(),
                context.Request.Headers["ChatGPT-Account-ID"].ToString(),
                context.Request.Headers["X-Future-Account-Metadata"].ToString(),
                context.Request.Headers["OpenAI-Beta"].ToString()));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"id\":\"resp_test\",\"status\":\"completed\",\"output\":[]}",
                context.RequestAborted);
        });

        try
        {
            await upstream.StartAsync();
            await using var proxy = new LoopbackSafetyProxy(diagnosticPath);
            const string upstreamToken = "LOCAL_LLAMA_API_KEY_MUST_NOT_BE_LOGGED";
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"),
                upstreamApiKey: upstreamToken);

            const string secretPrompt = "SECRET_USER_PROMPT_MUST_NOT_BE_LOGGED";
            const string secretToken = "SECRET_BEARER_TOKEN_MUST_NOT_BE_FORWARDED";
            var requestJson = JsonSerializer.Serialize(new
            {
                model = "local-coder",
                input = new object[]
                {
                    new { type = "message", role = "developer", content = "system context" },
                    new { type = "message", role = "user", content = secretPrompt },
                },
                tools = new object[]
                {
                    new { type = "function", name = "shell", parameters = new { type = "object" } },
                    new { type = "web_search" },
                },
                stream = true,
            });
            var compressed = await CompressAsync(requestJson, contentEncoding);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"http://127.0.0.1:{proxyPort}/v1/responses"))
            {
                Content = new ByteArrayContent(compressed),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add(contentEncoding);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretToken);
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", "account-secret");
            request.Headers.TryAddWithoutValidation("X-Future-Account-Metadata", "future-secret");
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=v1");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var captured = await capturedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(requestJson, captured.Body);
            using var forwarded = JsonDocument.Parse(captured.Body);
            Assert.Equal(2, forwarded.RootElement.GetProperty("input").GetArrayLength());
            Assert.Equal(2, forwarded.RootElement.GetProperty("tools").GetArrayLength());
            Assert.False(forwarded.RootElement.TryGetProperty("instructions", out _));
            Assert.Equal($"Bearer {upstreamToken}", captured.Authorization);
            Assert.Empty(captured.ContentEncoding);
            Assert.Empty(captured.ChatGptAccountId);
            Assert.Empty(captured.FutureAccountMetadata);
            Assert.Equal("responses=v1", captured.OpenAiBeta);

            var diagnostics = await File.ReadAllTextAsync(diagnosticPath);
            Assert.Contains("safety_proxy_started", diagnostics, StringComparison.Ordinal);
            Assert.Contains("upstream_response", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"requestDecoded\":true", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(secretPrompt, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(secretToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(upstreamToken, diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await upstream.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SafetyProxy_RejectsZstdBodyThatExpandsBeyondDecodedBodyLimit()
    {
        var root = CreateTemporaryDirectory();
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var diagnosticPath = Path.Combine(root, "proxy.jsonl");
        var upstreamRequestCount = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.Run(context =>
        {
            Interlocked.Increment(ref upstreamRequestCount);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy(diagnosticPath);
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            var oversizedBody = new byte[(16 * 1024 * 1024) + 1];
            var compressed = await CompressAsync(oversizedBody, "zstd");
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"http://127.0.0.1:{proxyPort}/v1/responses"))
            {
                Content = new ByteArrayContent(compressed),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("zstd");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal(0, Volatile.Read(ref upstreamRequestCount));
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("local_safety_proxy_error", responseBody, StringComparison.Ordinal);
            var diagnostics = await File.ReadAllTextAsync(diagnosticPath);
            Assert.Contains("\"stage\":\"decode_transport_body\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("16 MiB", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await upstream.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SafetyProxy_ForwardsCompactionRouteAndBodyUnchanged()
    {
        var root = CreateTemporaryDirectory();
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var diagnosticPath = Path.Combine(root, "proxy.jsonl");
        var capturedRequest = new TaskCompletionSource<(string Path, string Body)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.MapPost("/v1/responses/compact", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            capturedRequest.TrySetResult((
                context.Request.Path.Value ?? string.Empty,
                await reader.ReadToEndAsync(context.RequestAborted)));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"object\":\"upstream-result\"}", context.RequestAborted);
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy(diagnosticPath);
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            const string body = "{\"model\":\"local-coder\",\"input\":[{\"type\":\"compaction_trigger\"}]}";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.PostAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/v1/responses/compact"),
                new StringContent(body, Encoding.UTF8, "application/json"));

            response.EnsureSuccessStatusCode();
            var captured = await capturedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("/v1/responses/compact", captured.Path);
            Assert.Equal(body, captured.Body);
            Assert.Equal("{\"object\":\"upstream-result\"}", await response.Content.ReadAsStringAsync());
            var diagnostics = await File.ReadAllTextAsync(diagnosticPath);
            Assert.Contains("\"eventName\":\"codex_compaction_request\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"protocolShape\":\"legacy_remote_endpoint\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"hasCompactionTrigger\":true", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("local-coder", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await upstream.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SafetyProxy_LogsOnlySafeShapeForCodexLocalCompactionTurn()
    {
        var root = CreateTemporaryDirectory();
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var diagnosticPath = Path.Combine(root, "proxy.jsonl");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.MapPost("/v1/responses", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"id\":\"resp_compact\",\"status\":\"completed\",\"output\":[]}",
                context.RequestAborted);
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy(diagnosticPath);
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            const string secretPrompt = "PRIVATE_COMPACTION_PROMPT_MUST_NOT_BE_LOGGED";
            const string secretType = "PRIVATE_TYPE_MUST_NOT_BE_LOGGED";
            const string body =
                "{\"model\":\"local-coder\",\"input\":[{\"type\":\"message\","
                + "\"role\":\"user\",\"content\":\"" + secretPrompt + "\"},"
                + "{\"type\":\"" + secretType + "\"}],"
                + "\"tools\":[{\"type\":\"function\",\"name\":\"shell\"}]}";
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri($"http://127.0.0.1:{proxyPort}/v1/responses"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(
                "X-Codex-Turn-Metadata",
                "{\"request_kind\":\"compaction\",\"compaction\":{\"trigger\":\"auto\","
                + "\"implementation\":\"responses\",\"phase\":\"pre_turn\"},"
                + "\"turn_id\":\"sensitive-id-not-logged\"}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var response = await client.SendAsync(request);

            response.EnsureSuccessStatusCode();
            var diagnostics = await File.ReadAllTextAsync(diagnosticPath);
            Assert.Contains("\"eventName\":\"codex_compaction_request\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"requestKind\":\"compaction\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"protocolShape\":\"codex_local_summary_turn\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"compactionTrigger\":\"auto\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"inputItemTypes\":[\"message\",\"other\"]", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"toolTypes\":[\"function\"]", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(secretPrompt, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(secretType, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive-id-not-logged", diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain("local-coder", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await upstream.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SafetyProxy_WebSocketUpgradeReturns426WithoutReachingUpstream()
    {
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var upstreamRequestCount = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.Run(context =>
        {
            Interlocked.Increment(ref upstreamRequestCount);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy();
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"http://127.0.0.1:{proxyPort}/v1/responses"))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
            Assert.Equal(0, Volatile.Read(ref upstreamRequestCount));
        }
        finally
        {
            await upstream.StopAsync();
        }
    }

    [Theory]
    [InlineData("GET", "/v1/responses", null, HttpStatusCode.NotFound)]
    [InlineData("POST", "/v1/chat/completions", null, HttpStatusCode.NotFound)]
    [InlineData("POST", "/models/unload", null, HttpStatusCode.NotFound)]
    [InlineData("POST", "/v1/responses", "https://attacker.example", HttpStatusCode.Forbidden)]
    public async Task SafetyProxy_RejectsDisallowedMethodsPathsAndBrowserOrigins(
        string method,
        string path,
        string? origin,
        HttpStatusCode expectedStatus)
    {
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var upstreamRequestCount = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.Run(context =>
        {
            Interlocked.Increment(ref upstreamRequestCount);
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy();
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            using var request = new HttpRequestMessage(
                new HttpMethod(method),
                new Uri($"http://127.0.0.1:{proxyPort}{path}"));
            if (origin is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.SendAsync(request);

            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Equal(0, Volatile.Read(ref upstreamRequestCount));
        }
        finally
        {
            await upstream.StopAsync();
        }
    }

    [Theory]
    [InlineData("/models")]
    [InlineData("/v1/models")]
    public async Task SafetyProxy_AllowsReadOnlyModelListRoutes(string path)
    {
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.Run(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"data\":[{\"id\":\"local-coder\"}]}", context.RequestAborted);
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy();
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{proxyPort}{path}"));

            response.EnsureSuccessStatusCode();
            Assert.Contains("local-coder", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await upstream.StopAsync();
        }
    }

    [Fact]
    public async Task SafetyProxy_PreservesUpstreamErrorBody()
    {
        var upstreamPort = ReserveAvailableLoopbackPort();
        var proxyPort = ReserveAvailableLoopbackPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, upstreamPort));
        await using var upstream = builder.Build();
        upstream.MapPost("/v1/responses", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status418ImATeapot;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{}", context.RequestAborted);
        });

        await upstream.StartAsync();
        try
        {
            await using var proxy = new LoopbackSafetyProxy();
            await proxy.StartAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/"),
                new Uri($"http://127.0.0.1:{upstreamPort}/"));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            using var response = await client.PostAsync(
                new Uri($"http://127.0.0.1:{proxyPort}/v1/responses"),
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal((HttpStatusCode)418, response.StatusCode);
            Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await upstream.StopAsync();
        }
    }

    private static int ReserveAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<byte[]> CompressAsync(string value, string contentEncoding)
    {
        return await CompressAsync(Encoding.UTF8.GetBytes(value), contentEncoding);
    }

    private static async Task<byte[]> CompressAsync(ReadOnlyMemory<byte> value, string contentEncoding)
    {
        using var output = new MemoryStream();
        await using (Stream compressor = contentEncoding switch
        {
            "gzip" => new GZipStream(output, CompressionMode.Compress, leaveOpen: true),
            "zstd" => new ZstdSharp.CompressionStream(output, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(contentEncoding)),
        })
        {
            await compressor.WriteAsync(value);
        }

        return output.ToArray();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record CapturedRequest(
        string Body,
        string Authorization,
        string ContentEncoding,
        string ChatGptAccountId,
        string FutureAccountMetadata,
        string OpenAiBeta);
}
