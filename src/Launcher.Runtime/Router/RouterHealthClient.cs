using System.Text;
using System.Text.Json;

namespace Launcher.Runtime.Router;

public sealed class RouterHealthClient(HttpClient httpClient) : IRouterControlClient
{
    private const int MaximumRouteProbeResponseBytes = 64 * 1024;

    public async Task<ResponsesApiRouteProbeResult> ProbeResponsesRouteAsync(
        Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/responses"))
            {
                // Deliberately omit both `model` and `input`. A compatible llama.cpp build rejects
                // the request during validation, before it has a reason to load model weights.
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            var hasExpectedJsonShape = await HasExpectedResponsesShapeAsync(response, cancellationToken)
                .ConfigureAwait(false);
            var isAvailable = response.IsSuccessStatusCode
                ? hasExpectedJsonShape
                : statusCode is 400 or 422 && hasExpectedJsonShape;
            return new ResponsesApiRouteProbeResult(
                isAvailable,
                statusCode,
                isAvailable
                    ? null
                    : statusCode is 404 or 405 or 501
                        ? "Router 未暴露可用的 POST /v1/responses 路由。"
                        : "Responses API 路由未返回可识别的 OpenAI 兼容 JSON。"
            );
        }
        catch (HttpRequestException exception)
        {
            return new ResponsesApiRouteProbeResult(false, null, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ResponsesApiRouteProbeResult(false, null, "Responses API 路由探测超时。");
        }
    }

    private static async Task<bool> HasExpectedResponsesShapeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumRouteProbeResponseBytes)
            {
                return false;
            }

            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            return false;
        }

        buffer.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (response.IsSuccessStatusCode)
            {
                return root.TryGetProperty("id", out _)
                    || root.TryGetProperty("object", out _)
                    || root.TryGetProperty("output", out _);
            }

            return root.TryGetProperty("error", out var error)
                && error.ValueKind is JsonValueKind.Object or JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<RouterHealthSnapshot> ProbeAsync(
        Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        try
        {
            using var healthResponse = await httpClient.GetAsync(
                new Uri(baseUri, "health"),
                cancellationToken).ConfigureAwait(false);
            using var modelsResponse = await httpClient.GetAsync(
                new Uri(baseUri, "models"),
                cancellationToken).ConfigureAwait(false);

            var modelIds = modelsResponse.IsSuccessStatusCode
                ? await ReadModelIdsAsync(modelsResponse, cancellationToken).ConfigureAwait(false)
                : Array.Empty<string>();
            var isHealthy = healthResponse.IsSuccessStatusCode && modelsResponse.IsSuccessStatusCode;

            return new RouterHealthSnapshot(
                isHealthy,
                (int)healthResponse.StatusCode,
                (int)modelsResponse.StatusCode,
                modelIds,
                isHealthy ? null : "Router health 或 models 端点尚未就绪。");
        }
        catch (HttpRequestException exception)
        {
            return new RouterHealthSnapshot(false, null, null, Array.Empty<string>(), exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RouterHealthSnapshot(false, null, null, Array.Empty<string>(), "Router 请求超时。");
        }
    }

    public async Task<RouterModelActionResult> UnloadModelAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        try
        {
            var json = JsonSerializer.Serialize(new { model = modelId });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "models/unload"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            return new RouterModelActionResult(
                response.IsSuccessStatusCode,
                statusCode,
                response.IsSuccessStatusCode ? null : $"Router 卸载模型失败，HTTP {statusCode}。");
        }
        catch (HttpRequestException exception)
        {
            return new RouterModelActionResult(false, null, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RouterModelActionResult(false, null, "Router 卸载模型请求超时。");
        }
    }

    public async Task<RouterHealthSnapshot> WaitUntilReadyAsync(
        Uri baseUri,
        TimeSpan timeout,
        Func<bool>? hasExited = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        RouterHealthSnapshot? lastSnapshot = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasExited?.Invoke() == true)
            {
                throw new InvalidOperationException("Router 在健康检查完成前已经退出。");
            }

            lastSnapshot = await ProbeAsync(baseUri, cancellationToken).ConfigureAwait(false);
            if (lastSnapshot.IsHealthy)
            {
                return lastSnapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Router 未在 {timeout.TotalSeconds:0.#} 秒内就绪。最后状态：{lastSnapshot?.Diagnostic ?? "无响应"}");
    }

    private static async Task<string[]> ReadModelIdsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (TryReadIds(root, "data", out var openAiIds))
        {
            return openAiIds;
        }

        return TryReadIds(root, "models", out var modelIds) ? modelIds : Array.Empty<string>();
    }

    private static bool TryReadIds(JsonElement root, string propertyName, out string[] ids)
    {
        ids = Array.Empty<string>();
        if (!root.TryGetProperty(propertyName, out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        ids = models.EnumerateArray()
            .Select(model =>
            {
                if (model.ValueKind == JsonValueKind.String)
                {
                    return model.GetString();
                }

                foreach (var key in new[] { "id", "slug", "name" })
                {
                    if (model.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }

                return null;
            })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
        return true;
    }
}
