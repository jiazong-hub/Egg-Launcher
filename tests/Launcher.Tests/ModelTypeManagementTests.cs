using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ModelTypeManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-model-types", Guid.NewGuid().ToString("N"));
    private readonly JsonModelProfileStore _store = new();
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ModelTypeSource = ModelTypeSource.Detected,
        ContextSize = 65_536,
        CompactionSafetyReserve = 20_480,
        GpuLayers = "7",
        ResourceCatalogInitialized = true,
    };
    private string RegistrationPath => Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), "model.json");
    private string ConfigurationPath(string name) => Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), name);

    public ModelTypeManagementTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts", "model-resources"));
        File.WriteAllText(Path.Combine(_root, Model.ModelRelativePath), "fixture-main");
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "fixture-runtime");
        File.WriteAllText(ModelResourceCatalog.FilePath(Model, _root), JsonSerializer.Serialize(new ModelResourceDocument(1, Model.Id, [])));
    }

    private async Task<ModelProfile> Initialize(ModelType type = ModelType.Dense)
    {
        var profile = Model with
        {
            ModelType = type,
            MoeExpertPlacement = type == ModelType.MoE ? MoeExpertPlacement.CpuFirstLayers : null,
            CpuMoeLayers = type == ModelType.MoE ? 4 : null,
        };
        await _store.SaveRegistrationAsync(_root, profile);
        return Assert.Single((await _store.LoadAsync(_root)).Profiles);
    }

    private Dictionary<string, string> SnapshotExceptRegistration() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Equals(RegistrationPath, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(path => Path.GetRelativePath(_root, path), ModelConfigurationFile.Digest, StringComparer.OrdinalIgnoreCase);

    private void AssertSnapshot(Dictionary<string, string> before)
    {
        var after = SnapshotExceptRegistration();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
    }

    [Theory]
    [InlineData(ModelType.Dense, ModelType.MoE)]
    [InlineData(ModelType.MoE, ModelType.Dense)]
    public async Task TypeRoundTrip_PreservesEveryConfigurationResourceAndArtifact(ModelType originalType, ModelType otherType)
    {
        var original = await Initialize(originalType);
        await ModelConfigurationFile.WriteAsync(ConfigurationPath("A.json"), original with { ContextSize = 32_768 }, _root);
        await ModelConfigurationFile.WriteAsync(ConfigurationPath("B.json"), original with { GpuLayers = "0" }, _root);
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        await File.WriteAllTextAsync(Path.Combine(ChatTemplateFile.GetDirectory(_root), "kept.jinja"), "{{ messages }}");
        await File.WriteAllTextAsync(Path.Combine(_root, "models", "draft.gguf"), "draft");
        foreach (var artifact in new[] { "model.bat", "router.ini", "catalog.json" })
            await File.WriteAllTextAsync(Path.Combine(_root, "scripts", artifact), "existing-artifact");
        var before = SnapshotExceptRegistration();
        var changed = await _store.UpdateModelTypeAsync(_root, original, otherType, () => true);
        Assert.Equal(original.PreferredConfigurationPath, changed.PreferredConfigurationPath);
        Assert.Equal(ModelTypeSource.UserSelected, changed.ModelTypeSource);
        var unavailable = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Equal(otherType, unavailable.ModelType);
        Assert.True(ModelConfigurationFile.IsTypeMismatch(unavailable.ConfigurationError));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(ConfigurationPath("A.json"), unavailable, _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(unavailable, _root));
        AssertSnapshot(before);
        await _store.UpdateModelTypeAsync(_root, changed, originalType, () => true);
        var restored = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Null(restored.ConfigurationError);
        Assert.Equal(original.PreferredConfigurationPath, restored.PreferredConfigurationPath);
        Assert.Equal(original.ContextSize, restored.ContextSize);
        Assert.Equal(original.GpuLayers, restored.GpuLayers);
        Assert.Equal(originalType, (await ModelStartupConfiguration.ResolveAsync(restored, _root)).ModelType);
        AssertSnapshot(before);
    }

    [Fact]
    public async Task TypeSave_ChangesOnlyTwoRegistrationFields_AndKeepsUnknownMetadata()
    {
        var original = await Initialize();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(RegistrationPath))!.AsObject();
        json["FutureMetadata"] = new JsonObject { ["Note"] = "keep-me" };
        await File.WriteAllTextAsync(RegistrationPath, json.ToJsonString());
        await _store.UpdateModelTypeAsync(_root, original, ModelType.MoE, () => true);
        var after = JsonNode.Parse(await File.ReadAllTextAsync(RegistrationPath))!.AsObject();
        foreach (var key in new[] { "ModelType", "ModelTypeSource" }) { json.Remove(key); after.Remove(key); }
        Assert.True(JsonNode.DeepEquals(json, after));
    }

    [Fact]
    public async Task HistoricalMoeParameters_InRegistrationDoNotHideDenseModel()
    {
        var original = await Initialize(ModelType.MoE);
        await _store.UpdateModelTypeAsync(_root, original, ModelType.Dense, () => true);
        var current = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.True(ModelConfigurationFile.IsTypeMismatch(current.ConfigurationError));
        Assert.Equal(MoeExpertPlacement.CpuFirstLayers, current.MoeExpertPlacement);
        var draft = ModelConfigurationFile.CreateUnloadedDraft(current);
        Assert.Equal(ModelType.Dense, draft.ModelType);
        Assert.Null(draft.MoeExpertPlacement);
        Assert.Null(draft.CpuMoeLayers);
        Assert.Null(draft.DefaultParameters);
        Assert.Equal(16_384, draft.ContextSize);
        Assert.Equal("auto", draft.GpuLayers);
        Assert.Empty(ModelProfileValidator.Validate(draft, _root));
        Assert.Equal(original.PreferredConfigurationPath, draft.PreferredConfigurationPath);
    }

    [Fact]
    public async Task NewTypeConfiguration_RequiresExplicitSave_AndDoesNotReplaceOldSelection()
    {
        var original = await Initialize();
        var oldBytes = await File.ReadAllBytesAsync(original.PreferredConfigurationPath!);
        await _store.UpdateModelTypeAsync(_root, original, ModelType.MoE, () => true);
        var current = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        var before = SnapshotExceptRegistration();
        var draft = ModelConfigurationFile.CreateUnloadedDraft(current);
        AssertSnapshot(before);
        var newPath = ConfigurationPath("MoE.json");
        await ModelConfigurationFile.WriteAsync(newPath, draft, _root);
        Assert.Equal(ModelType.MoE, (await ModelConfigurationFile.ReadAsync(newPath, current, _root)).ModelType);
        var stillUnavailable = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Equal(original.PreferredConfigurationPath, stillUnavailable.PreferredConfigurationPath);
        Assert.True(ModelConfigurationFile.IsTypeMismatch(stillUnavailable.ConfigurationError));
        await _store.SaveRegistrationAsync(_root, draft with { PreferredConfigurationPath = newPath });
        var selected = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Null(selected.ConfigurationError);
        Assert.Equal(newPath, selected.PreferredConfigurationPath);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(original.PreferredConfigurationPath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceGuard_RejectsChangesWithoutWriting(bool changesDuringSave)
    {
        var original = await Initialize();
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        var snapshot = SnapshotExceptRegistration();
        var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.UpdateModelTypeAsync(_root, original,
            ModelType.MoE, () => changesDuringSave && ++checks == 1));
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
        AssertSnapshot(snapshot);
    }

    [Fact]
    public async Task ExternalMetadataEdit_IsPreservedWhenTypeSaveFails()
    {
        var original = await Initialize();
        var checks = 0;
        await Assert.ThrowsAsync<IOException>(() => _store.UpdateModelTypeAsync(_root, original, ModelType.MoE, () =>
        {
            if (++checks == 2)
            {
                var json = JsonNode.Parse(File.ReadAllText(RegistrationPath))!;
                json["DisplayName"] = "changed-externally";
                File.WriteAllText(RegistrationPath, json.ToJsonString());
            }
            return true;
        }));
        var retained = await _store.ReadRegistrationAsync(_root, original);
        Assert.Equal("changed-externally", retained.DisplayName);
        Assert.Equal(ModelType.Dense, retained.ModelType);
    }

    [Fact]
    public async Task StaleManager_CannotOverwriteAnotherTypeChange()
    {
        var original = await Initialize();
        await _store.UpdateModelTypeAsync(_root, original, ModelType.MoE, () => true);
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        await Assert.ThrowsAsync<IOException>(() => _store.UpdateModelTypeAsync(_root, original, ModelType.Dense, () => true));
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
    }

    [Theory]
    [InlineData(ModelType.Unknown)]
    [InlineData((ModelType)999)]
    public async Task UnsupportedType_IsRejected(ModelType type)
    {
        var original = await Initialize();
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.UpdateModelTypeAsync(_root, original, type, () => true));
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
    }

    [Fact]
    public async Task SameType_DoesNotRewriteRegistrationOrConfigurations()
    {
        var original = await Initialize();
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        var snapshot = SnapshotExceptRegistration();
        await _store.UpdateModelTypeAsync(_root, original, original.ModelType, () => true);
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
        AssertSnapshot(snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDamagedConfiguration_IsNeverRecreatedOrRepairedByTypeChange(bool damaged)
    {
        var original = await Initialize();
        if (damaged) await File.WriteAllTextAsync(original.PreferredConfigurationPath!, "{");
        else File.Delete(original.PreferredConfigurationPath!);
        var snapshot = SnapshotExceptRegistration();
        await _store.UpdateModelTypeAsync(_root, original, ModelType.MoE, () => true);
        AssertSnapshot(snapshot);
        Assert.NotNull(Assert.Single((await _store.LoadAsync(_root)).Profiles).ConfigurationError);
        AssertSnapshot(snapshot);
    }

    [Fact]
    public async Task ReferenceInspection_ProtectsFilesUsedByOtherTypesWithoutLoadingTheirParameters()
    {
        var original = await Initialize(ModelType.MoE);
        var selected = original with { MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = "models/draft.gguf", MtpEnabled = false };
        await ModelConfigurationFile.WriteAsync(ConfigurationPath("MoeReference.json"), selected, _root);
        var changed = await _store.UpdateModelTypeAsync(_root, original, ModelType.Dense, () => true);
        await File.WriteAllTextAsync(ModelResourceCatalog.FilePath(Model, _root) + ".lock", "");
        var before = SnapshotExceptRegistration();
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(ConfigurationPath("MoeReference.json"), changed, _root));
        var references = await ModelResourceManagement.FindReferencesAsync(changed, _root, ModelResourceKind.Mtp, "models/draft.gguf");
        Assert.Contains("MoeReference.json", references);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceManagement.RemoveAsync(changed, _root,
            ModelResourceKind.Mtp, "models/draft.gguf", () => true));
        AssertSnapshot(before);
    }

    [Fact]
    public async Task TypeMismatchMessage_DescribesBothTypes_WithoutRelaxingModelBinding()
    {
        var original = await Initialize();
        var other = original with { ModelType = ModelType.MoE };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(original.PreferredConfigurationPath!, other, _root));
        Assert.True(ModelConfigurationFile.IsTypeMismatch(error.Message));
        Assert.Contains("Dense", error.Message);
        Assert.Contains("MoE", error.Message);
        var wrong = other with { ModelRelativePath = "models/other.gguf" };
        var wrongModel = await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(original.PreferredConfigurationPath!, wrong, _root));
        Assert.False(ModelConfigurationFile.IsTypeMismatch(wrongModel.Message));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
