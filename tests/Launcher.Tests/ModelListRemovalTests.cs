using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ModelListRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-model-removal", Guid.NewGuid().ToString("N"));
    private readonly JsonModelProfileStore _profiles = new();
    private readonly JsonSettingsStore _settingsStore;
    private LauncherSettings _settings = new();
    private string Registry => Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), "model.json");
    private string Batch => BatchScriptGenerator.GetOutputPath(_root, "model");
    private string Preset => Path.Combine(_root, "state", "router.ini");
    private ModelProfile Model => new() { Id = "model", Alias = "model", DisplayName = "Model", ModelRelativePath = "models/main.gguf", ModelType = ModelType.Dense, ContextSize = 16384, CompactionSafetyReserve = 4096 };

    public ModelListRemovalTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        File.WriteAllText(Path.Combine(_root, "models", "main.gguf"), "main model");
        File.WriteAllText(Path.Combine(_root, "models", "draft.gguf"), "MTP companion");
        File.WriteAllText(Path.Combine(_root, "models", "vision.gguf"), "vision companion");
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "runtime fixture");
        File.WriteAllText(Preset, "current model B router preset");
        File.WriteAllText(Path.Combine(_root, "state", "catalog.json"), "current model B catalog");
        File.WriteAllText(Path.Combine(_root, "state", "applied-local-run.json"), "current model B run snapshot");
        _settingsStore = new JsonSettingsStore(Path.Combine(_root, "state", "settings.json"));
    }

    private async Task<ModelProfile> RegisterAsync()
    {
        await _profiles.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _profiles.LoadAsync(_root)).Profiles);
        var template = Path.Combine(_root, "scripts", "templates", "model.codex-compatible.jinja");
        Directory.CreateDirectory(Path.GetDirectoryName(template)!);
        await File.WriteAllTextAsync(template, CodexChatTemplateCompatibility.OwnershipMarker + "\n{{ messages }}");
        model = model with { ChatTemplateRelativePath = Path.GetRelativePath(_root, template) };
        await ModelResourceCatalog.RegisterAsync(model, _root, ModelResourceKind.Template, defaultTemplate: true);
        await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, model, _root);
        var selected = Path.Combine(ModelConfigurationFile.GetDirectory(model, _root), "fast.json");
        await ModelConfigurationFile.WriteAsync(selected, model with { ContextSize = 32768, GpuLayers = "0" }, _root);
        model = model with { PreferredConfigurationPath = selected };
        await _profiles.SaveRegistrationAsync(_root, model);
        File.Copy(Registry, Registry + ".bak");
        _profiles.MarkMissing(_root, model.Id);
        await BatchScriptGenerator.WriteOwnedAsync(_root, model);
        return model;
    }

    private async Task SetSettingsAsync(ProviderMode mode, string? selectedId, ProviderMode? pendingMode = ProviderMode.Local, string? pendingId = "model")
    {
        _settings = new LauncherSettings
        {
            SelectedMode = mode,
            SelectedModelId = selectedId,
            LlamaRoot = _root,
            PendingMode = pendingMode,
            PendingModelId = pendingId,
            PendingModelRelativePath = pendingId is null ? null : "models/main.gguf",
            PendingModelDisplayName = pendingId is null ? null : "Pending model",
            PendingSelectionInitialized = true,
        };
        await _settingsStore.SaveAsync(_settings);
    }

    private async Task<LauncherSettings> MutateAsync(Func<LauncherSettings, LauncherSettings> mutation, CancellationToken token)
    {
        var updated = mutation(_settings);
        await _settingsStore.SaveAsync(updated, token);
        return _settings = updated;
    }

    private Task RemoveAsync(ModelProfile model) => ModelListRemoval.RemoveAsync(model, _root, Preset, () => _settings, MutateAsync);
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    private static void AssertUnchanged(Dictionary<string, byte[]> snapshot)
    {
        foreach (var (path, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    private Dictionary<string, byte[]> PreservedFiles() => Snapshot().Where(pair => pair.Key != Registry && pair.Key != Registry + ".bak"
        && pair.Key != Path.ChangeExtension(Registry, ".missing") && pair.Key != Batch
        && !pair.Key.StartsWith(Path.Combine(_root, "state", "settings.json"), StringComparison.OrdinalIgnoreCase)).ToDictionary();

    [Theory]
    [InlineData(ProviderMode.OpenAI, null)]
    [InlineData(ProviderMode.Local, "other")]
    public async Task SwitchedToOnlineOrAnotherModel_RemovesOnlyRegistrationBatchAndMatchingPreselection(ProviderMode mode, string? selectedId)
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(mode, selectedId);
        var preserved = PreservedFiles();
        await RemoveAsync(model);
        Assert.False(File.Exists(Registry));
        Assert.False(File.Exists(Registry + ".bak"));
        Assert.False(_profiles.IsMarkedMissing(_root, model.Id));
        Assert.False(File.Exists(Batch));
        AssertUnchanged(preserved);
        var settings = await _settingsStore.LoadAsync();
        Assert.Equal(mode, settings.SelectedMode);
        Assert.Equal(selectedId, settings.SelectedModelId);
        Assert.Null(settings.PendingMode);
        Assert.Null(settings.PendingModelId);
        Assert.Null(settings.PendingModelRelativePath);
        Assert.Null(settings.PendingModelDisplayName);
        Assert.True(settings.PendingSelectionInitialized);
        Assert.Empty((await _profiles.LoadAsync(_root)).Profiles);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(ProviderMode.OpenAI, null)]
    [InlineData(ProviderMode.Local, "other")]
    public async Task AppliedModel_CannotBeRemovedEvenWhenStoppedOrAnotherModeIsOnlyPreselected(ProviderMode? pendingMode, string? pendingId)
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.Local, model.Id, pendingMode, pendingId);
        var before = Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RemoveAsync(model));
        AssertUnchanged(before);
        Assert.Equal(model.Id, (await _settingsStore.LoadAsync()).SelectedModelId);
    }

    [Fact]
    public async Task RemovingOtherModel_DoesNotClearAnotherModelsPreselection()
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.Local, "other", ProviderMode.Local, "other");
        var before = await _settingsStore.LoadAsync();
        await RemoveAsync(model);
        Assert.Equal(before, await _settingsStore.LoadAsync());
    }

    [Fact]
    public async Task SettingsFailure_RestoresRegistrationBatchAndMissingMarker_WithoutRevertingExternalParameterOrTemplateEdits()
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.Local, "other");
        var before = Snapshot();
        var template = Path.Combine(_root, model.ChatTemplateRelativePath!);
        await Assert.ThrowsAsync<IOException>(() => ModelListRemoval.RemoveAsync(model, _root, Preset, () => _settings,
            async (_, _) =>
            {
                Assert.False(File.Exists(Registry));
                Assert.False(File.Exists(Batch));
                await File.WriteAllTextAsync(model.PreferredConfigurationPath!, "external configuration update");
                await File.WriteAllTextAsync(template, "external template update");
                throw new IOException("settings write failed");
            }));
        before.Remove(model.PreferredConfigurationPath!);
        before.Remove(template);
        AssertUnchanged(before);
        Assert.Equal("external configuration update", await File.ReadAllTextAsync(model.PreferredConfigurationPath!));
        Assert.Equal("external template update", await File.ReadAllTextAsync(template));
    }

    [Fact]
    public async Task UnownedBat_IsNotDeleted_AndPreventsRegistrationOrSettingsRemoval()
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.OpenAI, null);
        await File.WriteAllTextAsync(Batch, "@echo off\nuser-owned script\n");
        var before = Snapshot();
        await Assert.ThrowsAsync<BatchScriptOwnershipException>(() => RemoveAsync(model));
        AssertUnchanged(before);
    }

    [Fact]
    public async Task AppliedSelectionChangesBeforeCommit_RollsBackRemovalInsteadOfClearingNewSettings()
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.Local, "other");
        var preserved = Snapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelListRemoval.RemoveAsync(model, _root, Preset, () => _settings,
            (mutation, _) =>
            {
                _settings = _settings with { SelectedModelId = model.Id };
                return Task.FromResult(mutation(_settings));
            }));
        AssertUnchanged(preserved);
        Assert.Equal(model.Id, _settings.SelectedModelId);
    }

    [Fact]
    public async Task ReaddingSameModel_PreservesOldConfigurationsAndValidEvidence_WithoutRestoringOldPreselection()
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.OpenAI, null);
        var configurationDirectory = ModelConfigurationFile.GetDirectory(model, _root);
        var oldFiles = Directory.GetFiles(configurationDirectory).ToDictionary(p => p, File.ReadAllBytes);
        await RemoveAsync(model);
        await _profiles.SaveRegistrationAsync(_root, ModelResourceCatalog.ApplyDefaultTemplate(Model, _root));
        var readded = Assert.Single((await _profiles.LoadAsync(_root)).Profiles);
        Assert.NotEqual(model.PreferredConfigurationPath, readded.PreferredConfigurationPath);
        Assert.Equal("未命名-2.json", Path.GetFileName(readded.PreferredConfigurationPath));
        AssertUnchanged(oldFiles);
        await ModelResourceCatalog.EnsureMigratedAsync(readded, _root);
        Assert.Contains(ModelResourceCatalog.Read(readded, _root).Records, r => ModelResourceCatalog.IsCurrent(r, _root));
        Assert.Equal(32768, (await ModelConfigurationFile.ReadAsync(model.PreferredConfigurationPath!, readded, _root)).ContextSize);
        Assert.Equal(model.ChatTemplateRelativePath, readded.ChatTemplateRelativePath);
    }

    [Fact]
    public async Task MissingParameterJson_DoesNotPreventUnregisteringInactiveModel()
    {
        var model = await RegisterAsync();
        await SetSettingsAsync(ProviderMode.OpenAI, null);
        File.Delete(model.PreferredConfigurationPath!);
        var visible = Assert.Single((await _profiles.LoadAsync(_root)).Profiles);
        Assert.NotNull(visible.ConfigurationError);
        var preserved = PreservedFiles();
        await RemoveAsync(visible);
        AssertUnchanged(preserved);
        Assert.False(File.Exists(Registry));
    }

    public void Dispose()
    {
        _settingsStore.Dispose();
        Directory.Delete(_root, true);
    }
}
