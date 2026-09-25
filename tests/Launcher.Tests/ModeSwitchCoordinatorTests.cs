using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;

namespace Launcher.Tests;

public sealed class ModeSwitchCoordinatorTests
{
    [Fact]
    public async Task EndpointMigration_UpdatesOnlyLocalProviderAndRestoresOfficialConfiguration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dataPaths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var codexHome = Path.Combine(root, "codex-home");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            var configPath = Path.Combine(codexHome, "config.toml");
            const string officialConfig = "model = \"official-model\"\ncustom_setting = true\n";
            await File.WriteAllTextAsync(configPath, officialConfig);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);
            using var settingsStore = new JsonSettingsStore(dataPaths.SettingsFile);
            var detector = new FixedClientDetector(false);
            var transactions = new ChatGptConfigTransactionService(detector);
            var modeSwitch = new ModeSwitchCoordinator(settingsStore, detector, transactions, dataPaths);
            await modeSwitch.SwitchToLocalAsync(new LocalModeSwitchRequest
            {
                CodexHome = codexHome,
                RuntimeRoot = runtimeRoot,
                Profile = CreateProfile(),
                RouterPort = 18080,
            });
            var current = await settingsStore.LoadAsync();
            var migration = new LocalEndpointMigrationCoordinator(
                settingsStore,
                detector,
                transactions,
                dataPaths);

            var migrated = await migration.MigrateAsync(current, new Uri("http://127.0.0.1:19090/"));

            Assert.Equal(19090, migrated.RouterPort);
            Assert.True(migrated.PublicProxyPortInitialized);
            var localConfig = await File.ReadAllTextAsync(configPath);
            Assert.Contains("base_url = \"http://127.0.0.1:19090/v1/\"", localConfig, StringComparison.Ordinal);
            Assert.Contains("model = \"local-coder\"", localConfig, StringComparison.Ordinal);
            Assert.Contains("custom_setting = true", localConfig, StringComparison.Ordinal);

            await modeSwitch.SwitchToOpenAIAsync();
            var restoredConfig = await File.ReadAllTextAsync(configPath);
            Assert.StartsWith(officialConfig, restoredConfig, StringComparison.Ordinal);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("base_url = \"http://127.0.0.1:0/v1/\"", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_provider =", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1:19090", restoredConfig, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EndpointMigration_WhenDesktopRuns_ChangesNeitherConfigNorSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dataPaths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var codexHome = Path.Combine(root, "codex-home");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            var configPath = Path.Combine(codexHome, "config.toml");
            await File.WriteAllTextAsync(configPath, "model = \"official-model\"\n");
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);
            using var settingsStore = new JsonSettingsStore(dataPaths.SettingsFile);
            var stoppedDetector = new FixedClientDetector(false);
            await new ModeSwitchCoordinator(
                settingsStore,
                stoppedDetector,
                new ChatGptConfigTransactionService(stoppedDetector),
                dataPaths).SwitchToLocalAsync(new LocalModeSwitchRequest
                {
                    CodexHome = codexHome,
                    RuntimeRoot = runtimeRoot,
                    Profile = CreateProfile(),
                    RouterPort = 18080,
                });
            var beforeSettings = await settingsStore.LoadAsync();
            var beforeConfig = await File.ReadAllTextAsync(configPath);
            var runningDetector = new FixedClientDetector(true);
            var migration = new LocalEndpointMigrationCoordinator(
                settingsStore,
                runningDetector,
                new ChatGptConfigTransactionService(runningDetector),
                dataPaths);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migration.MigrateAsync(beforeSettings, new Uri("http://127.0.0.1:19090/")));

            Assert.Contains("正在运行", exception.Message, StringComparison.Ordinal);
            Assert.Equal(beforeSettings, await settingsStore.LoadAsync());
            Assert.Equal(beforeConfig, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchRoundTrip_WithDirectLocalModelChange_PreservesOfficialAndUnknownConfiguration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dataPaths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var codexHome = Path.Combine(root, "codex-home");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "config.toml"),
                "model = \"official-model\"\n"
                + "model_reasoning_effort = \"xhigh\"\n"
                + "model_verbosity = \"high\"\n"
                + "service_tier = \"default\"\n"
                + "custom_setting = true\n[projects]\ntrust = \"trusted\"\n");
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder-b.gguf"), string.Empty);

            using var settingsStore = new JsonSettingsStore(dataPaths.SettingsFile);
            var detector = new FixedClientDetector(false);
            var coordinator = new ModeSwitchCoordinator(
                settingsStore,
                detector,
                new ChatGptConfigTransactionService(detector),
                dataPaths);
            var request = new LocalModeSwitchRequest
            {
                CodexHome = codexHome,
                RuntimeRoot = runtimeRoot,
                Profile = CreateProfile(),
                RouterPort = 18080,
            };

            var local = await coordinator.SwitchToLocalAsync(request);

            Assert.True(local.Changed);
            Assert.Equal(ProviderMode.Local, (await settingsStore.LoadAsync()).SelectedMode);
            var localConfig = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
            Assert.Contains("model = \"local-coder\"", localConfig, StringComparison.Ordinal);
            Assert.Contains(
                "model_provider = \"chatgpt_local_launcher\"",
                localConfig,
                StringComparison.Ordinal);
            Assert.Contains(
                "model_providers.chatgpt_local_launcher =",
                localConfig,
                StringComparison.Ordinal);
            Assert.Contains("requires_openai_auth = true", localConfig, StringComparison.Ordinal);
            Assert.Contains("supports_websockets = false", localConfig, StringComparison.Ordinal);
            Assert.Contains(
                "base_url = \"http://127.0.0.1:18080/v1/\"",
                localConfig,
                StringComparison.Ordinal);
            Assert.DoesNotContain("openai_base_url =", localConfig, StringComparison.Ordinal);
            Assert.Contains("model_context_window = 16384", localConfig, StringComparison.Ordinal);
            Assert.Contains("model_auto_compact_token_limit = 12288", localConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_auto_compact_token_limit_scope", localConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_reasoning_effort", localConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_verbosity", localConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("service_tier", localConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("approval_policy =", localConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("approvals_reviewer =", localConfig, StringComparison.Ordinal);
            Assert.Contains("custom_setting = true", localConfig, StringComparison.Ordinal);
            Assert.True(File.Exists(dataPaths.LocalModelCatalogFile));
            Assert.True(File.Exists(dataPaths.RouterPresetFile));

            var localB = await coordinator.SwitchToLocalAsync(request with
            {
                Profile = CreateProfile(
                    "local-coder-b",
                    "Local Coder B",
                    Path.Combine("models", "coder-b.gguf")) with
                {
                    ContextSize = 32_768,
                    CompactionSafetyReserve = 8_192,
                },
            });

            Assert.True(localB.Changed);
            Assert.Equal("local-coder-b", (await settingsStore.LoadAsync()).SelectedModelId);
            var localBConfig = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
            Assert.Contains("model = \"local-coder-b\"", localBConfig, StringComparison.Ordinal);
            Assert.Contains("model_context_window = 32768", localBConfig, StringComparison.Ordinal);
            Assert.Contains("model_auto_compact_token_limit = 24576", localBConfig, StringComparison.Ordinal);
            Assert.Contains("custom_setting = true", localBConfig, StringComparison.Ordinal);
            var localBCatalog = await File.ReadAllTextAsync(dataPaths.LocalModelCatalogFile);
            Assert.Contains("\"context_window\": 32768", localBCatalog, StringComparison.Ordinal);

            var openAi = await coordinator.SwitchToOpenAIAsync();

            Assert.True(openAi.Changed);
            var restoredSettings = await settingsStore.LoadAsync();
            Assert.Equal(ProviderMode.OpenAI, restoredSettings.SelectedMode);
            Assert.Equal("local-coder-b", restoredSettings.SelectedModelId);
            var restoredConfig = await File.ReadAllTextAsync(Path.Combine(codexHome, "config.toml"));
            Assert.Contains("model = \"official-model\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("model_reasoning_effort = \"xhigh\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("model_verbosity = \"high\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("service_tier = \"default\"", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("openai_base_url", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_provider =", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("base_url = \"http://127.0.0.1:0/v1/\"", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("approval_policy =", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("approvals_reviewer =", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("custom_setting = true", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("[projects]", restoredConfig, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchToLocal_WithUnsetContext_WritesNoArtifacts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dataPaths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var codexHome = Path.Combine(root, "codex-home");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            var configPath = Path.Combine(codexHome, "config.toml");
            const string officialConfig = "model = \"official-model\"\n";
            await File.WriteAllTextAsync(configPath, officialConfig);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);

            using var settingsStore = new JsonSettingsStore(dataPaths.SettingsFile);
            var detector = new FixedClientDetector(false);
            var coordinator = new ModeSwitchCoordinator(
                settingsStore,
                detector,
                new ChatGptConfigTransactionService(detector),
                dataPaths);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.SwitchToLocalAsync(
                new LocalModeSwitchRequest
                {
                    CodexHome = codexHome,
                    RuntimeRoot = runtimeRoot,
                    Profile = CreateProfile() with { ContextSize = 0 },
                }));

            Assert.Contains("1,024", exception.Message, StringComparison.Ordinal);
            Assert.Contains("不会代替用户选择", exception.Message, StringComparison.Ordinal);
            Assert.Equal(officialConfig, await File.ReadAllTextAsync(configPath));
            Assert.False(File.Exists(dataPaths.LocalModelCatalogFile));
            Assert.False(File.Exists(dataPaths.RouterPresetFile));
            Assert.False(File.Exists(dataPaths.RecoveryFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchToLocal_WhenClientRuns_WritesNoArtifacts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dataPaths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var codexHome = Path.Combine(root, "codex-home");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);

            using var settingsStore = new JsonSettingsStore(dataPaths.SettingsFile);
            var detector = new FixedClientDetector(true);
            var coordinator = new ModeSwitchCoordinator(
                settingsStore,
                detector,
                new ChatGptConfigTransactionService(detector),
                dataPaths);

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SwitchToLocalAsync(
                new LocalModeSwitchRequest
                {
                    CodexHome = codexHome,
                    RuntimeRoot = runtimeRoot,
                    Profile = CreateProfile(),
                }));

            Assert.False(File.Exists(dataPaths.LocalModelCatalogFile));
            Assert.False(File.Exists(dataPaths.RouterPresetFile));
            Assert.False(File.Exists(Path.Combine(codexHome, "config.toml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SwitchToOpenAI_WhenSettingsSaveFails_ReportsThatOfficialConfigWasRestored()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dataPaths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var codexHome = Path.Combine(root, "codex-home");
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            var configPath = Path.Combine(codexHome, "config.toml");
            const string officialConfig = "model = \"official-model\"\n";
            await File.WriteAllTextAsync(configPath, officialConfig);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);
            using var settingsStore = new JsonSettingsStore(dataPaths.SettingsFile);
            var detector = new FixedClientDetector(false);
            var transactions = new ChatGptConfigTransactionService(detector);
            await new ModeSwitchCoordinator(settingsStore, detector, transactions, dataPaths)
                .SwitchToLocalAsync(new LocalModeSwitchRequest
                {
                    CodexHome = codexHome,
                    RuntimeRoot = runtimeRoot,
                    Profile = CreateProfile(),
                    RouterPort = 18080,
                });
            var failingStore = new FailOpenAiSaveSettingsStore(settingsStore);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ModeSwitchCoordinator(failingStore, detector, transactions, dataPaths).SwitchToOpenAIAsync());

            Assert.Contains("官方配置已经恢复", exception.Message, StringComparison.Ordinal);
            Assert.Contains("重新打开 Launcher", exception.Message, StringComparison.Ordinal);
            var restoredConfig = await File.ReadAllTextAsync(configPath);
            Assert.StartsWith(officialConfig, restoredConfig, StringComparison.Ordinal);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("base_url = \"http://127.0.0.1:0/v1/\"", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_provider =", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1:18080", restoredConfig, StringComparison.Ordinal);
            Assert.Equal(ProviderMode.Local, (await settingsStore.LoadAsync()).SelectedMode);
            Assert.True(File.Exists(dataPaths.RecoveryFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ModelProfile CreateProfile(
        string id = "local-coder",
        string displayName = "Local Coder",
        string? relativePath = null) => new()
        {
            Id = id,
            DisplayName = displayName,
            ModelRelativePath = relativePath ?? Path.Combine("models", "coder.gguf"),
            Alias = id,
            ContextSize = 16384,
            CompactionSafetyReserve = 4096,
            Parallel = 1,
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedClientDetector(bool isRunning) : IChatGptClientDetector
    {
        public bool IsRunning() => isRunning;
    }

    private sealed class FailOpenAiSaveSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) =>
            settings.SelectedMode == ProviderMode.OpenAI
                ? Task.FromException(new IOException("injected settings failure"))
                : inner.SaveAsync(settings, cancellationToken);
    }
}
