using System.IO.Pipes;
using Launcher.Core.Agent;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;

namespace Launcher.Tests;

public sealed class AuditBoundaryFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedRollback_PreservesExternalChanges(bool deleteConfiguration)
    {
        var root = CreateRoot();
        try
        {
            var model = await CreateModelAsync(root);
            var registration = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(root), model.Id + ".json");
            var template = Path.Combine(root, "scripts", "templates", model.Id + ".codex-compatible.jinja");
            Directory.CreateDirectory(Path.GetDirectoryName(template)!);
            await File.WriteAllTextAsync(template, "old template");
            var batch = BatchScriptGenerator.GetOutputPath(root, model.Id);
            var preset = Path.Combine(root, "state", "preset.ini");
            var catalog = Path.Combine(root, "state", "catalog.json");
            foreach (var path in new[] { batch, preset, catalog })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "old generated");
            }
            await Assert.ThrowsAsync<IOException>(() => new ModelArtifactTransaction().ExecuteAsync<bool>(
                root, model.Id, preset, true, async token =>
                {
                    foreach (var path in new[] { batch, preset, catalog })
                        await File.WriteAllTextAsync(path, "partial generated", token);
                    await File.WriteAllTextAsync(registration, "external registration edit", token);
                    await File.WriteAllTextAsync(template, "external template edit", token);
                    if (deleteConfiguration) File.Delete(model.PreferredConfigurationPath!);
                    else await File.WriteAllTextAsync(model.PreferredConfigurationPath!, "external configuration edit", token);
                    throw new IOException("injected write failure");
                }, localModelCatalogPath: catalog, generatedFilesOnly: true));
            Assert.Equal("external registration edit", await File.ReadAllTextAsync(registration));
            Assert.Equal("external template edit", await File.ReadAllTextAsync(template));
            if (deleteConfiguration) Assert.False(File.Exists(model.PreferredConfigurationPath));
            else Assert.Equal("external configuration edit", await File.ReadAllTextAsync(model.PreferredConfigurationPath!));
            foreach (var path in new[] { batch, preset, catalog })
                Assert.Equal("old generated", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PreferenceEdit_RetainsMigratedPortAndConcurrentPreference()
    {
        var root = CreateRoot();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(root);
            using var app = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            using var agent = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            await app.SaveAsync(new LauncherSettings { RouterPort = 18080 });
            _ = await app.LoadAsync(); // A previously loaded UI snapshot must not be the write base.
            await agent.SaveAsync(new LauncherSettings { RouterPort = 19090 });
            await Task.WhenAll(
                SettingsMutationCoordinator.UpdateAsync(app, paths, value => value with { DetailedDiagnosticsEnabled = true }),
                SettingsMutationCoordinator.UpdateAsync(agent, paths, value => value with { PublicProxyPortInitialized = true }));
            var saved = await app.LoadAsync();
            Assert.Equal(19090, saved.RouterPort);
            Assert.True(saved.DetailedDiagnosticsEnabled);
            Assert.True(saved.PublicProxyPortInitialized);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PreferenceEdit_WaitsForModeTransaction_ThenReadsLatestSettings()
    {
        var root = CreateRoot();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(root);
            using var app = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            using var agent = new JsonSettingsStore(Path.Combine(root, "settings.json"));
            await app.SaveAsync(new LauncherSettings { RouterPort = 18080 });
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ModeSwitchLockFile)!);
            Task<LauncherSettings> update;
            await using (var modeLock = new FileStream(paths.ModeSwitchLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                update = SettingsMutationCoordinator.UpdateAsync(app, paths, value => value with { DetailedDiagnosticsEnabled = true });
                await agent.SaveAsync(new LauncherSettings { RouterPort = 19090 });
                Assert.False(update.IsCompleted);
            }
            var result = await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(19090, result.RouterPort);
            Assert.True(result.DetailedDiagnosticsEnabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewStartup_RejectsUnverifiedOrUnsupportedShifting(bool negativeProof)
    {
        var root = CreateRoot();
        try
        {
            var model = await CreateModelAsync(root);
            model = model with { ContextShiftEnabled = true };
            await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, model, root);
            if (negativeProof)
                ContextShiftCapabilityCache.Write(model, root, new(false, "unsupported", DateTimeOffset.UtcNow));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(model, root));
            Assert.Contains("上下文滚动", error.Message);
            Assert.True((await ModelConfigurationFile.ReadAsync(model.PreferredConfigurationPath!, model, root)).ContextShiftEnabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NewStartup_RejectsRuntimeOptionsThatCannotBeConfirmed()
    {
        var root = CreateRoot();
        try
        {
            var model = await CreateModelAsync(root);
            model = model with { ExtraArguments = new Dictionary<string, string?> { ["keep"] = "10" } };
            await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, model, root);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(model, root));
            Assert.Contains("--keep", error.Message);
            model = model with { ExtraArguments = new Dictionary<string, string?>(), ContextShiftEnabled = true };
            await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, model, root);
            ContextShiftCapabilityCache.Write(model, root, new(true, "previously checked", DateTimeOffset.UtcNow));
            error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(model, root));
            Assert.Contains("--context-shift", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ControlPipe_DisconnectedClient_DoesNotPreventNextShutdown()
    {
        var pipe = "EggLauncher.FixTests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var shutdown = false;
        var listener = AgentControlPipe.ListenForShutdownAsync(() => shutdown = true, cancellation.Token, pipe);
        try
        {
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
                await client.ConnectAsync(cancellation.Token);
            var result = await AgentControlPipe.RequestShutdownAsync(TimeSpan.FromSeconds(5), cancellation.Token, pipe);
            Assert.True(result.Succeeded, result.Diagnostic);
            await listener.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(shutdown);
        }
        finally
        {
            cancellation.Cancel();
            try { await listener; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ControlPipe_SilentClient_ReleasesListenerAfterCommandTimeout()
    {
        var pipe = "EggLauncher.FixTests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var listener = AgentControlPipe.ListenForShutdownAsync(() => { }, cancellation.Token, pipe);
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellation.Token);
            var result = await AgentControlPipe.RequestShutdownAsync(TimeSpan.FromSeconds(8), cancellation.Token, pipe);
            Assert.True(result.Succeeded, result.Diagnostic);
            await listener;
        }
        finally
        {
            cancellation.Cancel();
            try { await listener; } catch (OperationCanceledException) { }
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "EggLauncher.AuditFixTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<ModelProfile> CreateModelAsync(string root)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "model.gguf"), "fixture");
        var store = new JsonModelProfileStore();
        await store.SaveAsync(root, new ModelProfile
        {
            Id = "model",
            Alias = "model",
            DisplayName = "Model",
            ModelRelativePath = "model.gguf",
            ModelType = ModelType.Dense,
            ContextSize = 16384,
            CompactionSafetyReserve = 2048,
        });
        return Assert.Single((await store.LoadAsync(root)).Profiles);
    }
}
