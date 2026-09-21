using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Launcher.Runtime.Router;

public sealed class RouterModelDownloadClient
{
    private static readonly TimeSpan DefaultControlRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultEventInactivityTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _controlRequestTimeout;
    private readonly TimeSpan _eventInactivityTimeout;

    public RouterModelDownloadClient(
        HttpClient httpClient,
        TimeSpan? controlRequestTimeout = null,
        TimeSpan? eventInactivityTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _controlRequestTimeout = ValidateTimeout(
            controlRequestTimeout ?? DefaultControlRequestTimeout,
            nameof(controlRequestTimeout));
        _eventInactivityTimeout = ValidateTimeout(
            eventInactivityTimeout ?? DefaultEventInactivityTimeout,
            nameof(eventInactivityTimeout));
    }

    public async Task<RouterModelDownloadResult> DownloadAsync(
        Uri baseUri,
        string modelId,
        IProgress<RouterModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<RouterModelDownloadActivity>? activity = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ValidateModelId(modelId);

        var existing = await FindModelAsync(baseUri, modelId, reload: false, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new RouterModelDownloadResult(
                modelId,
                existing.Path,
                WasAlreadyCached: true,
                RegistrationObserved: true);
        }

        using var streamRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "models/sse"));
        using var streamResponse = await SendWithTimeoutAsync(
            streamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            "连接 llama.cpp 下载状态流超时。",
            cancellationToken).ConfigureAwait(false);
        streamResponse.EnsureSuccessStatusCode();
        activity?.Report(new RouterModelDownloadActivity(
            RouterModelDownloadPhase.StatusStreamConnected,
            "已连接 llama.cpp 原生下载状态流。"));

        await using var eventStream = await streamResponse.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(eventStream, Encoding.UTF8);

        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readEventsTask = ReadDownloadEventsAsync(
            reader,
            modelId,
            progress,
            activity,
            _eventInactivityTimeout,
            eventCancellation.Token);
        try
        {
            using var startRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "models"))
            {
                Content = JsonContent.Create(new { model = modelId }),
            };
            using var response = await SendWithTimeoutAsync(
                startRequest,
                HttpCompletionOption.ResponseContentRead,
                "llama.cpp 接受下载请求超时。",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(FormatRouterError(response.StatusCode, body));
            }

            activity?.Report(new RouterModelDownloadActivity(
                RouterModelDownloadPhase.RequestAccepted,
                "llama.cpp 已接受请求，正在解析仓库并等待首个下载字节。"));

            var completed = await readEventsTask.ConfigureAwait(false);
            if (!completed)
            {
                throw new InvalidOperationException("llama.cpp 报告模型下载失败，请查看下载 Router 日志。");
            }

            activity?.Report(new RouterModelDownloadActivity(
                RouterModelDownloadPhase.VerifyingCache,
                "llama.cpp 已结束下载事件，正在核对原生缓存登记与模型文件。"));
            var registration = await FindModelAsync(
                    baseUri,
                    modelId,
                    reload: true,
                    cancellationToken)
                .ConfigureAwait(false);
            return new RouterModelDownloadResult(
                modelId,
                registration?.Path,
                WasAlreadyCached: false,
                RegistrationObserved: registration is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            eventCancellation.Cancel();
            await TryCancelAsync(baseUri, modelId).ConfigureAwait(false);
            await IgnoreCancelledEventStreamAsync(readEventsTask).ConfigureAwait(false);
            throw;
        }
        catch
        {
            eventCancellation.Cancel();
            await IgnoreCancelledEventStreamAsync(readEventsTask).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<RouterModelRegistration?> FindModelAsync(
        Uri baseUri,
        string modelId,
        bool reload,
        CancellationToken cancellationToken)
    {
        var relative = reload ? "models?reload=1" : "models";
        using var response = await _httpClient.GetAsync(new Uri(baseUri, relative), cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var model in models.EnumerateArray())
        {
            if (!string.Equals(ReadString(model, "id"), modelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = ReadString(model, "path");
            return new RouterModelRegistration(
                string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : Path.GetFullPath(path),
                ReadString(model, "source"));
        }

        return null;
    }

    private static async Task<bool> ReadDownloadEventsAsync(
        StreamReader reader,
        string modelId,
        IProgress<RouterModelDownloadProgress>? progress,
        IProgress<RouterModelDownloadActivity>? activity,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        var relevantEventDeadline = DateTimeOffset.UtcNow + inactivityTimeout;
        while (true)
        {
            var remaining = relevantEventDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw DownloadEventTimeout(inactivityTimeout);
            }

            string? line;
            using (var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readCancellation.CancelAfter(remaining);
                try
                {
                    line = await reader.ReadLineAsync(readCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw DownloadEventTimeout(inactivityTimeout);
                }
            }

            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var json = line["data:".Length..].Trim();
            if (json.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!string.Equals(ReadString(root, "model"), modelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eventName = ReadString(root, "event");
            relevantEventDeadline = DateTimeOffset.UtcNow + inactivityTimeout;
            if (string.Equals(eventName, "download_finished", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(eventName, "download_failed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(eventName, "download_progress", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var progressElement = data.TryGetProperty("progress", out var nested)
                                  && nested.ValueKind == JsonValueKind.Object
                ? nested
                : data;
            var files = new List<RouterFileDownloadProgress>();
            foreach (var property in progressElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var done = Math.Max(0, ReadInt64(property.Value, "done"));
                var total = Math.Max(0, ReadInt64(property.Value, "total"));
                files.Add(new RouterFileDownloadProgress(property.Name, done, total));
            }

            if (files.Count > 0)
            {
                activity?.Report(new RouterModelDownloadActivity(
                    RouterModelDownloadPhase.Transferring,
                    "llama.cpp 正在下载 GGUF 文件。"));
                progress?.Report(new RouterModelDownloadProgress(modelId, files));
            }
        }

        throw new IOException("llama.cpp 下载状态流意外结束。");
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_controlRequestTimeout);
        try
        {
            return await _httpClient.SendAsync(request, completionOption, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage, exception);
        }
    }

    private async Task TryCancelAsync(Uri baseUri, string modelId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _httpClient.PostAsJsonAsync(
                    new Uri(baseUri, "models/unload"),
                    new { model = modelId },
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // The owned router process is stopped by the caller as a final cancellation boundary.
        }
    }

    private static async Task IgnoreCancelledEventStreamAsync(Task<bool> readEventsTask)
    {
        try
        {
            await readEventsTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static string FormatRouterError(System.Net.HttpStatusCode statusCode, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return $"llama.cpp 无法开始下载：{message}";
                }
            }
        }
        catch (JsonException)
        {
        }

        return $"llama.cpp 无法开始下载，HTTP {(int)statusCode}。";
    }

    private static TimeoutException DownloadEventTimeout(TimeSpan timeout) => new(
        $"llama.cpp 在 {timeout.TotalSeconds:0} 秒内没有返回当前模型的下载状态；"
        + "已停止等待，避免界面无限停留在 0%。");

    private static TimeSpan ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(parameterName, "下载超时必须大于 0 且不超过 1 小时。");
        }

        return value;
    }

    private static void ValidateModelId(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (modelId.Length > 320
            || modelId.IndexOfAny(['\r', '\n', '\0']) >= 0
            || modelId.Count(character => character == '/') != 1)
        {
            throw new ArgumentException("远程模型标识无效。", nameof(modelId));
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : 0;
}

public sealed record RouterModelDownloadResult(
    string ModelId,
    string? ModelPath,
    bool WasAlreadyCached,
    bool RegistrationObserved = true);

internal sealed record RouterModelRegistration(string? Path, string? Source);

public sealed record RouterFileDownloadProgress(
    string Source,
    long DownloadedBytes,
    long TotalBytes)
{
    public string FileName
    {
        get
        {
            if (Uri.TryCreate(Source, UriKind.Absolute, out var uri))
            {
                return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            }

            return Path.GetFileName(Source);
        }
    }
}

public sealed record RouterModelDownloadProgress(
    string ModelId,
    IReadOnlyList<RouterFileDownloadProgress> Files)
{
    public long DownloadedBytes => Files.Sum(file => file.DownloadedBytes);

    public long TotalBytes => Files.Sum(file => file.TotalBytes);

    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
}

public enum RouterModelDownloadPhase
{
    StatusStreamConnected,
    RequestAccepted,
    Transferring,
    VerifyingCache,
}

public sealed record RouterModelDownloadActivity(
    RouterModelDownloadPhase Phase,
    string Message);
