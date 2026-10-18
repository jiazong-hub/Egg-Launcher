using System.Text.Json;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;

namespace Launcher.Tests;

public sealed class LocalAuthenticationTests
{
    [Fact]
    public async Task MissingAndLegacySettings_DefaultToAuthenticationOff()
    {
        using var fixture = new Fixture();
        Assert.False((await fixture.Store.LoadAsync()).LocalRequiresOpenAiAuth);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Paths.SettingsFile)!);
        await File.WriteAllTextAsync(fixture.Paths.SettingsFile, "{\"SchemaVersion\":2}");
        Assert.False((await fixture.Store.LoadAsync()).LocalRequiresOpenAiAuth);
        await File.WriteAllTextAsync(fixture.Paths.SettingsFile, "{\"SchemaVersion\":1}");
        Assert.False((await fixture.Store.LoadAsync()).LocalRequiresOpenAiAuth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalPreference_IsDeferredUntilLaunchAndDoesNotEditModelJson(bool enabled)
    {
        using var fixture = new Fixture();
        var profile = await fixture.CreateProfileAsync();
        var path = ModelConfigurationFile.GetDirectory(profile, fixture.RuntimeRoot);
        var userJson = Path.Combine(path, "sample.json");
        await ModelConfigurationFile.WriteAsync(userJson, profile, fixture.RuntimeRoot);
        var originalJson = await File.ReadAllTextAsync(userJson);
        var originalConfig = await File.ReadAllTextAsync(fixture.ConfigPath);
        await SettingsMutationCoordinator.UpdateAsync(fixture.Store, fixture.Paths,
            settings => settings with { LocalRequiresOpenAiAuth = enabled });
        Assert.Equal(enabled, (await fixture.Store.LoadAsync()).LocalRequiresOpenAiAuth);
        Assert.Equal(originalConfig, await File.ReadAllTextAsync(fixture.ConfigPath));

        await fixture.Coordinator.SwitchToLocalAsync(fixture.Request(profile));
        Assert.Contains($"requires_openai_auth = {Literal(enabled)}", await File.ReadAllTextAsync(fixture.ConfigPath));

        var current = await fixture.Store.LoadAsync();
        await new LocalEndpointMigrationCoordinator(fixture.Store, fixture.Detector, fixture.Transactions, fixture.Paths)
            .MigrateAsync(current, new Uri("http://127.0.0.1:19090/"));
        Assert.Contains($"requires_openai_auth = {Literal(enabled)}", await File.ReadAllTextAsync(fixture.ConfigPath));

        // Reusing the same model must still update authentication on a new client launch.
        await SettingsMutationCoordinator.UpdateAsync(fixture.Store, fixture.Paths,
            settings => settings with { LocalRequiresOpenAiAuth = !enabled });
        await fixture.Coordinator.SwitchToLocalAsync(fixture.Request(profile));
        Assert.Contains($"requires_openai_auth = {Literal(!enabled)}", await File.ReadAllTextAsync(fixture.ConfigPath));
        await fixture.Coordinator.SwitchToOpenAIAsync();
        Assert.Equal(!enabled, (await fixture.Store.LoadAsync()).LocalRequiresOpenAiAuth);
        var restored = await File.ReadAllTextAsync(fixture.ConfigPath);
        Assert.Contains("model = \"official\"", restored);
        Assert.Contains("model_providers.user_provider = { name = \"User\", requires_openai_auth = true }", restored);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(userJson));
        Assert.DoesNotContain("LocalRequiresOpenAiAuth", originalJson);
        Assert.Equal("{\"fixture\":true}", await File.ReadAllTextAsync(Path.Combine(fixture.CodexHome, "auth.json")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Recovery_PreservesCurrentPreferenceInsteadOfRestoringStalePreference(bool restoreOnline, bool enabled)
    {
        using var fixture = new Fixture();
        var profile = await fixture.CreateProfileAsync();
        await fixture.Coordinator.SwitchToLocalAsync(fixture.Request(profile));
        var local = await fixture.Store.LoadAsync();
        if (restoreOnline)
            await fixture.Transactions.PrepareOpenAiRestoreAsync(fixture.Paths.RecoveryFile,
                local, local with { SelectedMode = ProviderMode.OpenAI });
        else
        {
            var snapshot = JsonSerializer.Deserialize<ManagedConfigSnapshot>(await File.ReadAllTextAsync(fixture.Paths.RecoveryFile))!;
            await File.WriteAllTextAsync(fixture.Paths.RecoveryFile, JsonSerializer.Serialize(snapshot with { SettingsCommitted = false }));
        }
        await fixture.Store.SaveAsync(local with { LocalRequiresOpenAiAuth = enabled });
        var recovery = await new ModeRecoveryCoordinator(fixture.Store, fixture.Transactions, fixture.Paths).RecoverAsync();
        Assert.True(recovery.Changed);
        var settings = await fixture.Store.LoadAsync();
        Assert.Equal(enabled, settings.LocalRequiresOpenAiAuth);
        Assert.Equal(restoreOnline ? ProviderMode.OpenAI : ProviderMode.Local, settings.SelectedMode);
        Assert.True(recovery.Snapshot!.SettingsCommitted);
    }

    private static string Literal(bool value) => value ? "true" : "false";

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        public LauncherDataPaths Paths { get; }
        public JsonSettingsStore Store { get; }
        public string RuntimeRoot => Path.Combine(_root, "llama.cpp");
        public string CodexHome => Path.Combine(_root, "codex");
        public string ConfigPath => Path.Combine(CodexHome, "config.toml");
        public IChatGptClientDetector Detector { get; } = new FixedClientDetector();
        public ChatGptConfigTransactionService Transactions { get; }
        public ModeSwitchCoordinator Coordinator { get; }

        public Fixture()
        {
            Paths = LauncherDataPaths.ForCurrentUser(Path.Combine(_root, "data"));
            Store = new JsonSettingsStore(Paths.SettingsFile);
            Transactions = new ChatGptConfigTransactionService(Detector);
            Coordinator = new ModeSwitchCoordinator(Store, Detector, Transactions, Paths);
            Directory.CreateDirectory(CodexHome);
            File.WriteAllText(ConfigPath, "model = \"official\"\nmodel_providers.user_provider = { name = \"User\", requires_openai_auth = true }\n");
            File.WriteAllText(Path.Combine(CodexHome, "auth.json"), "{\"fixture\":true}");
        }

        public async Task<ModelProfile> CreateProfileAsync()
        {
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "models"));
            await File.WriteAllTextAsync(Path.Combine(RuntimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(RuntimeRoot, "models", "model.gguf"), string.Empty);
            return new ModelProfile
            {
                Id = "local-a",
                Alias = "local-a",
                DisplayName = "Local A",
                ModelRelativePath = "models/model.gguf",
                ContextSize = 16_384,
                CompactionSafetyReserve = 4_096,
            };
        }

        public LocalModeSwitchRequest Request(ModelProfile profile) => new()
        {
            CodexHome = CodexHome,
            RuntimeRoot = RuntimeRoot,
            Profile = profile,
            RouterPort = 18080,
        };

        public void Dispose()
        {
            Store.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedClientDetector : IChatGptClientDetector
    {
        public bool IsRunning() => false;
    }
}
