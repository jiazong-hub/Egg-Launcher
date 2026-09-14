using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Orchestration.ModeSwitch;
using System.Text.Json;

namespace Launcher.Tests;

public sealed class ModeRecoveryCoordinatorTests
{
    [Fact]
    public async Task Recover_WhenLocalConfigWasWrittenBeforeSettings_CommitsTargetSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            await File.WriteAllTextAsync(configPath, "model = \"official\"\n");
            await WriteCatalogAsync(catalogPath, "local-a");
            var original = new LauncherSettings { LlamaRoot = runtimeRoot };
            var target = original with
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-a",
            };
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(original);
            var detector = new FixedClientDetector(false);
            var service = new ChatGptConfigTransactionService(detector);
            await service.ApplyLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-a",
                    ModelCatalogPath = catalogPath,
                    OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                    OriginalLauncherSettings = original,
                    TargetLauncherSettings = target,
                },
                paths);

            var recovered = await new ModeRecoveryCoordinator(settingsStore, service, paths).RecoverAsync();

            Assert.True(recovered.Changed);
            Assert.Equal(target, await settingsStore.LoadAsync());
            Assert.True(recovered.Snapshot!.SettingsCommitted);
            Assert.Equal(ConfigTransactionStage.LocalApplied, recovered.Snapshot.Stage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_WhenOpenAiRestoreWasPrepared_CompletesConfigAndSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            await File.WriteAllTextAsync(configPath, "model = \"official\"\n");
            await WriteCatalogAsync(catalogPath, "local-a");
            var openAi = new LauncherSettings { LlamaRoot = runtimeRoot };
            var local = openAi with { SelectedMode = ProviderMode.Local, SelectedModelId = "local-a" };
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(local);
            var detector = new FixedClientDetector(false);
            var service = new ChatGptConfigTransactionService(detector);
            await service.ApplyLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-a",
                    ModelCatalogPath = catalogPath,
                    OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                },
                paths);
            await service.PrepareOpenAiRestoreAsync(paths.RecoveryFile, local, openAi);

            var recovered = await new ModeRecoveryCoordinator(settingsStore, service, paths).RecoverAsync();

            Assert.True(recovered.Changed);
            Assert.Equal(openAi, await settingsStore.LoadAsync());
            Assert.Equal(ConfigTransactionStage.Restored, recovered.Snapshot!.Stage);
            Assert.True(recovered.Snapshot.SettingsCommitted);
            Assert.Contains("model = \"official\"", await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_WhenInitialConfigWasNotWritten_RestoresOriginalSettingsAndInvariant()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            const string originalText = "model = \"official\"\n";
            await File.WriteAllTextAsync(configPath, originalText);
            await WriteCatalogAsync(catalogPath, "local-a");
            var original = new LauncherSettings { LlamaRoot = runtimeRoot };
            var target = original with
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-a",
            };
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(original);
            var service = new ChatGptConfigTransactionService(new FixedClientDetector(false));
            var applied = await service.ApplyLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-a",
                    ModelCatalogPath = catalogPath,
                    OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                    OriginalLauncherSettings = original,
                    TargetLauncherSettings = target,
                },
                paths);
            await File.WriteAllTextAsync(configPath, originalText);
            await File.WriteAllTextAsync(
                paths.RecoveryFile,
                JsonSerializer.Serialize(applied with { Stage = ConfigTransactionStage.Prepared }));

            var recovered = await new ModeRecoveryCoordinator(settingsStore, service, paths).RecoverAsync();

            Assert.True(recovered.Changed);
            Assert.Equal(original, await settingsStore.LoadAsync());
            Assert.Equal(ConfigTransactionStage.Restored, recovered.Snapshot!.Stage);
            Assert.Equal(original, recovered.Snapshot.TargetLauncherSettings);
            Assert.True(recovered.Snapshot.SettingsCommitted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recover_WhenLocalUpdateConfigWasNotWritten_RollsBackSettingsAndInvariant()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var configPath = Path.Combine(root, "config.toml");
            var firstCatalog = Path.Combine(root, "catalog-a.json");
            var secondCatalog = Path.Combine(root, "catalog-b.json");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            await File.WriteAllTextAsync(configPath, "model = \"official\"\n");
            await WriteCatalogAsync(firstCatalog, "local-a");
            await WriteCatalogAsync(secondCatalog, "local-b");
            var openAi = new LauncherSettings { LlamaRoot = runtimeRoot };
            var localA = openAi with
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-a",
            };
            var localB = localA with { SelectedModelId = "local-b" };
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(localA);
            var service = new ChatGptConfigTransactionService(new FixedClientDetector(false));
            await service.ApplyLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-a",
                    ModelCatalogPath = firstCatalog,
                    OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                },
                paths);
            var localAText = await File.ReadAllTextAsync(configPath);
            await service.UpdateLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-b",
                    ModelCatalogPath = secondCatalog,
                    OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                    OriginalLauncherSettings = localA,
                    TargetLauncherSettings = localB,
                },
                paths.RecoveryFile);
            await File.WriteAllTextAsync(configPath, localAText);

            var recovered = await new ModeRecoveryCoordinator(settingsStore, service, paths).RecoverAsync();

            Assert.True(recovered.Changed);
            Assert.Equal(localA, await settingsStore.LoadAsync());
            Assert.Equal(ConfigTransactionStage.LocalApplied, recovered.Snapshot!.Stage);
            Assert.Equal(localA, recovered.Snapshot.TargetLauncherSettings);
            Assert.True(recovered.Snapshot.SettingsCommitted);
            Assert.Contains("model = \"local-a\"", await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task WriteCatalogAsync(string path, string slug) =>
        LocalModelCatalogBuilder.WriteAtomicallyAsync(
            path,
            new LocalModelCatalogOptions
            {
                Slug = slug,
                DisplayName = slug,
                ContextWindow = 8192,
            });

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record FixedClientDetector(bool IsRunningValue) : IChatGptClientDetector
    {
        public bool IsRunning() => IsRunningValue;
    }
}
