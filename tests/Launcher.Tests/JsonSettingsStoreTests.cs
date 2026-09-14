using Launcher.Core.Configuration;
using Launcher.Core.Persistence;

namespace Launcher.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSelectedModeAndModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            using var store = new JsonSettingsStore(path);
            var expected = new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "model-a",
                LlamaRoot = @"D:\llama.cpp",
            };

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(expected, actual);
            Assert.Contains("\"SelectedMode\": \"Local\"", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_WhenReplacingSettings_KeepsPreviousBackup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            using var store = new JsonSettingsStore(path);

            await store.SaveAsync(new LauncherSettings { SelectedMode = ProviderMode.OpenAI });
            await store.SaveAsync(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "model-a",
                LlamaRoot = @"D:\llama.cpp",
            });

            Assert.True(File.Exists(path + ".bak"));
            Assert.Contains("\"SelectedMode\": \"OpenAI\"", await File.ReadAllTextAsync(path + ".bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAndSave_WhenLegacyApprovalSettingExists_DropsObsoleteProperty()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "SelectedMode": "OpenAI",
                  "RouterPort": 8080,
                  "LocalApprovalReviewer": "AutoReview",
                  "StartAgentAtLogin": false
                }
                """);

            using var store = new JsonSettingsStore(path);
            var settings = await store.LoadAsync();
            await store.SaveAsync(settings);

            Assert.Equal(ProviderMode.OpenAI, settings.SelectedMode);
            Assert.DoesNotContain("LocalApprovalReviewer", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WhenMainFileIsInvalid_ReportsExistingBackupWithoutApplyingIt()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            using var store = new JsonSettingsStore(path);
            await store.SaveAsync(new LauncherSettings { SelectedMode = ProviderMode.OpenAI });
            await store.SaveAsync(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "model-a",
                LlamaRoot = @"D:\llama.cpp",
            });
            await File.WriteAllTextAsync(path, "{ invalid-json }");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

            Assert.Contains(".bak", exception.Message, StringComparison.Ordinal);
            Assert.Contains("不会自动套用", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
