using System.Text.Json;

namespace Launcher.ChatGPT.Catalog;

public static class LocalModelCatalogValidator
{
    public static async Task ValidateSingleModelAsync(
        string catalogPath,
        string expectedSlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSlug);

        await using var stream = new FileStream(
            catalogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array
            || models.GetArrayLength() != 1)
        {
            throw new InvalidDataException("Local Catalog 必须且只能包含一个模型。");
        }

        var model = models[0];
        if (!model.TryGetProperty("slug", out var slug)
            || slug.ValueKind != JsonValueKind.String
            || !string.Equals(slug.GetString(), expectedSlug, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Local Catalog 的唯一模型与当前选择不匹配。");
        }

        if (!model.TryGetProperty("supported_in_api", out var supportedInApi)
            || supportedInApi.ValueKind is not JsonValueKind.True)
        {
            throw new InvalidDataException("Local Catalog 模型必须设置 supported_in_api = true。");
        }

        if (!model.TryGetProperty("truncation_policy", out var truncationPolicy)
            || truncationPolicy.ValueKind != JsonValueKind.Object
            || !truncationPolicy.TryGetProperty("mode", out var truncationMode)
            || truncationMode.ValueKind != JsonValueKind.String
            || !string.Equals(truncationMode.GetString(), "tokens", StringComparison.Ordinal)
            || !truncationPolicy.TryGetProperty("limit", out var truncationLimit)
            || truncationLimit.ValueKind != JsonValueKind.Number
            || !truncationLimit.TryGetInt32(out var tokenLimit)
            || tokenLimit < 1024)
        {
            throw new InvalidDataException("Local Catalog 缺少当前 Codex 所需的 token truncation_policy。");
        }

        foreach (var requiredProperty in new[]
        {
            "base_instructions",
            "supported_reasoning_levels",
            "availability_nux",
            "upgrade",
            "default_verbosity",
            "apply_patch_tool_type",
            "experimental_supported_tools",
        })
        {
            if (!model.TryGetProperty(requiredProperty, out _))
            {
                throw new InvalidDataException($"Local Catalog 缺少当前 Codex 所需字段：{requiredProperty}。");
            }
        }
    }
}
