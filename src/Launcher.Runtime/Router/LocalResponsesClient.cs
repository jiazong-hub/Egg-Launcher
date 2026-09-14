using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Launcher.Runtime.Router;

public sealed class LocalResponsesClient(HttpClient httpClient)
{
    public async Task<ResponsesInferenceResult> CreateAsync(
        Uri baseUri,
        string model,
        string input,
        int maxOutputTokens = 32,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(baseUri, model, input, maxOutputTokens);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model,
                input,
                max_output_tokens = maxOutputTokens,
                stream = false,
                store = false,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/responses"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ResponsesInferenceResult(
                    false,
                    (int)response.StatusCode,
                    null,
                    model,
                    null,
                    null,
                    null,
                    Truncate(responseText, 1000),
                    stopwatch.Elapsed);
            }

            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var responseId = ReadString(root, "id");
            var responseModel = ReadString(root, "model") ?? model;
            var responseStatus = ReadString(root, "status");
            var incompleteReason = ReadNestedString(root, "incomplete_details", "reason");
            var outputText = ExtractOutputText(root);
            return new ResponsesInferenceResult(
                !string.IsNullOrWhiteSpace(outputText),
                (int)response.StatusCode,
                responseId,
                responseModel,
                responseStatus,
                incompleteReason,
                outputText,
                string.IsNullOrWhiteSpace(outputText)
                    ? $"Responses API 没有 output_text；status={responseStatus ?? "<none>"}，incomplete_reason={incompleteReason ?? "<none>"}。响应：{Truncate(responseText, 3000)}"
                    : null,
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return new ResponsesInferenceResult(false, null, null, model, null, null, null, exception.Message, stopwatch.Elapsed);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ResponsesInferenceResult(false, null, null, model, null, null, null, "Responses 推理请求超时。", stopwatch.Elapsed);
        }
        catch (JsonException exception)
        {
            stopwatch.Stop();
            return new ResponsesInferenceResult(false, null, null, model, null, null, null, $"Responses JSON 无效：{exception.Message}", stopwatch.Elapsed);
        }
    }

    private static void ValidateRequest(Uri baseUri, string model, string input, int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (!baseUri.IsAbsoluteUri
            || baseUri.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(baseUri.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("本地 Responses 测试只允许 HTTP 回环地址。", nameof(baseUri));
        }

        if (maxOutputTokens is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "Smoke Test 输出必须限制在 1 到 1,024 tokens。 ");
        }
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var directText) && directText.ValueKind == JsonValueKind.String)
        {
            return directText.GetString();
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var pieces = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "output_text", StringComparison.Ordinal)
                    && part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    pieces.Add(text.GetString()!);
                }
            }
        }

        return pieces.Count == 0 ? null : string.Concat(pieces);
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement root, string objectName, string propertyName) =>
        root.TryGetProperty(objectName, out var parent)
        && parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";
}
