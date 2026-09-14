using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Launcher.Core.Security;

namespace Launcher.Runtime.Transport;

/// <summary>
/// Exposes a deliberately small loopback-only surface for ChatGPT Desktop.
/// It strips account credentials and forwards request semantics unchanged to llama.cpp.
/// </summary>
public sealed class LoopbackSafetyProxy : ILoopbackSafetyProxy
{
    private const int MaximumRequestBodyBytes = 16 * 1024 * 1024;
    private const long MaximumDiagnosticLogBytes = 8L * 1024 * 1024;

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    // A positive allow-list ensures Authorization, Cookie and account metadata never
    // leave the Desktop-facing boundary. Additions must be transport requirements,
    // not model or product behavior.
    private static readonly HashSet<string> AllowedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "Content-Encoding",
        "Content-Type",
        "OpenAI-Beta",
        "User-Agent",
    };

    private static readonly HashSet<string> KnownResponseItemTypes = new(StringComparer.Ordinal)
    {
        "apply_patch_call",
        "apply_patch_call_output",
        "compaction",
        "compaction_trigger",
        "computer_call",
        "computer_call_output",
        "custom_tool_call",
        "custom_tool_call_output",
        "function_call",
        "function_call_output",
        "item_reference",
        "local_shell_call",
        "local_shell_call_output",
        "message",
        "reasoning",
        "shell_call",
        "shell_call_output",
        "web_search_call",
    };

    private static readonly HashSet<string> KnownToolTypes = new(StringComparer.Ordinal)
    {
        "apply_patch",
        "computer_use_preview",
        "custom",
        "function",
        "local_shell",
        "mcp",
        "namespace",
        "shell",
        "web_search",
        "web_search_preview",
    };

    private static readonly HashSet<string> KnownRequestKinds = new(StringComparer.Ordinal)
    {
        "compaction",
        "turn",
    };

    private static readonly HashSet<string> KnownCompactionTriggers = new(StringComparer.Ordinal)
    {
        "auto",
        "manual",
    };

    private static readonly HashSet<string> KnownCompactionImplementations = new(StringComparer.Ordinal)
    {
        "responses",
        "responses_compact",
        "token_budget",
    };

    private static readonly HashSet<string> KnownCompactionPhases = new(StringComparer.Ordinal)
    {
        "mid_turn",
        "post_turn",
        "pre_turn",
        "standalone_turn",
    };

    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _diagnosticGate = new(1, 1);
    private readonly string? _diagnosticLogPath;
    private WebApplication? _application;
    private Uri? _upstreamBaseUri;
    private string? _upstreamApiKey;
    private bool _diagnosticLogHardened;
    private bool _disposed;

    public LoopbackSafetyProxy(string? diagnosticLogPath = null)
    {
        _diagnosticLogPath = string.IsNullOrWhiteSpace(diagnosticLogPath)
            ? null
            : Path.GetFullPath(diagnosticLogPath);
    }

    public bool IsRunning => _application is not null;

    public Uri? PublicBaseUri { get; private set; }

    public async Task StartAsync(
        Uri publicBaseUri,
        Uri upstreamBaseUri,
        CancellationToken cancellationToken = default,
        string? upstreamApiKey = null)
    {
        ValidateLoopbackHttpUri(publicBaseUri, nameof(publicBaseUri));
        ValidateLoopbackHttpUri(upstreamBaseUri, nameof(upstreamBaseUri));
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.IsNullOrWhiteSpace(upstreamApiKey)
            && (upstreamApiKey.Length > 256 || upstreamApiKey.IndexOfAny(['\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("llama.cpp upstream API key 无效。", nameof(upstreamApiKey));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                throw new InvalidOperationException("本地安全代理已经在运行。");
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(IPAddress.Loopback, publicBaseUri.Port);
                options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            });

            var application = builder.Build();
            _upstreamBaseUri = EnsureTrailingSlash(upstreamBaseUri);
            _upstreamApiKey = upstreamApiKey;
            application.Run(ForwardAsync);

            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                _upstreamBaseUri = null;
                _upstreamApiKey = null;
                throw;
            }

            _application = application;
            PublicBaseUri = publicBaseUri.Port == 0
                ? ResolveBoundLoopbackUri(application)
                : EnsureTrailingSlash(publicBaseUri);
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "safety_proxy_started",
                publicBaseUri = SafeEndpoint(PublicBaseUri),
                upstreamBaseUri = SafeEndpoint(_upstreamBaseUri),
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Uri ResolveBoundLoopbackUri(WebApplication application)
    {
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        if (addresses is null)
        {
            throw new InvalidOperationException("安全代理未报告实际绑定的回环端口。");
        }

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var candidate)
                && candidate.Scheme == Uri.UriSchemeHttp
                && IPAddress.TryParse(candidate.Host, out var host)
                && IPAddress.IsLoopback(host)
                && candidate.Port > 0)
            {
                return EnsureTrailingSlash(candidate);
            }
        }

        throw new InvalidOperationException("安全代理没有绑定有效的 IPv4 回环端点。");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var application = _application;
            if (application is null)
            {
                return;
            }

            await application.StopAsync(cancellationToken).ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
            _application = null;
            PublicBaseUri = null;
            _upstreamBaseUri = null;
            _upstreamApiKey = null;
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "safety_proxy_stopped",
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _httpClient.Dispose();
        _gate.Dispose();
        _diagnosticGate.Dispose();
    }

    private async Task ForwardAsync(HttpContext context)
    {
        var upstreamBaseUri = _upstreamBaseUri
            ?? throw new InvalidOperationException("本地安全代理尚未配置 llama.cpp upstream。");
        var target = new Uri(
            upstreamBaseUri,
            context.Request.Path.Value?.TrimStart('/') + context.Request.QueryString.Value);
        var requestId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var stage = "receive_request";
        RequestCopySummary? copySummary = null;
        var codexMetadata = ReadCodexRequestMetadata(context.Request);

        await TryWriteDiagnosticAsync(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            eventName = "request_received",
            requestId,
            method = context.Request.Method,
            path = context.Request.Path.Value,
            protocol = context.Request.Protocol,
            contentLength = context.Request.ContentLength,
            contentType = context.Request.ContentType,
            contentEncoding = ReadContentEncodings(context.Request),
            requestKind = codexMetadata.RequestKind,
            compactionTrigger = codexMetadata.CompactionTrigger,
            compactionImplementation = codexMetadata.CompactionImplementation,
            compactionPhase = codexMetadata.CompactionPhase,
        }).ConfigureAwait(false);

        if (context.Request.Headers.ContainsKey("Origin"))
        {
            await RejectRequestAsync(
                context,
                requestId,
                stopwatch,
                StatusCodes.Status403Forbidden,
                "browser_origin_rejected",
                "Browser-originated requests are not accepted by the Local safety proxy.")
                .ConfigureAwait(false);
            return;
        }

        if (IsWebSocketUpgrade(context.Request))
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                || !PathEquals(context.Request.Path, "/v1/responses"))
            {
                await RejectRequestAsync(
                    context,
                    requestId,
                    stopwatch,
                    StatusCodes.Status404NotFound,
                    "route_rejected",
                    "The requested Local safety proxy route is not available.")
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(
                new { error = "Local llama.cpp uses the HTTP transport." },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedHttpRoute(context.Request))
        {
            await RejectRequestAsync(
                context,
                requestId,
                stopwatch,
                StatusCodes.Status404NotFound,
                "route_rejected",
                "The requested Local safety proxy route is not available.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            using var upstreamRequest = new HttpRequestMessage(
                new HttpMethod(context.Request.Method),
                target);
            copySummary = await CopyRequestAsync(
                context.Request,
                upstreamRequest,
                value => stage = value,
                context.RequestAborted).ConfigureAwait(false);
            if (IsCompactionRequest(context.Request, codexMetadata, copySummary.Semantics))
            {
                await TryWriteDiagnosticAsync(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventName = "codex_compaction_request",
                    requestId,
                    path = context.Request.Path.Value,
                    requestKind = codexMetadata.RequestKind,
                    compactionTrigger = codexMetadata.CompactionTrigger,
                    compactionImplementation = codexMetadata.CompactionImplementation,
                    compactionPhase = codexMetadata.CompactionPhase,
                    protocolShape = CompactionProtocolShape(
                        context.Request,
                        codexMetadata,
                        copySummary.Semantics),
                    inputItemTypes = copySummary.Semantics.InputItemTypes,
                }).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_upstreamApiKey))
            {
                upstreamRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _upstreamApiKey);
            }

            stage = "send_upstream";
            using var upstreamResponse = await _httpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            stage = "forward_response";
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(upstreamResponse, context.Response);

            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "upstream_response",
                requestId,
                upstream = SafeEndpoint(target),
                statusCode = (int)upstreamResponse.StatusCode,
                originalBodyBytes = copySummary.OriginalBodyBytes,
                forwardedBodyBytes = copySummary.ForwardedBodyBytes,
                requestDecoded = copySummary.Decoded,
                requestKind = codexMetadata.RequestKind,
                inputItemTypes = copySummary.Semantics.InputItemTypes,
                toolTypes = copySummary.Semantics.ToolTypes,
                hasCompactionTrigger = copySummary.Semantics.HasCompactionTrigger,
                durationMs = stopwatch.ElapsedMilliseconds,
            }).ConfigureAwait(false);

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                var errorBody = await upstreamResponse.Content.ReadAsByteArrayAsync(context.RequestAborted)
                    .ConfigureAwait(false);
                var safeDiagnostic = SafeDiagnostic(errorBody);
                Console.Error.WriteLine(
                    $"llama.cpp returned HTTP {(int)upstreamResponse.StatusCode} for "
                    + $"{context.Request.Method} {context.Request.Path}: {safeDiagnostic}");
                await TryWriteDiagnosticAsync(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventName = "upstream_error",
                    requestId,
                    statusCode = (int)upstreamResponse.StatusCode,
                    diagnostic = safeDiagnostic,
                    requestKind = codexMetadata.RequestKind,
                    inputItemTypes = copySummary.Semantics.InputItemTypes,
                    hasCompactionTrigger = copySummary.Semantics.HasCompactionTrigger,
                    durationMs = stopwatch.ElapsedMilliseconds,
                }).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(errorBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await upstreamResponse.Content.CopyToAsync(
                context.Response.Body,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "request_cancelled",
                requestId,
                stage,
                durationMs = stopwatch.ElapsedMilliseconds,
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var safeMessage = SafeExceptionMessage(exception);
            Console.Error.WriteLine(
                $"Local safety proxy failed for {context.Request.Method} {context.Request.Path}: "
                + $"{exception.GetType().Name}: {safeMessage}");
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "safety_proxy_error",
                requestId,
                stage,
                upstream = SafeEndpoint(target),
                exceptionType = exception.GetType().FullName,
                message = safeMessage,
                originalBodyBytes = copySummary?.OriginalBodyBytes,
                forwardedBodyBytes = copySummary?.ForwardedBodyBytes,
                requestKind = codexMetadata.RequestKind,
                inputItemTypes = copySummary?.Semantics.InputItemTypes,
                hasCompactionTrigger = copySummary?.Semantics.HasCompactionTrigger,
                durationMs = stopwatch.ElapsedMilliseconds,
            }).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = new
                    {
                        message = $"Local safety proxy failed during {stage}. Diagnostic ID: {requestId}",
                        type = "proxy_error",
                        code = "local_safety_proxy_error",
                    },
                },
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task<RequestCopySummary> CopyRequestAsync(
        HttpRequest source,
        HttpRequestMessage destination,
        Action<string> setStage,
        CancellationToken cancellationToken)
    {
        var originalBodyBytes = 0;
        var decoded = false;
        if (RequestMayHaveBody(source))
        {
            setStage("read_request_body");
            await using var buffer = new MemoryStream();
            await source.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var body = buffer.ToArray();
            originalBodyBytes = body.Length;
            var encodings = ReadContentEncodings(source);
            if (encodings.Length > 0)
            {
                setStage("decode_transport_body");
                body = DecodeBody(body, encodings);
                decoded = true;
            }

            destination.Content = new ByteArrayContent(body);
            setStage("copy_request_headers");
            CopyAllowedRequestHeaders(source, destination, decoded);
            return new RequestCopySummary(
                originalBodyBytes,
                body.Length,
                decoded,
                SummarizeRequestBody(body, source.ContentType));
        }

        setStage("copy_request_headers");
        CopyAllowedRequestHeaders(source, destination, decoded);
        return new RequestCopySummary(0, 0, false, RequestSemanticSummary.Empty);
    }

    private static RequestSemanticSummary SummarizeRequestBody(byte[] body, string? contentType)
    {
        if (body.Length == 0
            || contentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return RequestSemanticSummary.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RequestSemanticSummary.Empty;
            }

            var inputItemTypes = ReadTypeNames(
                document.RootElement,
                "input",
                KnownResponseItemTypes);
            var toolTypes = ReadTypeNames(document.RootElement, "tools", KnownToolTypes);
            return new RequestSemanticSummary(
                inputItemTypes,
                toolTypes,
                inputItemTypes.Contains("compaction_trigger", StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return RequestSemanticSummary.Empty;
        }
    }

    private static string[] ReadTypeNames(
        JsonElement root,
        string propertyName,
        IReadOnlySet<string> knownTypes)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("type").GetString())
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => knownTypes.Contains(type!) ? type! : "other")
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
    }

    private static CodexRequestMetadata ReadCodexRequestMetadata(HttpRequest request)
    {
        var raw = request.Headers["X-Codex-Turn-Metadata"].ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096)
        {
            return CodexRequestMetadata.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var requestKind = ReadKnownString(root, "request_kind", KnownRequestKinds);
            if (!root.TryGetProperty("compaction", out var compaction)
                || compaction.ValueKind != JsonValueKind.Object)
            {
                return new CodexRequestMetadata(requestKind, null, null, null);
            }

            return new CodexRequestMetadata(
                requestKind,
                ReadKnownString(compaction, "trigger", KnownCompactionTriggers),
                ReadKnownString(compaction, "implementation", KnownCompactionImplementations),
                ReadKnownString(compaction, "phase", KnownCompactionPhases));
        }
        catch (JsonException)
        {
            return CodexRequestMetadata.Empty;
        }
    }

    private static string? ReadKnownString(
        JsonElement value,
        string propertyName,
        IReadOnlySet<string> knownValues)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return text is not null && knownValues.Contains(text) ? text : null;
    }

    private static bool IsCompactionRequest(
        HttpRequest request,
        CodexRequestMetadata metadata,
        RequestSemanticSummary semantics) =>
        PathEquals(request.Path, "/v1/responses/compact")
        || string.Equals(metadata.RequestKind, "compaction", StringComparison.Ordinal)
        || semantics.HasCompactionTrigger;

    private static string CompactionProtocolShape(
        HttpRequest request,
        CodexRequestMetadata metadata,
        RequestSemanticSummary semantics)
    {
        if (PathEquals(request.Path, "/v1/responses/compact"))
        {
            return "legacy_remote_endpoint";
        }

        if (semantics.HasCompactionTrigger)
        {
            return "remote_v2_trigger";
        }

        return string.Equals(metadata.RequestKind, "compaction", StringComparison.Ordinal)
            ? "codex_local_summary_turn"
            : "unknown";
    }

    private static void CopyAllowedRequestHeaders(
        HttpRequest source,
        HttpRequestMessage destination,
        bool decoded)
    {
        foreach (var header in source.Headers)
        {
            if (!AllowedRequestHeaders.Contains(header.Key)
                || decoded && header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!destination.Headers.TryAddWithoutValidation(header.Key, values))
            {
                destination.Content?.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }
    }

    private static string[] ReadContentEncodings(HttpRequest request) =>
        request.Headers.ContentEncoding
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool RequestMayHaveBody(HttpRequest request) =>
        request.ContentLength > 0
        || request.Headers.ContainsKey("Transfer-Encoding")
        || HttpMethods.IsPost(request.Method)
        || HttpMethods.IsPut(request.Method)
        || HttpMethods.IsPatch(request.Method);

    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        if (!string.Equals(
                request.Headers.Upgrade.ToString().Trim(),
                "websocket",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.Headers.Connection
            .SelectMany(value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(value => value.Equals("upgrade", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAllowedHttpRoute(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
            && (PathEquals(request.Path, "/v1/responses")
                || PathEquals(request.Path, "/v1/responses/compact"))
        || HttpMethods.IsGet(request.Method)
            && (PathEquals(request.Path, "/models")
                || PathEquals(request.Path, "/v1/models")
                || PathEquals(request.Path, "/health"));

    private static bool PathEquals(PathString actual, string expected) =>
        string.Equals(actual.Value, expected, StringComparison.OrdinalIgnoreCase);

    private async Task RejectRequestAsync(
        HttpContext context,
        string requestId,
        Stopwatch stopwatch,
        int statusCode,
        string eventName,
        string message)
    {
        await TryWriteDiagnosticAsync(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            eventName,
            requestId,
            method = context.Request.Method,
            path = context.Request.Path.Value,
            statusCode,
            durationMs = stopwatch.ElapsedMilliseconds,
        }).ConfigureAwait(false);
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = new
                {
                    message,
                    type = "invalid_request_error",
                    code = eventName,
                },
            },
            context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[] DecodeBody(byte[] body, IReadOnlyList<string> encodings)
    {
        var current = body;
        for (var index = encodings.Count - 1; index >= 0; index--)
        {
            using var input = new MemoryStream(current, writable: false);
            using Stream decoder = encodings[index].ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                "zstd" => new ZstdSharp.DecompressionStream(input),
                _ => throw new InvalidDataException(
                    $"不支持的请求 Content-Encoding：{encodings[index]}。"),
            };
            using var output = new MemoryStream();
            CopyWithLimit(decoder, output, MaximumRequestBodyBytes);
            current = output.ToArray();
        }

        return current;
    }

    private static void CopyWithLimit(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = new byte[81920];
        var totalBytes = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalBytes = checked(totalBytes + bytesRead);
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException(
                    $"解码后的请求超过 {maximumBytes / (1024 * 1024)} MiB 限制。");
            }

            destination.Write(buffer, 0, bytesRead);
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        destination.Headers.Remove("transfer-encoding");
    }

    private static void ValidateLoopbackHttpUri(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(uri.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("安全代理只允许 HTTP 回环地址。", parameterName);
        }
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        new(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);

    private async Task TryWriteDiagnosticAsync(object value)
    {
        if (_diagnosticLogPath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_diagnosticLogPath)
                ?? throw new InvalidOperationException("代理日志必须位于一个目录中。");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(value) + Environment.NewLine;
            await _diagnosticGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                RotateDiagnosticLogIfNeeded();
                var shouldHarden = !_diagnosticLogHardened || !File.Exists(_diagnosticLogPath);
                await using var stream = new FileStream(
                    _diagnosticLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);
                if (shouldHarden)
                {
                    PrivateFilePermissions.HardenFile(_diagnosticLogPath);
                    _diagnosticLogHardened = true;
                }
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
            finally
            {
                _diagnosticGate.Release();
            }
        }
        catch
        {
            // Diagnostics must never break the Local inference path.
        }
    }

    private void RotateDiagnosticLogIfNeeded()
    {
        if (_diagnosticLogPath is null
            || !File.Exists(_diagnosticLogPath)
            || new FileInfo(_diagnosticLogPath).Length < MaximumDiagnosticLogBytes)
        {
            return;
        }

        var secondBackup = _diagnosticLogPath + ".2";
        var firstBackup = _diagnosticLogPath + ".1";
        if (File.Exists(secondBackup))
        {
            File.Delete(secondBackup);
        }

        if (File.Exists(firstBackup))
        {
            File.Move(firstBackup, secondBackup, overwrite: true);
        }

        File.Move(_diagnosticLogPath, firstBackup, overwrite: true);
        _diagnosticLogHardened = false;
    }

    private static string SafeEndpoint(Uri? uri) => uri is null
        ? string.Empty
        : $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";

    private static string SafeExceptionMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    private static string SafeDiagnostic(byte[] body)
    {
        var text = System.Text.Encoding.UTF8.GetString(body);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind is not JsonValueKind.Object)
            {
                return "JSON error body without structured error details";
            }

            var code = error.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "unknown";
            var type = error.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "unknown";
            return $"code={code}, type={type}";
        }
        catch
        {
            return "non-JSON error body";
        }
    }

    private sealed record RequestCopySummary(
        int OriginalBodyBytes,
        int ForwardedBodyBytes,
        bool Decoded,
        RequestSemanticSummary Semantics);

    private sealed record RequestSemanticSummary(
        IReadOnlyList<string> InputItemTypes,
        IReadOnlyList<string> ToolTypes,
        bool HasCompactionTrigger)
    {
        public static RequestSemanticSummary Empty { get; } = new([], [], false);
    }

    private sealed record CodexRequestMetadata(
        string? RequestKind,
        string? CompactionTrigger,
        string? CompactionImplementation,
        string? CompactionPhase)
    {
        public static CodexRequestMetadata Empty { get; } = new(null, null, null, null);
    }
}
