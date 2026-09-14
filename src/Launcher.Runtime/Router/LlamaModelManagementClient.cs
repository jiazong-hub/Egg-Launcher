using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Runtime.Router;

public sealed partial class LlamaModelManagementClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<LlamaModelRuntimeInfo>> ListAsync(
        Uri baseUri,
        bool reload = false,
        CancellationToken cancellationToken = default)
    {
        ValidateBaseUri(baseUri);
        using var response = await httpClient.GetAsync(
            new Uri(baseUri, reload ? "models?reload=1" : "models"),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "读取模型列表", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LlamaModelRuntimeInfo>();
        }

        return data.EnumerateArray().Select(ParseModel).Where(model => model is not null).Cast<LlamaModelRuntimeInfo>().ToArray();
    }

    public Task<RouterModelActionResult> LoadAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default) =>
        SendModelActionAsync(HttpMethod.Post, baseUri, "models/load", modelId, "加载", cancellationToken);

    public Task<RouterModelActionResult> UnloadAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default) =>
        SendModelActionAsync(HttpMethod.Post, baseUri, "models/unload", modelId, "释放", cancellationToken);

    public Task<RouterModelActionResult> RemoveCachedAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default) =>
        SendModelActionAsync(HttpMethod.Delete, baseUri, "models?model=" + Uri.EscapeDataString(ValidateModelId(modelId)), modelId, "删除缓存", cancellationToken);

    private async Task<RouterModelActionResult> SendModelActionAsync(
        HttpMethod method,
        Uri baseUri,
        string relativePath,
        string modelId,
        string operation,
        CancellationToken cancellationToken)
    {
        ValidateBaseUri(baseUri);
        modelId = ValidateModelId(modelId);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
            if (method != HttpMethod.Delete)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { model = modelId }),
                    Encoding.UTF8,
                    "application/json");
            }

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return new RouterModelActionResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                response.IsSuccessStatusCode ? null : $"llama.cpp {operation}模型失败，HTTP {(int)response.StatusCode}。");
        }
        catch (HttpRequestException exception)
        {
            return new RouterModelActionResult(false, null, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RouterModelActionResult(false, null, $"llama.cpp {operation}模型请求超时。");
        }
    }

    private static LlamaModelRuntimeInfo? ParseModel(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !ReadString(value, "id", out var id) || !SafeModelIdRegex().IsMatch(id))
        {
            return null;
        }

        var status = value.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.Object
            ? statusNode
            : default;
        var architecture = value.TryGetProperty("architecture", out var architectureNode)
            ? architectureNode
            : default;
        return new LlamaModelRuntimeInfo(
            id,
            ReadOptionalString(value, "path"),
            ReadBoolean(value, "in_cache"),
            ReadBoolean(value, "can_remove"),
            ReadOptionalString(value, "source") ?? "unknown",
            status.ValueKind == JsonValueKind.Object ? ReadOptionalString(status, "value") ?? "unknown" : "unknown",
            status.ValueKind == JsonValueKind.Object && ReadBoolean(status, "failed"),
            status.ValueKind == JsonValueKind.Object ? ReadInt32(status, "exit_code") : null,
            ReadInt64(value, "size") ?? ReadNestedInt64(value, "meta", "size"),
            ReadInt64(value, "n_params") ?? ReadNestedInt64(value, "meta", "n_params"),
            ReadInt32(value, "n_ctx_train") ?? ReadNestedInt32(value, "meta", "n_ctx_train"),
            ReadStrings(architecture, "input_modalities"),
            ReadStrings(architecture, "output_modalities"));
    }

    public static void ValidateBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !IPAddress.TryParse(baseUri.Host, out var address)
            || !IPAddress.IsLoopback(address)
            || baseUri.Port is < 1 or > 65535)
        {
            throw new ArgumentException("llama.cpp 管理端点必须是 HTTP 回环地址。", nameof(baseUri));
        }
    }

    private static string ValidateModelId(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (modelId.Length > 512 || !SafeModelIdRegex().IsMatch(modelId))
        {
            throw new ArgumentException("模型 ID 包含不支持的字符。", nameof(modelId));
        }

        return modelId;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"{operation}失败，HTTP {(int)response.StatusCode}。", null, response.StatusCode);
    }

    private static bool ReadString(JsonElement element, string name, out string value)
    {
        value = ReadOptionalString(element, name) ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static int? ReadInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? ReadInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var result)
            ? result
            : null;

    private static int? ReadNestedInt32(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested) ? ReadInt32(nested, name) : null;

    private static long? ReadNestedInt64(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested) ? ReadInt64(nested, name) : null;

    private static string[] ReadStrings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var values)
        && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Take(16)
                .ToArray()
            : Array.Empty<string>();

    [GeneratedRegex("^[A-Za-z0-9._:/+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeModelIdRegex();
}
