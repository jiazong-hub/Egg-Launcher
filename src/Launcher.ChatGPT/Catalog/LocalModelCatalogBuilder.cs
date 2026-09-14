using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Launcher.ChatGPT.Catalog;

public static partial class LocalModelCatalogBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BuildJson(LocalModelCatalogOptions options)
    {
        Validate(options);

        var model = new JsonObject
        {
            ["slug"] = options.Slug,
            ["display_name"] = options.DisplayName,
            ["description"] = options.Description,
            // Current Codex requires either base_instructions or an instruction template.
            // Keep this empty so the launcher does not invent model behavior.
            ["base_instructions"] = string.Empty,
            // Do not advertise a launcher-chosen reasoning mode. An empty list tells
            // Codex that this local endpoint has no client-selectable reasoning preset;
            // llama.cpp and the model template remain responsible for model behavior.
            ["supported_reasoning_levels"] = new JsonArray(),
            ["shell_type"] = "unified_exec",
            ["visibility"] = "list",
            ["supported_in_api"] = true,
            ["priority"] = 0,
            ["availability_nux"] = null,
            ["upgrade"] = null,
            ["include_skills_usage_instructions"] = false,
            ["include_plugin_usage_instructions"] = false,
            ["include_apps_usage_instructions"] = false,
            ["supports_reasoning_summary_parameter"] = false,
            ["default_reasoning_summary"] = "none",
            ["support_verbosity"] = false,
            ["default_verbosity"] = null,
            ["apply_patch_tool_type"] = null,
            ["truncation_policy"] = new JsonObject
            {
                ["mode"] = "tokens",
                ["limit"] = options.ContextWindow,
            },
            ["experimental_supported_tools"] = new JsonArray(),
            ["supports_image_detail_original"] = false,
            ["context_window"] = options.ContextWindow,
            ["max_context_window"] = options.ContextWindow,
            ["input_modalities"] = new JsonArray("text"),
            ["supports_search_tool"] = false,
        };

        var catalog = new JsonObject
        {
            ["models"] = new JsonArray(model),
        };

        return catalog.ToJsonString(SerializerOptions) + "\n";
    }

    public static async Task WriteAtomicallyAsync(
        string path,
        LocalModelCatalogOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Catalog 必须位于一个目录中。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, BuildJson(options), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original catalog write result; temp files are never referenced.
            }
        }
    }

    private static void Validate(LocalModelCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Slug) || !SafeSlugRegex().IsMatch(options.Slug))
        {
            throw new ArgumentException("Model slug 包含不支持的字符。", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.DisplayName);
        if (options.ContextWindow < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Catalog Context Window 不能小于 1024。");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSlugRegex();
}
