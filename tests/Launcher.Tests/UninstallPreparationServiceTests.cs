using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.Startup;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;

namespace Launcher.Tests;

public sealed class UninstallPreparationServiceTests
{
    [Fact]
    public async Task Prepare_FromLocal_RestoresOfficialRoutingAndKeepsHistoryProviderAndData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var codexHome = Path.Combine(root, "codex");
            var runtimeRoot = Path.Combine(root, "runtime");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "models"));
            var configPath = Path.Combine(codexHome, "config.toml");
            await File.WriteAllTextAsync(configPath, "model = \"official\"\n");
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "models", "coder.gguf"), string.Empty);
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            var detector = new FixedClientDetector(false);
            var transactions = new ChatGptConfigTransactionService(detector);
            await new ModeSwitchCoordinator(settingsStore, detector, transactions, paths)
                .SwitchToLocalAsync(new LocalModeSwitchRequest
                {
                    CodexHome = codexHome,
                    RuntimeRoot = runtimeRoot,
                    Profile = new ModelProfile
                    {
                        Id = "local-coder",
                        DisplayName = "Local Coder",
                        Alias = "local-coder",
                        ModelRelativePath = Path.Combine("models", "coder.gguf"),
                        ContextSize = 16_384,
                        CompactionSafetyReserve = 4_096,
                        Parallel = 1,
                    },
                    RouterPort = 18080,
                });
            var appPath = Path.Combine(root, "installed", "Launcher.App.exe");
            var runStore = new MemoryRunEntryStore();
            runStore.Write(AgentStartupRegistration.ValueName,
                AgentStartupRegistration.BuildCommand(appPath, "--startup"));

            await new UninstallPreparationService(
                settingsStore, detector, transactions, paths, runStore).PrepareAsync(appPath);

            Assert.Equal(ProviderMode.OpenAI, (await settingsStore.LoadAsync()).SelectedMode);
            var restoredConfig = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model = \"official\"", restoredConfig, StringComparison.Ordinal);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"",
                restoredConfig, StringComparison.Ordinal);
            Assert.Contains("base_url = \"http://127.0.0.1:0/v1/\"", restoredConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("model_provider =", restoredConfig, StringComparison.Ordinal);
            Assert.Null(runStore.Read(AgentStartupRegistration.ValueName));
            Assert.True(File.Exists(paths.SettingsFile));
            Assert.True(Directory.Exists(paths.BackupsDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_WhenClientRuns_ChangesNothing()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(new LauncherSettings());
            var appPath = Path.Combine(root, "installed", "Launcher.App.exe");
            var runStore = new MemoryRunEntryStore();
            var command = AgentStartupRegistration.BuildCommand(appPath, "--startup");
            runStore.Write(AgentStartupRegistration.ValueName, command);
            var detector = new FixedClientDetector(true);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new UninstallPreparationService(
                    settingsStore,
                    detector,
                    new ChatGptConfigTransactionService(detector),
                    paths,
                    runStore).PrepareAsync(appPath));

            Assert.Equal(command, runStore.Read(AgentStartupRegistration.ValueName));
            Assert.Equal(ProviderMode.OpenAI, (await settingsStore.LoadAsync()).SelectedMode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_WhenLocalRecoveryIsMissing_LeavesFilesAndStartupEntryUntouched()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                LlamaRoot = Path.Combine(root, "runtime"),
            });
            var appPath = Path.Combine(root, "installed", "Launcher.App.exe");
            var runStore = new MemoryRunEntryStore();
            var command = AgentStartupRegistration.BuildCommand(appPath, "--startup");
            runStore.Write(AgentStartupRegistration.ValueName, command);
            var detector = new FixedClientDetector(false);

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                new UninstallPreparationService(
                    settingsStore,
                    detector,
                    new ChatGptConfigTransactionService(detector),
                    paths,
                    runStore).PrepareAsync(appPath));

            Assert.Equal(ProviderMode.Local, (await settingsStore.LoadAsync()).SelectedMode);
            Assert.Equal(command, runStore.Read(AgentStartupRegistration.ValueName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_PreservesForeignStartupEntry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            var appPath = Path.Combine(root, "installed", "Launcher.App.exe");
            var runStore = new MemoryRunEntryStore();
            const string foreignCommand = "\"C:\\portable\\Launcher.App.exe\" --startup";
            runStore.Write(AgentStartupRegistration.ValueName, foreignCommand);
            var detector = new FixedClientDetector(false);

            await new UninstallPreparationService(
                settingsStore,
                detector,
                new ChatGptConfigTransactionService(detector),
                paths,
                runStore).PrepareAsync(appPath);

            Assert.Equal(foreignCommand, runStore.Read(AgentStartupRegistration.ValueName));
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

    private sealed class FixedClientDetector(bool isRunning) : IChatGptClientDetector
    {
        public bool IsRunning() => isRunning;
    }

    private sealed class MemoryRunEntryStore : IUserRunEntryStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string valueName) => _values.GetValueOrDefault(valueName);

        public void Write(string valueName, string command) => _values[valueName] = command;

        public void Delete(string valueName) => _values.Remove(valueName);
    }
}
