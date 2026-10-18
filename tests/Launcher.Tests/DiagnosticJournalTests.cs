using System.Text.Json;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Orchestration.Models;

namespace Launcher.Tests;

public sealed class DiagnosticJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-diagnostic-journal", Guid.NewGuid().ToString("N"));
    private string Concise => Path.Combine(_root, "launcher.concise.jsonl");
    private string Full => Path.Combine(_root, "launcher.full.jsonl");
    private DiagnosticJournal Journal(bool detailed) => new(new ModeAwareJsonLineDiagnosticLog(Concise, Full, detailed));

    [Fact]
    public async Task PreferenceProvenance_DistinguishesMissingFieldFromExplicitFalseWithoutExtraFile()
    {
        using var store = new JsonSettingsStore(Path.Combine(_root, "settings.json"));
        await store.LoadAsync();
        Assert.True(store.LastLoadUsedAuthenticationDefault);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{\"SchemaVersion\":2}");
        await store.LoadAsync();
        Assert.True(store.LastLoadUsedAuthenticationDefault);
        await store.SaveAsync(new LauncherSettings { LocalRequiresOpenAiAuth = false });
        Assert.False((await store.LoadAsync()).LocalRequiresOpenAiAuth);
        Assert.False(store.LastLoadUsedAuthenticationDefault);
        Assert.DoesNotContain("LastLoadUsedAuthenticationDefault", await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modes_WriteOneResultAndDetailedStagesToOnlyTheSelectedFile(bool detailed)
    {
        var journal = Journal(detailed);
        var result = await journal.RunAsync("configuration_save", new Dictionary<string, object?> { ["modelId"] = "fixture" },
            async operation => { await operation.StageAsync("write_configuration"); return 42; });
        Assert.Equal(42, result);
        Assert.False(File.Exists(detailed ? Concise : Full));
        var records = Read(detailed ? Full : Concise);
        Assert.Equal(detailed ? 3 : 1, records.Length);
        Assert.Single(records, record => record.GetProperty("eventName").GetString() == "configuration_save");
        Assert.Single(records.Select(record => record.GetProperty("properties").GetProperty("operationId").GetString()).Distinct());
        foreach (var record in records)
        {
            Assert.Equal(2, record.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(detailed ? "full" : "concise", record.GetProperty("diagnosticMode").GetString());
            Assert.Equal(detailed, record.GetProperty("properties").TryGetProperty("durationMs", out _));
        }
    }

    [Fact]
    public async Task SwitchingModes_DoesNotDuplicateEventsOrKeepWritingToOldMode()
    {
        var journal = Journal(false);
        await journal.WriteAsync("first", "success");
        journal.SetDetailed(true);
        await journal.WriteAsync("detail", "success", detailedOnly: true);
        await journal.WriteAsync("second", "success");
        journal.SetDetailed(false);
        await journal.WriteAsync("filtered", "success", detailedOnly: true);
        await journal.WriteAsync("third", "success");
        Assert.Equal(new[] { "first", "third" }, Read(Concise).Select(record => record.GetProperty("eventName").GetString()));
        Assert.Equal(new[] { "detail", "second" }, Read(Full).Select(record => record.GetProperty("eventName").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failures_PreserveOriginalExceptionAndOmitArbitraryMessageBodies(bool detailed)
    {
        var journal = Journal(detailed);
        var expected = new InvalidDataException("PRIVATE_CONFIG_AND_RESPONSE_BODY");
        var actual = await Assert.ThrowsAsync<InvalidDataException>(() => journal.RunAsync<int>("startup_configuration", null,
            async operation => { await operation.StageAsync("read_configuration"); throw expected; }));
        Assert.Same(expected, actual);
        var content = await File.ReadAllTextAsync(detailed ? Full : Concise);
        Assert.DoesNotContain("PRIVATE_CONFIG_AND_RESPONSE_BODY", content);
        var terminal = Read(detailed ? Full : Concise).Last();
        Assert.Equal("error", terminal.GetProperty("level").GetString());
        var fields = terminal.GetProperty("properties");
        Assert.Equal("failed", fields.GetProperty("outcome").GetString());
        Assert.Equal("validation_failed", fields.GetProperty("reasonCode").GetString());
        Assert.Equal("read_configuration", fields.GetProperty("stage").GetString());
        Assert.Equal(detailed, fields.TryGetProperty("stackTrace", out _));
    }

    [Fact]
    public async Task SensitiveProperties_UseExistingSanitizer()
    {
        var journal = Journal(true);
        await journal.WriteAsync("privacy", "success", new Dictionary<string, object?>
        { ["token"] = "PRIVATE_TOKEN", ["requestBody"] = "PRIVATE_BODY", ["safe"] = false });
        var text = await File.ReadAllTextAsync(Full);
        Assert.DoesNotContain("PRIVATE_TOKEN", text);
        Assert.DoesNotContain("PRIVATE_BODY", text);
        Assert.False(Read(Full).Single().GetProperty("properties").GetProperty("safe").GetBoolean());
    }

    [Fact]
    public async Task ThrowingSink_DoesNotChangeSuccessFailureOrCancellation()
    {
        var journal = new DiagnosticJournal(_ => throw new IOException("fixture logging failure"));
        journal.SetDetailed(true);
        Assert.Equal(17, await journal.RunAsync("save", null, _ => Task.FromResult(17)));
        var expected = new InvalidOperationException("business failure");
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RunAsync<int>("save", null, _ => throw expected)));
        var cancelled = new OperationCanceledException();
        Assert.Same(cancelled, await Assert.ThrowsAsync<OperationCanceledException>(() => journal.RunAsync<int>("save", null, _ => throw cancelled)));
        await journal.AppendEventAsync(new DiagnosticEvent("native", "error"));
    }

    [Fact]
    public async Task UnwritableDirectory_DoesNotBlockUnderlyingFileSave()
    {
        Directory.CreateDirectory(_root);
        var blockedDirectory = Path.Combine(_root, "occupied");
        await File.WriteAllTextAsync(blockedDirectory, "fixture");
        var journal = new DiagnosticJournal(new ModeAwareJsonLineDiagnosticLog(Path.Combine(blockedDirectory, "concise"), Path.Combine(blockedDirectory, "full")));
        var target = Path.Combine(_root, "saved.json");
        await journal.RunAsync("save", null, async _ => { await File.WriteAllTextAsync(target, "saved"); return true; });
        Assert.Equal("saved", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ExplicitOutcomeAndDisposal_DoNotEmitFalseSuccessOrDuplicateResults()
    {
        var journal = Journal(false);
        await journal.RunAsync("template_add", null, async operation => { await operation.CompleteAsync("blocked", "already_registered"); return false; });
        await using (journal.Begin("configuration_save_as")) { }
        var events = Read(Concise);
        Assert.Equal(2, events.Length);
        Assert.Equal("blocked", events[0].GetProperty("properties").GetProperty("outcome").GetString());
        Assert.Equal("cancelled", events[1].GetProperty("properties").GetProperty("outcome").GetString());
        await journal.WriteAsync("failed_without_exception", "failed");
        Assert.Equal("error", Read(Concise).Last().GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealModeTransactions_LogRequestedAndAppliedAuthAndRestoreDespiteBrokenLog(bool auth)
    {
        var profile = PrepareRuntime();
        var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(_root, "data"));
        using var store = new JsonSettingsStore(paths.SettingsFile);
        await store.SaveAsync(new LauncherSettings { LocalRequiresOpenAiAuth = auth });
        var config = Path.Combine(_root, "config.toml");
        await File.WriteAllTextAsync(config, "model = \"official\"\n");
        var journal = Journal(true);
        var detector = new FixedDetector();
        var transactions = new ChatGptConfigTransactionService(detector);
        var coordinator = new ModeSwitchCoordinator(store, detector, transactions, paths, journal);
        await coordinator.SwitchToLocalAsync(new LocalModeSwitchRequest { CodexHome = _root, RuntimeRoot = _root, Profile = profile, RouterPort = 18080 });
        var result = Read(Full).Single(record => record.GetProperty("eventName").GetString() == "local_mode_apply").GetProperty("properties");
        Assert.Equal(auth, result.GetProperty("requestedRequiresOpenAiAuth").GetBoolean());
        Assert.Equal(auth, result.GetProperty("appliedRequiresOpenAiAuth").GetBoolean());
        Assert.Contains("requires_openai_auth = " + (auth ? "true" : "false"), await File.ReadAllTextAsync(config));
        var brokenJournal = new DiagnosticJournal(_ => throw new UnauthorizedAccessException());
        await new ModeSwitchCoordinator(store, detector, transactions, paths, brokenJournal).SwitchToOpenAIAsync();
        Assert.Contains("model = \"official\"", await File.ReadAllTextAsync(config));
        Assert.Equal(auth, (await store.LoadAsync()).LocalRequiresOpenAiAuth);
    }

    [Fact]
    public async Task Startup_LogsMissingSelectedFileWithReadStageAndNoFalseSuccess()
    {
        var profile = PrepareRuntime();
        var store = new JsonModelProfileStore();
        await store.SaveRegistrationAsync(_root, profile);
        var model = (await store.LoadAsync(_root)).Profiles.Single();
        File.Delete(model.PreferredConfigurationPath!);
        await Assert.ThrowsAnyAsync<Exception>(() => ModelStartupConfiguration.ResolveAsync(model, _root, diagnostics: Journal(true)));
        var result = Read(Full).Single(record => record.GetProperty("eventName").GetString() == "startup_configuration").GetProperty("properties");
        Assert.Equal("failed", result.GetProperty("outcome").GetString());
        Assert.Equal("read_configuration", result.GetProperty("stage").GetString());
        Assert.Equal(model.Id, result.GetProperty("modelId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_RecordsCommitFailureAndCompletedRecoveryWithoutClaimingAuthWasApplied(bool update)
    {
        var profile = PrepareRuntime();
        var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(_root, "data"));
        using var store = new JsonSettingsStore(paths.SettingsFile);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.toml"), "model = \"official\"\n");
        var journal = Journal(true);
        var detector = new FixedDetector();
        var transactions = new ChatGptConfigTransactionService(detector);
        var request = new LocalModeSwitchRequest { CodexHome = _root, RuntimeRoot = _root, Profile = profile, RouterPort = 18080 };
        if (update) await new ModeSwitchCoordinator(store, detector, transactions, paths, journal).SwitchToLocalAsync(request);
        await store.SaveAsync((await store.LoadAsync()) with { LocalRequiresOpenAiAuth = true });
        await Assert.ThrowsAsync<IOException>(() => new ModeSwitchCoordinator(new FailOnceStore(store), detector, transactions, paths, journal).SwitchToLocalAsync(request));
        var result = Read(Full).Last(record => record.GetProperty("eventName").GetString() == "local_mode_apply").GetProperty("properties");
        Assert.Equal("failed", result.GetProperty("outcome").GetString());
        Assert.Equal("commit_launcher_settings", result.GetProperty("failureStage").GetString());
        Assert.Equal("completed", result.GetProperty(update ? "rollback" : "configurationRollback").GetString());
        Assert.Equal("completed", result.GetProperty("generatedFilesRollback").GetString());
        Assert.False(result.TryGetProperty("appliedRequiresOpenAiAuth", out _));
        Assert.True((await store.LoadAsync()).LocalRequiresOpenAiAuth);
    }

    private ModelProfile PrepareRuntime()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "fixture-runtime");
        File.WriteAllText(Path.Combine(_root, "models", "model.gguf"), "fixture-model");
        return new ModelProfile { Id = "fixture", Alias = "fixture", DisplayName = "Fixture", ModelRelativePath = "models/model.gguf", ModelType = ModelType.Dense, ContextSize = 16384, CompactionSafetyReserve = 4096 };
    }
    private static JsonElement[] Read(string path) => File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private sealed class FixedDetector : IChatGptClientDetector { public bool IsRunning() => false; }
    private sealed class FailOnceStore(ISettingsStore inner) : ISettingsStore
    {
        private bool _fail = true;
        public Task<LauncherSettings> LoadAsync(CancellationToken token = default) => inner.LoadAsync(token);
        public Task SaveAsync(LauncherSettings settings, CancellationToken token = default)
        {
            if (!_fail) return inner.SaveAsync(settings, token);
            _fail = false;
            return Task.FromException(new IOException("controlled commit failure"));
        }
    }
}
