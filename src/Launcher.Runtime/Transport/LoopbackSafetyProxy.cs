using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Launcher.Core.Diagnostics;
using Launcher.Core.Security;

namespace Launcher.Runtime.Transport;

/// <summary>
/// Exposes a deliberately small loopback-only surface for ChatGPT Desktop.
/// It strips account credentials and preserves conversation content; verified explicit thinking overrides use native request parameters.
/// </summary>
public sealed class LoopbackSafetyProxy : ILoopbackSafetyProxy
{
    private int _thinkingOverride = -1;
    private int _showThinkingProcess;

    public void SetShowThinkingProcess(bool enabled) => Volatile.Write(ref _showThinkingProcess, enabled ? 1 : 0);

    public void SetThinkingEnabled(bool? enabled) => Volatile.Write(ref _thinkingOverride, enabled.HasValue ? (enabled.Value ? 1 : 0) : -1);
    // Base64 image input is larger than the source image. Keep the boundary bounded,
    // but leave enough room for a normal desktop screenshot plus prompt/tool metadata.
    private const int MaximumRequestBodyBytes = 32 * 1024 * 1024;
    private const int MaximumDecodedRequestBodyBytes = 32 * 1024 * 1024;
    private const long MaximumDiagnosticLogBytes = 8L * 1024 * 1024;
    private const int MaximumFailureBursts = 256;
    private static readonly TimeSpan FailureBurstWindow = TimeSpan.FromMinutes(2);

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
    private readonly string? _conciseDiagnosticLogPath;
    private readonly string? _fullDiagnosticLogPath;
    private readonly Func<Exception, Task>? _diagnosticFailureReporter;
    private readonly object _failureBurstGate = new();
    private readonly Dictionary<string, FailureBurstState> _failureBursts = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private Uri? _upstreamBaseUri;
    private string? _upstreamApiKey;
    private string? _diagnosticLogHardenedPath;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private string? _routerRunId;
    private int _detailedDiagnosticsEnabled;
    private long _lastDiagnosticFailureReportAtMilliseconds;
    private bool _disposed;

    public LoopbackSafetyProxy(
        string? diagnosticLogPath = null,
        string? fullDiagnosticLogPath = null,
        string? sessionId = null,
        Func<Exception, Task>? diagnosticFailureReporter = null)
    {
        _conciseDiagnosticLogPath = string.IsNullOrWhiteSpace(diagnosticLogPath)
            ? null
            : Path.GetFullPath(diagnosticLogPath);
        _fullDiagnosticLogPath = string.IsNullOrWhiteSpace(fullDiagnosticLogPath)
            ? _conciseDiagnosticLogPath
            : Path.GetFullPath(fullDiagnosticLogPath);
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? _sessionId : sessionId;
        _diagnosticFailureReporter = diagnosticFailureReporter;
    }

    public bool IsRunning => _application is not null;

    public Uri? PublicBaseUri { get; private set; }

    public void SetDetailedDiagnosticsEnabled(bool enabled) =>
        Volatile.Write(ref _detailedDiagnosticsEnabled, enabled ? 1 : 0);

