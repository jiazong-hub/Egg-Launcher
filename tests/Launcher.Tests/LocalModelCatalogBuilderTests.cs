using System.Text.Json;
using Launcher.ChatGPT.Catalog;

namespace Launcher.Tests;

public sealed class LocalModelCatalogBuilderTests
{
    [Fact]
    public void BuildJson_ContainsExactlyOneLocalModel()
    {
        var json = LocalModelCatalogBuilder.BuildJson(CreateOptions());
        using var document = JsonDocument.Parse(json);

        var models = document.RootElement.GetProperty("models");
        var model = Assert.Single(models.EnumerateArray());

        Assert.Equal("local/coder-q4", model.GetProperty("slug").GetString());
        Assert.True(model.GetProperty("supported_in_api").GetBoolean());
        Assert.Equal("unified_exec", model.GetProperty("shell_type").GetString());
        Assert.Equal("本地 llama.cpp 模型", model.GetProperty("description").GetString());
        Assert.False(model.TryGetProperty("default_reasoning_level", out _));
        Assert.Empty(model.GetProperty("supported_reasoning_levels").EnumerateArray());
        Assert.Equal(8192, model.GetProperty("context_window").GetInt32());
        Assert.Equal("tokens", model.GetProperty("truncation_policy").GetProperty("mode").GetString());
        Assert.Equal(8192, model.GetProperty("truncation_policy").GetProperty("limit").GetInt32());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("availability_nux").ValueKind);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("upgrade").ValueKind);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("default_verbosity").ValueKind);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("apply_patch_tool_type").ValueKind);
        Assert.Empty(model.GetProperty("experimental_supported_tools").EnumerateArray());
        Assert.Equal(string.Empty, model.GetProperty("base_instructions").GetString());
        Assert.False(model.TryGetProperty("auto_compact_token_limit", out _));
        Assert.False(model.TryGetProperty("effective_context_window_percent", out _));
        Assert.False(model.TryGetProperty("supports_parallel_tool_calls", out _));
        Assert.False(model.GetProperty("include_skills_usage_instructions").GetBoolean());
        Assert.False(model.GetProperty("include_plugin_usage_instructions").GetBoolean());
        Assert.False(model.GetProperty("include_apps_usage_instructions").GetBoolean());
        Assert.False(model.GetProperty("supports_search_tool").GetBoolean());
        Assert.False(model.TryGetProperty("web_search_tool_type", out _));
    }

    [Fact]
    public async Task WriteAndValidate_RoundTripsSingleModelCatalog()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "local-models.json");
            await LocalModelCatalogBuilder.WriteAtomicallyAsync(path, CreateOptions());

            await LocalModelCatalogValidator.ValidateSingleModelAsync(path, "local/coder-q4");
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateSingleModelAsync_RejectsMultipleModels()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(
                path,
                "{\"models\":[{\"slug\":\"a\",\"supported_in_api\":true},{\"slug\":\"b\",\"supported_in_api\":true}]}");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LocalModelCatalogValidator.ValidateSingleModelAsync(path, "a"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateSingleModelAsync_RejectsLegacyCatalogWithoutTruncationPolicy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "legacy.json");
            await File.WriteAllTextAsync(
                path,
                "{\"models\":[{\"slug\":\"a\",\"supported_in_api\":true}]}");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                LocalModelCatalogValidator.ValidateSingleModelAsync(path, "a"));

            Assert.Contains("truncation_policy", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalModelCatalogOptions CreateOptions() => new()
    {
        Slug = "local/coder-q4",
        DisplayName = "Coder Q4",
        ContextWindow = 8192,
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
