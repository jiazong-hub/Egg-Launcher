using System.Text.Json;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Tests;

public sealed class CustomParameterTests
{
    private static CustomParameter Row(string name, string value = "1", bool flag = false) => new() { Name = name, Value = value, NoValue = flag };

    [Theory]
    [InlineData("--batch-size")]
    [InlineData("-b")]
    [InlineData("--ubatch-size")]
    [InlineData("-ub")]
    [InlineData("--no-cache-prompt")]
    [InlineData("--spec-draft-n-ubatch")]
    [InlineData("--no-kv-unified")]
    [InlineData("--host")]
    [InlineData("-m")]
    [InlineData("--api-key")]
    [InlineData("--chat-template-file")]
    [InlineData("--spec-ngram-mod-n-min")]
    [InlineData("--lookup-cache-static")]
    [InlineData("--threads")]
    public void FixedAndAutomaticAndAliasCommandsAreReservedEvenWhenNotEmitted(string command) =>
        Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row(command)] }));

    [Theory]
    [InlineData("model")]
    [InlineData("model_providers.user.name")]
    [InlineData("default_permissions")]
    [InlineData("sandbox_mode")]
    [InlineData("permissions.custom")]
    [InlineData("shell_environment_policy.set.TEST")]
    [InlineData("model_auto_compact_token_limit")]
    [InlineData("profile")]
    [InlineData("approval_policy")]
    public void CodexManagedNamespacesCannotBeOverridden(string command) =>
        Assert.NotEmpty(CodexCustomParameterValidator.Validate([Row(command)]));

    [Theory]
    [InlineData("--seed", "--seed", "1")]
    [InlineData("--seed", "-s", "2")]
    [InlineData("--future-flag", "--no-future-flag", "true")]
    public void SameDifferentAliasAndOppositeCommandsAllConflict(string first, string second, string value) =>
        Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row(first), Row(second, value)] }));

    [Theory]
    [InlineData("bad-name")]
    [InlineData("--x=1")]
    [InlineData("--x --other")]
    [InlineData("--")]
    public void InvalidLlamaNamesAreRejected(string name) => Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row(name)] }));

    [Theory]
    [InlineData("hello")]
    [InlineData("true\nevil = 1")]
    [InlineData("\"x\"\n[evil]\ny=2")]
    [InlineData("")]
    [InlineData("[1,")]
    [InlineData("true # not part of the value")]
    public void CodexSyntaxAndAssignmentInjectionAreRejectedWithoutLeakingValues(string value)
    {
        var errors = CodexCustomParameterValidator.Validate([Row("future_value", value)]);
        Assert.NotEmpty(errors);
        if (value.Length > 10) Assert.DoesNotContain(value, string.Join('|', errors));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1024")]
    [InlineData("1.25")]
    [InlineData("\"hello\"")]
    [InlineData("[1, 2]")]
    [InlineData("{ a = true, b = \"x\" }")]
    public void ValidTomlTypesAreNotGuessedOrCoerced(string value) => Assert.Empty(CodexCustomParameterValidator.Validate([Row("future_value", value)]));

    [Fact]
    public void ParentChildConflictAndReceiverSeparationAndLegacyExtraAreHandled()
    {
        Assert.NotEmpty(CodexCustomParameterValidator.Validate([Row("features"), Row("features.x")]));
        Assert.Empty(CustomParameterRules.Validate(new() { Llama = [Row("--future")], Codex = [Row("future")] }));
        Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row("--future")] }, ["future"]));
        Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row("--flag", "retained", true)] }));
        Assert.Empty(CustomParameterRules.Validate(new() { Llama = [Row("-C", "0xff")] })); // CPU mask, distinct from -c.
        Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row("-c", "8192")] }));
    }

    [Theory]
    [InlineData("hello#lost")]
    [InlineData("hello;lost")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("x\ny")]
    [InlineData("\"quoted\"")]
    public void ValuesThatCannotRoundTripThroughBothIniAndBatAreRejected(string value) =>
        Assert.NotEmpty(CustomParameterRules.Validate(new() { Llama = [Row("--future", value)] }));

    [Fact]
    public void ShortAliasesAndTrailingWindowsSlashesAreEmittedExactly()
    {
        using var f = new Fixture();
        var model = f.Model with { CustomParameters = new() { Llama = [Row("-s", "42"), Row("--future", "C:\\folder\\")] } };
        var bat = BatchScriptGenerator.Generate(model, f.Root);
        Assert.Contains("-s \"42\"", bat);
        Assert.DoesNotContain("--s ", bat);
        Assert.Contains("--future \"C:\\folder\\\\\"", bat);
        Assert.Contains("future = C:\\folder\\\n", RouterPresetGenerator.Generate(model, f.Root, false));
    }

    [Fact]
    public async Task ExternalChangeToNewDynamicKeyBlocksUpdateBeforeAnyWrite()
    {
        using var f = new Fixture("future = 0\nnew_key = 1\n");
        await f.Service.ApplyLocalAsync(f.Request(Row("future", "7")), f.Paths);
        await File.WriteAllTextAsync(f.Config, (await File.ReadAllTextAsync(f.Config)).Replace("new_key = 1", "new_key = 2"));
        var before = File.ReadAllBytes(f.Config);
        await Assert.ThrowsAsync<ChatGptConfigConflictException>(() => f.Service.UpdateLocalAsync(f.Request(Row("new_key", "3")), f.Paths.RecoveryFile));
        Assert.Equal(before, File.ReadAllBytes(f.Config));
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        Assert.Contains("new_key = 2", await File.ReadAllTextAsync(f.Config));
    }

    [Fact]
    public async Task InterruptedLocalUpdateRecoversBothBeforeAndAfterWriteStates()
    {
        using var f = new Fixture("future = 0\n");
        await f.Service.ApplyLocalAsync(f.Request(Row("future", "7")), f.Paths);
        var before = await File.ReadAllTextAsync(f.Config);
        var updated = await f.Service.UpdateLocalAsync(f.Request(Row("new_key", "3")), f.Paths.RecoveryFile);
        var after = await File.ReadAllTextAsync(f.Config);
        // Existing recovery semantics commit an applied update and abandon an unwritten update.
        foreach (var stage in new[] { ConfigTransactionStage.LocalUpdatePrepared, ConfigTransactionStage.LocalUpdateApplied })
            foreach (var wasWritten in new[] { false, true })
            {
                await File.WriteAllTextAsync(f.Config, wasWritten ? after : before);
                await File.WriteAllTextAsync(f.Paths.RecoveryFile, JsonSerializer.Serialize(updated with { Stage = stage }));
                var result = await f.NewService().RecoverInterruptedAsync(f.Paths.RecoveryFile);
                Assert.Equal(ConfigTransactionStage.LocalApplied, result.Snapshot!.Stage);
                var values = new TomlRootDocument(await File.ReadAllTextAsync(f.Config));
                Assert.Equal(wasWritten ? "0" : "7", values.GetCustomValue("future"));
                Assert.Equal(wasWritten ? "3" : null, values.GetCustomValue("new_key"));
            }
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        Assert.Contains("future = 0", await File.ReadAllTextAsync(f.Config));
    }

    [Fact]
    public async Task ChangedCustomPathShapeRequiresOnlineBoundaryWithoutDataLoss()
    {
        using var f = new Fixture();
        await f.Service.ApplyLocalAsync(f.Request(Row("features.a", "7")), f.Paths);
        var before = File.ReadAllBytes(f.Config);
        await Assert.ThrowsAsync<ChatGptConfigConflictException>(() => f.Service.UpdateLocalAsync(f.Request(Row("features", "{ b = true }")), f.Paths.RecoveryFile));
        Assert.Equal(before, File.ReadAllBytes(f.Config));
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        await f.Service.ApplyLocalAsync(f.Request(Row("features", "{ b = true }")), f.Paths);
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        Assert.DoesNotContain("features", await File.ReadAllTextAsync(f.Config));
    }

    [Fact]
    public async Task ExplicitAndDottedTableSiblingsSurviveInsertionAndRemoval()
    {
        using var f = new Fixture("[features.child]\nkeep = true\n");
        await f.Service.ApplyLocalAsync(f.Request(Row("features.a", "7")), f.Paths);
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        var restored = await File.ReadAllTextAsync(f.Config);
        Assert.Contains("[features.child]\nkeep = true", restored);
        Assert.DoesNotContain("features.a", restored);
    }

    [Fact]
    public async Task JsonAndDefaultRoundTripRetainsEveryRowAndGeneratesExactlyOneValue()
    {
        using var f = new Fixture();
        var model = f.Model with
        {
            CustomParameters = new() { Llama = [Row("--seed", "42"), Row("--future-switch", "", true)], Codex = [Row("features.future", "true")] },
            RuntimeParameters = new() { CacheRamMiB = -1, CachePrompt = false, CacheIdleSlots = true, KvUnified = false, BackendSampling = true, CheckpointMinimumStep = 0, DraftMicroBatchSize = 0, DraftSampling = "greedy", MoeCacheMiB = 512 },
        };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(model, f.Root), "settings.json");
        await ModelConfigurationFile.WriteAsync(path, model, f.Root);
        var loaded = await ModelConfigurationFile.ReadAsync(path, model, f.Root);
        Assert.Equal(JsonSerializer.Serialize(model.CustomParameters), JsonSerializer.Serialize(loaded.CustomParameters));
        Assert.Equal(model.RuntimeParameters, loaded.RuntimeParameters);
        Assert.Equal(2, JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal(model.RuntimeParameters, ModelParameterDefaults.FromProfile(model).ApplyTo(f.Model).RuntimeParameters);
        Assert.Equal(model.CustomParameters, ModelParameterDefaults.FromProfile(model).ApplyTo(f.Model).CustomParameters);
        var bat = BatchScriptGenerator.Generate(loaded, f.Root);
        var preset = RouterPresetGenerator.Generate(loaded, f.Root, false);
        Assert.Contains("--seed \"42\"", bat);
        Assert.Contains("seed = 42\n", preset);
        Assert.Contains("--future-switch\r\n", bat.Replace(" ^\r\n", "\r\n"));
        Assert.Contains("future-switch = true\n", preset);
        Assert.DoesNotContain("features.future", bat);
        Assert.DoesNotContain("features.future", preset);
        Assert.Contains("--cache-ram \"-1\"", bat);
        Assert.Contains("--no-cache-prompt", bat);
        Assert.DoesNotContain("--spec-draft-n-ubatch", bat); // MTP off: saved, not emitted.
        Assert.DoesNotContain("--moe-cache-mib", bat); // Dense: saved, not emitted.
        var enabled = BatchScriptGenerator.Generate(loaded with { ModelType = ModelType.MoE, MtpEnabled = true, MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate }, f.Root);
        Assert.Contains("--moe-cache-mib \"512\"", enabled);
        Assert.Contains("--spec-draft-n-ubatch \"0\"", enabled);
        Assert.Contains("--spec-draft-sampling \"greedy\"", enabled);
    }

    [Fact]
    public async Task OldConflictsRemainLoadableButEveryWriteRejectsWithoutChangingFile()
    {
        using var f = new Fixture();
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(f.Model, f.Root), "repair.json");
        await ModelConfigurationFile.WriteAsync(path, f.Model, f.Root);
        var document = JsonSerializer.Deserialize<ModelConfigurationDocument>(await File.ReadAllTextAsync(path))!;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document with { Parameters = f.Model with { CustomParameters = new() { Llama = [Row("-b", "8192")] } } }));
        var before = await File.ReadAllTextAsync(path);
        var loaded = await ModelConfigurationFile.ReadAsync(path, f.Model, f.Root);
        Assert.Single(loaded.CustomParameters.Llama);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.WriteAsync(path, loaded, f.Root));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("all")]
    [InlineData("0")]
    [InlineData("999")]
    public void ExternalDraftLayersAcceptNewModes(string value)
    {
        using var f = new Fixture();
        Assert.Empty(ModelProfileValidator.Validate(f.Model with { MtpDraftGpuLayers = value }, f.Root));
    }

    [Fact]
    public void RuntimeRangesRejectOnlyDirectInvalidInputs()
    {
        Assert.Empty(new RuntimeParameterSettings { CacheRamMiB = -1, DraftMicroBatchSize = 0, CheckpointMinimumStep = 0 }.Validate());
        Assert.Single(new RuntimeParameterSettings { CacheRamMiB = 0, CacheIdleSlots = true }.Validate());
        Assert.Equal(6, new RuntimeParameterSettings { CacheRamMiB = -2, DraftMicroBatchSize = -1, CheckpointMinimumStep = -1, MoeCacheMiB = -1, DraftSampling = "invalid", BackendSampling = false }.Validate().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStartupDiagnosticsDistinguishDetailAndNeverRecordCustomValues(bool detailed)
    {
        using var f = new Fixture();
        var events = new List<DiagnosticEvent>();
        var journal = new DiagnosticJournal(e => { events.Add(e); return Task.CompletedTask; });
        journal.SetDetailed(detailed);
        var profile = f.Model with
        {
            BatchSize = 4096,
            MicroBatchSize = 1024,
            CustomParameters = new()
            {
                Llama = [Row("--future", "PRIVATE_LITERAL_123")],
                Codex = [Row("features.future", "\"PRIVATE_LITERAL_456\"")],
            }
        };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(profile, f.Root), "logs.json");
        await ModelConfigurationFile.WriteAsync(path, profile, f.Root);
        await ModelStartupConfiguration.ResolveAsync(profile with { ConfigurationInitialized = true, PreferredConfigurationPath = path }, f.Root, diagnostics: journal);
        var json = JsonSerializer.Serialize(events);
        Assert.Contains("features.future", json);
        Assert.DoesNotContain("PRIVATE_LITERAL", json);
        Assert.Equal(detailed, json.Contains("microBatchSize"));
        Assert.Equal(detailed, json.Contains("customDelivery"));
        Assert.Contains("success", json);
    }

    [Theory]
    [InlineData("root", "future = 0 # original\n", "future", "7")]
    [InlineData("table", "[features]\nkeep = true # sibling\nfuture = 0 # original\n", "features.future", "7")]
    [InlineData("inline", "features = { keep = true, future = 0 } # original\n", "features.future", "7")]
    [InlineData("dotted", "features.future = 0 # original\nfeatures.keep = true\n", "features.future", "7")]
    public async Task OnlineAToBOnlineRestoresExactOriginalValuesAndPreservesSiblings(string _, string original, string key, string value)
    {
        using var f = new Fixture(original);
        await f.Service.ApplyLocalAsync(f.Request(Row(key, value)), f.Paths);
        var updated = await f.Service.UpdateLocalAsync(f.Request(Row(key, "9"), Row("new_field", "true")), f.Paths.RecoveryFile);
        await f.Service.CommitLocalUpdateAsync(f.Paths.RecoveryFile);
        Assert.Equal("9", new TomlRootDocument(await File.ReadAllTextAsync(f.Config)).GetCustomValue(key));
        Assert.Equal("0", updated.OriginalAssignments["custom:" + key]);
        // Restart uses the persisted transaction, not in-memory state.
        await f.NewService().RestoreOpenAIAsync(f.Paths.RecoveryFile);
        var restored = await File.ReadAllTextAsync(f.Config);
        Assert.Contains(original.TrimEnd(), restored);
        Assert.DoesNotContain("new_field", restored);
        Assert.Equal("original auth", File.ReadAllText(Path.Combine(f.Root, "auth.json")));
    }

    [Fact]
    public async Task AbsentFieldsAndOmittedAFieldsAndNewBFieldsAndRollbackAreRestored()
    {
        using var f = new Fixture("[features]\nkeep = true # preserved\n");
        await f.Service.ApplyLocalAsync(f.Request(Row("features.a", "1")), f.Paths);
        await f.Service.UpdateLocalAsync(f.Request(Row("features.b", "2")), f.Paths.RecoveryFile);
        var afterB = new TomlRootDocument(await File.ReadAllTextAsync(f.Config));
        Assert.Null(afterB.GetCustomValue("features.a"));
        Assert.Equal("2", afterB.GetCustomValue("features.b"));
        await f.NewService().RollbackLocalUpdateAsync(f.Paths.RecoveryFile);
        var rollback = new TomlRootDocument(await File.ReadAllTextAsync(f.Config));
        Assert.Equal("1", rollback.GetCustomValue("features.a"));
        Assert.Null(rollback.GetCustomValue("features.b"));
        await f.NewService().RestoreOpenAIAsync(f.Paths.RecoveryFile);
        Assert.Contains("keep = true # preserved", await File.ReadAllTextAsync(f.Config));
        Assert.Null(new TomlRootDocument(await File.ReadAllTextAsync(f.Config)).GetCustomValue("features.a"));
    }

    [Fact]
    public async Task EmptyOriginalConfigAndNoCustomBDoNotLeaveValuesBehind()
    {
        using var f = new Fixture();
        File.Delete(f.Config);
        await f.Service.ApplyLocalAsync(f.Request(Row("features.new", "true")), f.Paths);
        await f.Service.UpdateLocalAsync(f.Request(), f.Paths.RecoveryFile);
        await f.Service.CommitLocalUpdateAsync(f.Paths.RecoveryFile);
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        if (File.Exists(f.Config)) Assert.DoesNotContain("features", await File.ReadAllTextAsync(f.Config));
    }

    [Fact]
    public async Task ExternalEditOfOwnedFieldBlocksRestoreButUnrelatedEditSurvives()
    {
        using var f = new Fixture("future = 0\n[other]\nkeep = 1\n");
        await f.Service.ApplyLocalAsync(f.Request(Row("future", "7")), f.Paths);
        var local = await File.ReadAllTextAsync(f.Config);
        await File.WriteAllTextAsync(f.Config, local.Replace("future = 7", "future = 8"));
        await Assert.ThrowsAsync<ChatGptConfigConflictException>(() => f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile));
        Assert.Contains("future = 8", await File.ReadAllTextAsync(f.Config));
        await File.WriteAllTextAsync(f.Config, local.Replace("keep = 1", "keep = 2"));
        await f.Service.RestoreOpenAIAsync(f.Paths.RecoveryFile);
        var restored = await File.ReadAllTextAsync(f.Config);
        Assert.Contains("future = 0", restored);
        Assert.Contains("keep = 2", restored);
    }

    [Fact]
    public async Task DottedSiblingChangedDuringLocalSurvivesRestore()
    {
        using var f = new Fixture("[features]\nfuture = 0\nkeep = 1\n");
        await f.Service.ApplyLocalAsync(f.Request(Row("features.future", "7")), f.Paths);
        await File.WriteAllTextAsync(f.Config, (await File.ReadAllTextAsync(f.Config)).Replace("keep = 1", "keep = 2"));
        await f.NewService().RestoreOpenAIAsync(f.Paths.RecoveryFile);
        Assert.Contains("future = 0\nkeep = 2", await File.ReadAllTextAsync(f.Config));
    }

    [Theory]
    [InlineData("features = { keep = true }\n", "features.new", "true")]
    [InlineData("[[features]]\nx = 1\n", "features.new", "true")]
    [InlineData("[features]\nx = 1\n", "features", "true")]
    public async Task IncompatibleTomlShapeStopsBeforeAnyConfigWrite(string original, string key, string value)
    {
        using var f = new Fixture(original);
        var before = File.ReadAllBytes(f.Config);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.ApplyLocalAsync(f.Request(Row(key, value)), f.Paths));
        Assert.Equal(before, File.ReadAllBytes(f.Config));
    }

    [Fact]
    public async Task EndpointUpdateAndPreparedRecoveryRetainCustomOwnership()
    {
        using var f = new Fixture("future = 0\n");
        var applied = await f.Service.ApplyLocalAsync(f.Request(Row("future", "7")), f.Paths);
        await f.Service.UpdateLocalEndpointAsync(f.Paths.RecoveryFile, new Uri("http://127.0.0.1:19090/v1"), new LauncherSettings(), new LauncherSettings());
        var current = JsonSerializer.Deserialize<ManagedConfigSnapshot>(await File.ReadAllTextAsync(f.Paths.RecoveryFile))!;
        Assert.Equal("7", current.AppliedAssignments["custom:future"]);
        await f.Service.CommitLocalUpdateAsync(f.Paths.RecoveryFile);
        await f.Service.PrepareOpenAiRestoreAsync(f.Paths.RecoveryFile, new LauncherSettings(), new LauncherSettings());
        await f.NewService().RecoverInterruptedAsync(f.Paths.RecoveryFile);
        Assert.Contains("future = 0", await File.ReadAllTextAsync(f.Config));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "launcher-custom-parameters", Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(Root, "config.toml");
        public LauncherDataPaths Paths { get; }
        public ChatGptConfigTransactionService Service { get; }
        public ModelProfile Model => new() { Id = "model", Alias = "model", DisplayName = "Fixture", ModelRelativePath = "model.gguf", ModelType = ModelType.Dense, ContextSize = 16384, CompactionSafetyReserve = 4096 };
        public Fixture(string original = "")
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Config, "model = \"online\"\n" + original);
            File.WriteAllText(Path.Combine(Root, "auth.json"), "original auth");
            File.WriteAllText(Path.Combine(Root, "model.gguf"), "fixture");
            Paths = LauncherDataPaths.ForCurrentUser(Path.Combine(Root, "data"));
            LocalModelCatalogBuilder.WriteAtomicallyAsync(Path.Combine(Root, "catalog.json"), new LocalModelCatalogOptions { Slug = "model", DisplayName = "Fixture", ContextWindow = 16384 }).GetAwaiter().GetResult();
            Service = NewService();
        }
        public ChatGptConfigTransactionService NewService() => new(new Detector());
        public ChatGptLocalModeRequest Request(params CustomParameter[] rows) => new()
        {
            ConfigPath = Config,
            ModelSlug = "model",
            ModelCatalogPath = Path.Combine(Root, "catalog.json"),
            OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
            ContextWindow = 16384,
            AutoCompactTokenLimit = 12288,
            CustomParameters = rows,
        };
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Detector : IChatGptClientDetector { public bool IsRunning() => false; }
}