    public void SetRouterRunId(string? routerRunId) =>
        Volatile.Write(ref _routerRunId, routerRunId);

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
            }, importantInConcise: true).ConfigureAwait(false);
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
            }, importantInConcise: true).ConfigureAwait(false);
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
        long responseBytesSent = 0;
        var responseIsEventStream = false;
        SseEventTracker? sseTracker = null;
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
            contentType = SafeMediaType(context.Request.ContentType),
            contentEncoding = ReadContentEncodings(context.Request),
            requestKind = codexMetadata.RequestKind,
            compactionTrigger = codexMetadata.CompactionTrigger,
            compactionImplementation = codexMetadata.CompactionImplementation,
            compactionPhase = codexMetadata.CompactionPhase,
        }, importantInConcise: false).ConfigureAwait(false);

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

            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "websocket_http_fallback",
                requestId,
                path = context.Request.Path.Value,
                statusCode = StatusCodes.Status426UpgradeRequired,
            }, importantInConcise: true).ConfigureAwait(false);
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
            var requestPreparationMs = stopwatch.ElapsedMilliseconds;
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "request_prepared",
                requestId,
                originalBodyBytes = copySummary.OriginalBodyBytes,
                forwardedBodyBytes = copySummary.ForwardedBodyBytes,
                requestDecoded = copySummary.Decoded,
                requestPreparationMs,
                inputItemTypes = copySummary.Semantics.InputItemTypes,
                toolTypes = copySummary.Semantics.ToolTypes,
                hasCompactionTrigger = copySummary.Semantics.HasCompactionTrigger,
                forwardedReasoningEffort = copySummary.Semantics.ReasoningEffort,
                thinkingOverride = Volatile.Read(ref _thinkingOverride) is var thinking && thinking >= 0 ? (bool?)(thinking == 1) : null,
            }, importantInConcise: false).ConfigureAwait(false);
            var isCompactionRequest = IsCompactionRequest(context.Request, codexMetadata, copySummary.Semantics);
            if (isCompactionRequest)
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
                }, importantInConcise: true).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(_upstreamApiKey))
            {
                upstreamRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _upstreamApiKey);
            }

            stage = "send_upstream";
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "upstream_request_started",
                requestId,
                method = context.Request.Method,
                path = context.Request.Path.Value,
                requestPreparationMs,
                isCompactionRequest,
            }, importantInConcise: false).ConfigureAwait(false);
            using var upstreamResponse = await _httpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);
            stage = "forward_response";
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(upstreamResponse, context.Response);

            var timeToHeadersMs = stopwatch.ElapsedMilliseconds;
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "upstream_response",
                responsePhase = "headers_received",
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
                timeToHeadersMs,
                durationMs = timeToHeadersMs,
                upstreamRequestId = ReadSafeUpstreamRequestId(upstreamResponse),
                responseContentType = SafeMediaType(upstreamResponse.Content.Headers.ContentType?.ToString()),
                responseContentLength = upstreamResponse.Content.Headers.ContentLength,
            }, importantInConcise: true)
                .ConfigureAwait(false);

            if (!upstreamResponse.IsSuccessStatusCode)
            {
                var errorBody = await upstreamResponse.Content.ReadAsByteArrayAsync(context.RequestAborted)
                    .ConfigureAwait(false);
                var safeDiagnostic = SafeDiagnostic(errorBody);
                var errorCategory = SafeDiagnosticCategory(errorBody);
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
                    errorCategory,
                    errorBodyBytes = errorBody.Length,
                    requestKind = codexMetadata.RequestKind,
                    inputItemTypes = copySummary.Semantics.InputItemTypes,
                    hasCompactionTrigger = copySummary.Semantics.HasCompactionTrigger,
                    durationMs = stopwatch.ElapsedMilliseconds,
                }, importantInConcise: true).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(errorBody, context.RequestAborted).ConfigureAwait(false);
                responseBytesSent = errorBody.Length;
                await TryWriteDiagnosticAsync(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventName = "response_completed",
                    requestId,
                    outcome = "upstream_http_error",
                    statusCode = (int)upstreamResponse.StatusCode,
                    responseBytes = responseBytesSent,
                    timeToHeadersMs,
                    totalDurationMs = stopwatch.ElapsedMilliseconds,
                    isCompactionRequest,
                }, importantInConcise: true).ConfigureAwait(false);
                return;
            }

            responseIsEventStream = string.Equals(
                upstreamResponse.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase);
            sseTracker = responseIsEventStream ? new SseEventTracker() : null;
            using var thinkingDisplay = responseIsEventStream && Volatile.Read(ref _showThinkingProcess) == 1
                ? new ThinkingDisplayBridge() : null;
            if (thinkingDisplay is not null) context.Response.ContentLength = null;
            await using var responseStream = await upstreamResponse.Content
                .ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
            var responseBuffer = new byte[81920];
            long? timeToFirstBodyByteMs = null;
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(
                    responseBuffer.AsMemory(),
                    context.RequestAborted).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                sseTracker?.Observe(responseBuffer.AsSpan(0, bytesRead), stopwatch.ElapsedMilliseconds);
                timeToFirstBodyByteMs ??= stopwatch.ElapsedMilliseconds;
                var forwarded = thinkingDisplay?.Observe(responseBuffer.AsSpan(0, bytesRead));
                var responseBytes = forwarded is null ? responseBuffer.AsMemory(0, bytesRead) : forwarded.AsMemory();
                await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted).ConfigureAwait(false);
                if (responseIsEventStream) await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                responseBytesSent += responseBytes.Length;
            }

            if (thinkingDisplay is not null)
            {
                var tail = thinkingDisplay.Complete();
                await context.Response.Body.WriteAsync(tail, context.RequestAborted).ConfigureAwait(false);
                responseBytesSent += tail.Length;
            }

            sseTracker?.Complete(stopwatch.ElapsedMilliseconds);
            var responseOutcome = sseTracker is null
                ? "success"
                : sseTracker.TerminalEvent switch
                {
                    "response.failed" => "sse_terminal_failed",
                    "response.incomplete" => "sse_terminal_incomplete",
                    null => "sse_eof_without_terminal_event",
                    _ => "success",
                };

            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "response_completed",
                requestId,
                outcome = responseOutcome,
                statusCode = (int)upstreamResponse.StatusCode,
                responseBytes = responseBytesSent,
                timeToHeadersMs,
                timeToFirstBodyByteMs,
                responseIsEventStream,
                sseEventCount = sseTracker?.EventCount,
                lastSseEvent = sseTracker?.LastEvent,
                lastSseEventElapsedMs = sseTracker?.LastEventElapsedMs,
                terminalSseEvent = sseTracker?.TerminalEvent,
                terminalSseEventElapsedMs = sseTracker?.TerminalEventElapsedMs,
                durationMs = stopwatch.ElapsedMilliseconds,
                totalDurationMs = stopwatch.ElapsedMilliseconds,
                isCompactionRequest,
            }, importantInConcise: isCompactionRequest || responseOutcome != "success")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            await TryWriteDiagnosticAsync(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventName = "request_cancelled",
                requestId,
                stage,
                downstreamAbortObserved = true,
                responseHasStarted = context.Response.HasStarted,
                responseBytesSent,
                responseIsEventStream,
                sseEventCount = sseTracker?.EventCount,
                lastSseEvent = sseTracker?.LastEvent,
                lastSseEventElapsedMs = sseTracker?.LastEventElapsedMs,
                terminalSseEvent = sseTracker?.TerminalEvent,
                terminalSseEventElapsedMs = sseTracker?.TerminalEventElapsedMs,
                durationMs = stopwatch.ElapsedMilliseconds,
                totalDurationMs = stopwatch.ElapsedMilliseconds,
            }, importantInConcise: true).ConfigureAwait(false);
        }
        catch (UnsafeMediaReferenceException exception) when (!context.Response.HasStarted)
        {
            await RejectRequestAsync(
                context,
                requestId,
                stopwatch,
                StatusCodes.Status400BadRequest,
                "unsafe_image_reference",
                exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var safeMessage = SafeExceptionMessage(exception);
            Console.Error.WriteLine(
                $"Local safety proxy failed for {context.Request.Method} {context.Request.Path}: "
                + $"{exception.GetType().Name}: {safeMessage}");
            var exceptionProperties = DiagnosticSanitizer.CreateExceptionProperties(
                exception,
                includeStackTrace: Volatile.Read(ref _detailedDiagnosticsEnabled) == 1);
            exceptionProperties["timestampUtc"] = DateTimeOffset.UtcNow;
            exceptionProperties["eventName"] = "safety_proxy_error";
            exceptionProperties["requestId"] = requestId;
            exceptionProperties["stage"] = stage;
            exceptionProperties["upstream"] = SafeEndpoint(target);
            exceptionProperties["message"] = safeMessage;
            exceptionProperties["originalBodyBytes"] = copySummary?.OriginalBodyBytes;
            exceptionProperties["forwardedBodyBytes"] = copySummary?.ForwardedBodyBytes;
            exceptionProperties["requestKind"] = codexMetadata.RequestKind;
            exceptionProperties["inputItemTypes"] = copySummary?.Semantics.InputItemTypes;
            exceptionProperties["hasCompactionTrigger"] = copySummary?.Semantics.HasCompactionTrigger;
            exceptionProperties["responseHasStarted"] = context.Response.HasStarted;
            exceptionProperties["responseBytesSent"] = responseBytesSent;
            exceptionProperties["responseIsEventStream"] = responseIsEventStream;
            exceptionProperties["sseEventCount"] = sseTracker?.EventCount;
            exceptionProperties["lastSseEvent"] = sseTracker?.LastEvent;
            exceptionProperties["lastSseEventElapsedMs"] = sseTracker?.LastEventElapsedMs;
            exceptionProperties["terminalSseEvent"] = sseTracker?.TerminalEvent;
            exceptionProperties["terminalSseEventElapsedMs"] = sseTracker?.TerminalEventElapsedMs;
            exceptionProperties["durationMs"] = stopwatch.ElapsedMilliseconds;
            exceptionProperties["totalDurationMs"] = stopwatch.ElapsedMilliseconds;
            await TryWriteDiagnosticAsync(exceptionProperties, importantInConcise: true).ConfigureAwait(false);
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

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

    private async Task<RequestCopySummary> CopyRequestAsync(
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

            ValidateImageReferences(body, source.ContentType);
            if (source.Path.Value is "/v1/responses" or "/responses" or "/v1/responses/compact" or "/responses/compact"
                && source.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                body = ThinkingDisplayBridge.RemoveDisplayCopies(body);
            var thinking = Volatile.Read(ref _thinkingOverride);
            if (thinking >= 0 && source.Path.Value is "/v1/responses" or "/responses"
                && source.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                body = ThinkingRequestPolicy.Apply(body, thinking == 1);
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
            string? effort = null;
            if (document.RootElement.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object
                && reasoning.TryGetProperty("effort", out var effortNode) && effortNode.ValueKind == JsonValueKind.String)
            {
                var raw = effortNode.GetString();
                effort = raw is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra" or "persistent" ? raw : "unrecognized";
            }
            return new RequestSemanticSummary(
                inputItemTypes,
                toolTypes,
                inputItemTypes.Contains("compaction_trigger", StringComparer.Ordinal), effort);
        }
        catch (JsonException)
        {
            return RequestSemanticSummary.Empty;
        }
    }

    private static void ValidateImageReferences(byte[] body, string? contentType)
    {
        if (body.Length == 0
            || contentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            ValidateImageReferences(document.RootElement);
        }
        catch (JsonException)
        {
            // llama.cpp remains responsible for ordinary JSON validation.
        }
    }

    private static void ValidateImageReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "input_image", StringComparison.Ordinal)
                && element.TryGetProperty("image_url", out var url)
                && url.ValueKind == JsonValueKind.String)
            {
                var value = url.GetString() ?? string.Empty;
                if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnsafeMediaReferenceException(
                        "Local image input must be embedded as a data:image URL; remote and file URLs are not forwarded.");
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                ValidateImageReferences(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateImageReferences(item);
            }
        }
    }

    private sealed class UnsafeMediaReferenceException(string message) : Exception(message);

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
        }, importantInConcise: true).ConfigureAwait(false);
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
            CopyWithLimit(decoder, output, MaximumDecodedRequestBodyBytes);
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

    private async Task TryWriteDiagnosticAsync(object value, bool importantInConcise)
    {
        var detailed = Volatile.Read(ref _detailedDiagnosticsEnabled) == 1;
        if (!detailed && !importantInConcise)
        {
            return;
        }

        var path = detailed ? _fullDiagnosticLogPath : _conciseDiagnosticLogPath;
        if (path is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("代理日志必须位于一个目录中。");
            Directory.CreateDirectory(directory);
            var record = JsonSerializer.SerializeToNode(value) as JsonObject
                ?? new JsonObject { ["eventName"] = "diagnostic_event" };
            record = DiagnosticSanitizer.SanitizeJsonNode(record) as JsonObject
                ?? new JsonObject { ["eventName"] = "diagnostic_event" };
            record["schemaVersion"] = 2;
            record["eventId"] = Guid.NewGuid().ToString("N");
            record["level"] = ResolveDiagnosticLevel(record);
            record["sessionId"] = _sessionId;
            record["routerRunId"] = Volatile.Read(ref _routerRunId);
            record["diagnosticMode"] = detailed ? "full" : "concise";
            ApplyFailureBurst(record);
            var line = record.ToJsonString() + Environment.NewLine;
            await _diagnosticGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                RotateDiagnosticLogIfNeeded(path);
                var shouldHarden = !string.Equals(_diagnosticLogHardenedPath, path, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path);
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);
                if (shouldHarden)
                {
                    PrivateFilePermissions.HardenFile(path);
                    _diagnosticLogHardenedPath = path;
                }
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
            finally
            {
                _diagnosticGate.Release();
            }
        }
        catch (Exception exception)
        {
            await ReportDiagnosticWriteFailureAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task ReportDiagnosticWriteFailureAsync(Exception exception)
    {
        var now = Environment.TickCount64;
        var previous = Volatile.Read(ref _lastDiagnosticFailureReportAtMilliseconds);
        if ((previous != 0 && now - previous < 30_000)
            || Interlocked.CompareExchange(ref _lastDiagnosticFailureReportAtMilliseconds, now, previous) != previous)
        {
            return;
        }

        try
        {
            if (_diagnosticFailureReporter is not null)
            {
                await _diagnosticFailureReporter(exception).ConfigureAwait(false);
                return;
            }

            Console.Error.WriteLine(
                "Local proxy diagnostic write failed: "
                + DiagnosticSanitizer.SanitizeText(exception.GetType().Name + ": " + exception.Message, 500));
        }
        catch (Exception reporterException)
        {
            try
            {
                Console.Error.WriteLine(
                    "Local proxy diagnostic fallback failed: "
                    + DiagnosticSanitizer.SanitizeText(
                        reporterException.GetType().Name + ": " + reporterException.Message,
                        500));
            }
            catch
            {
                // The inference path must keep running even when both log sinks fail.
            }
        }
    }

    private void ApplyFailureBurst(JsonObject record)
    {
        var eventName = record["eventName"]?.GetValue<string>() ?? string.Empty;
        var outcome = record["outcome"]?.GetValue<string>() ?? string.Empty;
        var statusCode = record["statusCode"]?.GetValue<int?>();
        var isFailure = eventName is "upstream_error" or "safety_proxy_error" or "request_cancelled"
            || (eventName == "response_completed" && !string.Equals(outcome, "success", StringComparison.Ordinal))
            || (statusCode is >= 400 && eventName is "upstream_response" or "websocket_http_fallback");
        if (!isFailure)
        {
            return;
        }

        var signature = string.Join('|',
            eventName,
            record["stage"]?.GetValue<string>() ?? string.Empty,
            statusCode?.ToString() ?? string.Empty,
            record["errorCategory"]?.GetValue<string>() ?? string.Empty,
            record["exceptionType"]?.GetValue<string>() ?? string.Empty,
            record["requestKind"]?.GetValue<string>() ?? string.Empty,
            outcome);
        var signatureHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
        var now = DateTimeOffset.UtcNow;
        lock (_failureBurstGate)
        {
            foreach (var stale in _failureBursts
                         .Where(pair => now - pair.Value.LastSeenUtc > FailureBurstWindow)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _failureBursts.Remove(stale);
            }

            if (!_failureBursts.TryGetValue(signatureHash, out var burst)
                || now - burst.LastSeenUtc > FailureBurstWindow)
            {
                burst = new FailureBurstState(Guid.NewGuid().ToString("N"), now, now, 0);
            }

            burst = burst with { LastSeenUtc = now, RepeatCount = burst.RepeatCount + 1 };
            _failureBursts[signatureHash] = burst;
            while (_failureBursts.Count > MaximumFailureBursts)
            {
                var oldest = _failureBursts.MinBy(pair => pair.Value.LastSeenUtc).Key;
                _failureBursts.Remove(oldest);
            }

            record["failureBurstId"] = burst.BurstId;
            record["failureRepeatCount"] = burst.RepeatCount;
            record["failureFirstSeenUtc"] = burst.FirstSeenUtc;
            record["failureLastSeenUtc"] = burst.LastSeenUtc;
        }
    }

    private static string ResolveDiagnosticLevel(JsonObject record)
    {
        var eventName = record["eventName"]?.GetValue<string>() ?? string.Empty;
        var outcome = record["outcome"]?.GetValue<string>() ?? string.Empty;
        var statusCode = record["statusCode"]?.GetValue<int?>();
        if (eventName is "safety_proxy_error" or "upstream_error"
            || (eventName == "response_completed" && !string.Equals(outcome, "success", StringComparison.Ordinal)))
        {
            return "error";
        }

        if (eventName == "request_cancelled" || statusCode is >= 400)
        {
            return "warning";
        }

        return "info";
    }

    private sealed record FailureBurstState(
        string BurstId,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        int RepeatCount);

    private static void RotateDiagnosticLogIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumDiagnosticLogBytes)
        {
            return;
        }

        var secondBackup = path + ".2";
        var firstBackup = path + ".1";
        if (File.Exists(secondBackup))
        {
            File.Delete(secondBackup);
        }

        if (File.Exists(firstBackup))
        {
            File.Move(firstBackup, secondBackup, overwrite: true);
        }

        File.Move(path, firstBackup, overwrite: true);
    }

    private static string SafeEndpoint(Uri? uri) => uri is null
        ? string.Empty
        : $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";

    private static string SafeExceptionMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        message = Regex.Replace(message, @"(?i)\bBearer\s+[^\s,;]+", "Bearer <redacted>");
        message = Regex.Replace(message, @"(?i)(?:[A-Z]:\\|\\\\)\S+", "<path>");
        message = Regex.Replace(message, @"https?://\S+", "<url>");
        message = Regex.Replace(message, @"(?i)\b(prompt|input|content|text)\s*[:=]\s*[^,;]+", "$1=<redacted>");
        message = Regex.Replace(message, @"\b[A-Za-z0-9_+/.=-]{40,}\b", "<value>");
        return message.Length <= 500 ? message : message[..500];
    }

    private static string SafeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        return MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            ? parsed.MediaType ?? string.Empty
            : string.Empty;
    }

    private static string? ReadSafeUpstreamRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "x-request-id", "request-id" })
        {
            if (!response.Headers.TryGetValues(name, out var values))
            {
                continue;
            }

            var value = values.FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                continue;
            }

            if (value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':'))
            {
                return value;
            }
        }

        return null;
    }

    private static string SafeDiagnosticCategory(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return "unclassified_upstream_error";
            }

            var code = error.TryGetProperty("code", out var codeElement)
                ? codeElement.ToString().ToLowerInvariant()
                : string.Empty;
            var type = error.TryGetProperty("type", out var typeElement)
                ? typeElement.ToString().ToLowerInvariant()
                : string.Empty;
            var message = error.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()?.ToLowerInvariant() ?? string.Empty
                    : string.Empty;
            var diagnostic = $"{code} {type} {message}";

            if (message.Contains("jinja", StringComparison.Ordinal)
                && message.Contains("system message must be at the beginning.", StringComparison.Ordinal))
                return "chat_template_message_order";

            if (diagnostic.Contains("exceed_context_size", StringComparison.Ordinal)
                || diagnostic.Contains("context size", StringComparison.Ordinal)
                || diagnostic.Contains("context length", StringComparison.Ordinal))
            {
                return "context_overflow";
            }

            if (diagnostic.Contains("device lost", StringComparison.Ordinal))
            {
                return "device_lost";
            }

            if (diagnostic.Contains("vulkan", StringComparison.Ordinal))
            {
                return "vulkan_backend_error";
            }

            if (diagnostic.Contains("cuda", StringComparison.Ordinal))
            {
                return "cuda_backend_error";
            }

            if (diagnostic.Contains("out of memory", StringComparison.Ordinal)
                || diagnostic.Contains("allocation", StringComparison.Ordinal))
            {
                return "allocation_failure";
            }

            if (diagnostic.Contains("cancel", StringComparison.Ordinal))
            {
                return "upstream_cancelled";
            }

            if (type.Contains("invalid_request", StringComparison.Ordinal))
            {
                return "invalid_request";
            }

            if (type.Contains("server_error", StringComparison.Ordinal))
            {
                return "upstream_server_error";
            }
        }
        catch (JsonException)
        {
            return "unstructured_upstream_error";
        }

        return "unclassified_upstream_error";
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

            var code = error.TryGetProperty("code", out var codeElement)
                ? SafeDiagnosticToken(codeElement.ToString())
                : "unknown";
            var type = error.TryGetProperty("type", out var typeElement)
                ? SafeDiagnosticToken(typeElement.ToString())
                : "unknown";
            return $"code={code}, type={type}";
        }
        catch
        {
            return "non-JSON error body";
        }
    }

    private static string SafeDiagnosticToken(string value)
    {
        var safe = new string(value
            .Take(80)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            .ToArray());
        return string.IsNullOrEmpty(safe) ? "unknown" : safe;
    }

    private sealed record RequestCopySummary(
        int OriginalBodyBytes,
        int ForwardedBodyBytes,
        bool Decoded,
        RequestSemanticSummary Semantics);

    private sealed record RequestSemanticSummary(
        IReadOnlyList<string> InputItemTypes,
        IReadOnlyList<string> ToolTypes,
        bool HasCompactionTrigger, string? ReasoningEffort = null)
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

    private sealed class SseEventTracker
    {
        private const int MaximumEventLineBytes = 512;
        private readonly byte[] _line = new byte[MaximumEventLineBytes];
        private int _lineLength;
        private bool _lineOverflowed;

        public int EventCount { get; private set; }

        public string? LastEvent { get; private set; }

        public string? TerminalEvent { get; private set; }

        public long? LastEventElapsedMs { get; private set; }

        public long? TerminalEventElapsedMs { get; private set; }

        public void Observe(ReadOnlySpan<byte> bytes, long elapsedMs)
        {
            foreach (var value in bytes)
            {
                if (value == (byte)'\n')
                {
                    ProcessLine(elapsedMs);
                    _lineLength = 0;
                    _lineOverflowed = false;
                    continue;
                }

                if (_lineLength < MaximumEventLineBytes)
                {
                    _line[_lineLength++] = value;
                }
                else
                {
                    _lineOverflowed = true;
                }
            }
        }

        public void Complete(long elapsedMs) => ProcessLine(elapsedMs);

        private void ProcessLine(long elapsedMs)
        {
            if (_lineOverflowed || _lineLength == 0)
            {
                return;
            }

            var line = Encoding.ASCII.GetString(_line, 0, _lineLength).TrimEnd('\r');
            string? eventName = null;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = SafeDiagnosticToken(line[6..].Trim());
            }
            else if (string.Equals(line, "data: [DONE]", StringComparison.Ordinal))
            {
                eventName = "done";
            }

            if (eventName is null)
            {
                return;
            }

            EventCount++;
            LastEvent = eventName;
            LastEventElapsedMs = elapsedMs;
            if (eventName is "response.completed" or "response.failed" or "response.incomplete" or "done")
            {
                TerminalEvent = eventName;
                TerminalEventElapsedMs = elapsedMs;
            }
        }
    }
}
