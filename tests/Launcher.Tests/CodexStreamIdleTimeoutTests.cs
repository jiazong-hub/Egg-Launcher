using System.Text.Json;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;

namespace Launcher.Tests;

public sealed class CodexStreamIdleTimeoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-timeout-tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherDataPaths _paths;
    private readonly JsonSettingsStore _settings;
    private readonly ModeSwitchCoordinator _modes;
    private readonly ModelArtifactWriter _writer = new(new JsonModelProfileStore(), new ModelArtifactTransaction());
    private string Config => Path.Combine(_root, "codex", "config.toml");
    private const string Official = "model = \"official\"\ncustom_setting = true\n";

    public CodexStreamIdleTimeoutTests()
    {
        _paths = LauncherDataPaths.ForCurrentUser(Path.Combine(_root, "state"));
        Directory.CreateDirectory(Path.Combine(_root, "codex"));
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllText(Config, Official);
        File.WriteAllText(Path.Combine(_root, "models", "model.gguf"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), string.Empty);
        _settings = new JsonSettingsStore(_paths.SettingsFile);
        var detector = new StoppedClient();
        _modes = new ModeSwitchCoordinator(_settings, detector, new ChatGptConfigTransactionService(detector), _paths);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(5, true)]
    [InlineData(30, true)]
    [InlineData(125, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(6, false)]
    [InlineData(int.MaxValue, false)]
    public void Timeout_RequiresFiveMinuteIncrements(int? minutes, bool valid)
    {
        Assert.Equal(valid, CodexStreamIdleTimeout.IsValid(minutes));
        if (valid) Assert.Equal(minutes * 60_000, CodexStreamIdleTimeout.ToMilliseconds(minutes));
        else Assert.Throws<ArgumentOutOfRangeException>(() => CodexStreamIdleTimeout.ToMilliseconds(minutes));
    }

    [Fact]
    public async Task ActiveSave_SynchronizesTimeoutAndConversationSettingsBeforeRestart()
    {
        var profile = Profile();
        await Save(profile, false);
        await Switch(profile);
        var updated = profile with
        {
            ContextSize = 32768,
            CompactionSafetyReserve = 8192,
            ToolOutputTokenLimit = 2048,
            CodexStreamIdleTimeoutMinutes = 30,
        };
        await Save(updated, true);
        var config = await File.ReadAllTextAsync(Config);
        Assert.Contains("stream_idle_timeout_ms = 1800000", config);
        Assert.Contains("model_context_window = 32768", config);
        Assert.Contains("model_auto_compact_token_limit = 24576", config);
        Assert.Contains("tool_output_token_limit = 2048", config);
        var saved = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        Assert.Equal(30, saved.CodexStreamIdleTimeoutMinutes);
        Assert.Equal(32768, saved.ContextSize);
        Assert.Contains("--ctx-size 32768", await File.ReadAllTextAsync(BatchScriptGenerator.GetOutputPath(_root, profile.Id)));
        Assert.DoesNotContain("stream_idle_timeout", await File.ReadAllTextAsync(BatchScriptGenerator.GetOutputPath(_root, profile.Id)));
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.LocalModelCatalogFile));
        Assert.Equal(32768, catalog.RootElement.GetProperty("models")[0].GetProperty("context_window").GetInt32());

        // Repeated saves update ownership, including removing the explicit override.
        await Save(updated with { CodexStreamIdleTimeoutMinutes = 15 }, true);
        Assert.Contains("stream_idle_timeout_ms = 900000", await File.ReadAllTextAsync(Config));
        await Save(updated with { CodexStreamIdleTimeoutMinutes = null }, true);
        Assert.DoesNotContain("stream_idle_timeout_ms", await File.ReadAllTextAsync(Config));
        await Save(updated, true);
        await _modes.SwitchToOpenAIAsync();
        var restored = await File.ReadAllTextAsync(Config);
        Assert.StartsWith(Official, restored);
        Assert.DoesNotContain("stream_idle_timeout_ms", restored);
        Assert.DoesNotContain("model_context_window", restored);
        await Switch(updated);
        Assert.Contains("stream_idle_timeout_ms = 1800000", await File.ReadAllTextAsync(Config));
    }

    [Fact]
    public async Task Save_ConfigurationConflictRollsBackAllModelArtifacts()
    {
        var profile = Profile();
        await Save(profile, false);
        await Switch(profile);
        var files = new[]
        {
            Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), profile.Id + ".json"),
            Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), profile.Id + ".json.bak"),
            BatchScriptGenerator.GetOutputPath(_root, profile.Id), _paths.RouterPresetFile, _paths.LocalModelCatalogFile,
        };
        var original = new Dictionary<string, byte[]>();
        foreach (var file in files) original[file] = await File.ReadAllBytesAsync(file);
        var externalConfig = (await File.ReadAllTextAsync(Config)).Replace("Local llama.cpp", "Externally edited");
        await File.WriteAllTextAsync(Config, externalConfig);
        await Assert.ThrowsAnyAsync<Exception>(() => Save(profile with
        {
            ContextSize = 32768,
            CodexStreamIdleTimeoutMinutes = 30,
        }, true));
        foreach (var file in files) Assert.Equal(original[file], await File.ReadAllBytesAsync(file));
        Assert.Equal(externalConfig, await File.ReadAllTextAsync(Config));
    }

    [Fact]
    public async Task InactiveSave_PreservesActiveCodexConfiguration()
    {
        var profile = Profile() with { CodexStreamIdleTimeoutMinutes = 30 };
        await Switch(profile);
        var original = await File.ReadAllTextAsync(Config);
        await Save(profile with { Id = "other", Alias = "other", CodexStreamIdleTimeoutMinutes = 60 }, false);
        Assert.Equal(original, await File.ReadAllTextAsync(Config));
        Assert.Equal(60, Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles).CodexStreamIdleTimeoutMinutes);
    }

    [Fact]
    public async Task LegacyProfile_InheritsDefaultAndDedicatedDefaultsRoundTripTimeout()
    {
        await Save(Profile(), false);
        var path = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), "model.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Profile() with { SchemaVersion = 11 }))!;
        legacy.AsObject().Remove(nameof(ModelProfile.CodexStreamIdleTimeoutMinutes));
        await File.WriteAllTextAsync(path, legacy.ToJsonString());
        var migrated = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        Assert.Equal(ModelProfile.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Null(migrated.CodexStreamIdleTimeoutMinutes);
        var defaults = ModelParameterDefaults.FromProfile(migrated with { CodexStreamIdleTimeoutMinutes = 30 });
        Assert.Equal(30, defaults.ApplyTo(migrated).CodexStreamIdleTimeoutMinutes);
        Assert.Equal(ContextShiftCapabilityCache.Fingerprint(migrated, _root),
            ContextShiftCapabilityCache.Fingerprint(migrated with { CodexStreamIdleTimeoutMinutes = 30 }, _root));
    }

    private Task Save(ModelProfile profile, bool active) => _writer.SaveAsync(profile, _root,
        _paths.RouterPresetFile, _paths.LocalModelCatalogFile, active,
        synchronizeActiveCodexConfiguration: async (saved, _) => { await Switch(saved); });

    private Task<ModeSwitchResult> Switch(ModelProfile profile) => _modes.SwitchToLocalAsync(new LocalModeSwitchRequest
    {
        CodexHome = Path.Combine(_root, "codex"),
        RuntimeRoot = _root,
        Profile = profile,
        RouterPort = 18080,
    });

    private static ModelProfile Profile() => new()
    {
        Id = "model",
        Alias = "local-model",
        DisplayName = "Model",
        ModelRelativePath = @"models\model.gguf",
        ContextSize = 16384,
        CompactionSafetyReserve = 4096,
    };

    private sealed class StoppedClient : IChatGptClientDetector
    {
        public bool IsRunning() => false;
    }

    public void Dispose()
    {
        _settings.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
