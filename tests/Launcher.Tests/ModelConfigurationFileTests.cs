using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ModelConfigurationFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-configurations", Guid.NewGuid().ToString("N"));
    private readonly JsonModelProfileStore _store = new();
    private string ConfigurationPath(string fileName) => Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), fileName);
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 16384,
        CompactionSafetyReserve = 4096,
    };

    public ModelConfigurationFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        File.WriteAllText(Path.Combine(_root, "models", "main.gguf"), "fixture-model");
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "fixture-runtime");
    }

    [Fact]
    public async Task Migration_CreatesOneActualUnnamedConfiguration_WithoutBackupOrRuntimeArtifacts()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.True(File.Exists(model.PreferredConfigurationPath));
        Assert.Equal("未命名.json", Path.GetFileName(model.PreferredConfigurationPath));
        Assert.Empty(Directory.GetFiles(_root, "*.bak", SearchOption.AllDirectories));
        Assert.False(File.Exists(BatchScriptGenerator.GetOutputPath(_root, Model.Id)));
        var again = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Equal(model.PreferredConfigurationPath, again.PreferredConfigurationPath);
    }

    [Fact]
    public async Task SaveAs_AndEditUnselectedConfiguration_DoNotChangeNextRun()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        var pathB = ConfigurationPath("B.json");
        await ModelConfigurationFile.WriteAsync(pathB, model with { ContextSize = 32768, GpuLayers = "0" }, _root);
        var unchanged = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Equal(model.PreferredConfigurationPath, unchanged.PreferredConfigurationPath);
        Assert.Equal(16384, unchanged.ContextSize);
        var b = await ModelConfigurationFile.ReadAsync(pathB, model, _root);
        Assert.Equal(32768, b.ContextSize);
        await _store.SaveRegistrationAsync(_root, b with { PreferredConfigurationPath = pathB });
        var selected = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Equal(pathB, selected.PreferredConfigurationPath);
        Assert.Equal(32768, selected.ContextSize);
        Assert.Equal("0", selected.GpuLayers);
    }

    [Fact]
    public async Task SelectedDocumentMissing_DoesNotHideModelOrResurrectConfiguration_AndBlocksStartup()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        File.Delete(model.PreferredConfigurationPath!);
        var loaded = await _store.LoadAsync(_root);
        var retained = Assert.Single(loaded.Profiles);
        Assert.NotNull(retained.ConfigurationError);
        Assert.False(File.Exists(model.PreferredConfigurationPath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ModelStartupConfiguration.ResolveAsync(retained, _root));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, model, _root, requireExisting: true));
    }

    [Fact]
    public async Task ConfigurationBoundToAnotherModel_IsRejectedWithoutChangingSelection()
    {
        var path = ConfigurationPath("A.json");
        await ModelConfigurationFile.WriteAsync(path, Model, _root);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(path, Model with { Id = "other", Alias = "other" }, _root));
        var other = Model with { Id = "other", Alias = "other" };
        var otherPath = Path.Combine(ModelConfigurationFile.GetDirectory(other, _root), "copied.json");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(path, otherPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(otherPath, other, _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(path, Model with { ModelRelativePath = "models/other.gguf" }, _root));
    }

    [Fact]
    public async Task ImportedMetadata_CannotOverrideTrustedModelProvenanceOrAlias()
    {
        var path = ConfigurationPath("A.json");
        await ModelConfigurationFile.WriteAsync(path, Model with { Alias = "untrusted", SourceKind = ModelSourceKind.LlamaCache, RemoteModelId = "owner/repo:Q4", RemoteRepositoryId = "owner/repo" }, _root);
        var loaded = await ModelConfigurationFile.ReadAsync(path, Model, _root);
        Assert.Equal(Model.Alias, loaded.Alias);
        Assert.Equal(ModelSourceKind.LocalFile, loaded.SourceKind);
        Assert.Null(loaded.RemoteModelId);
    }

    [Fact]
    public async Task ExternalModification_RejectsStaleEditorSave_AndPreservesExternalContent()
    {
        var path = ConfigurationPath("A.json");
        await ModelConfigurationFile.WriteAsync(path, Model, _root);
        var digest = ModelConfigurationFile.Digest(path);
        await ModelConfigurationFile.WriteAsync(path, Model with { ContextSize = 32768 }, _root);
        await Assert.ThrowsAsync<IOException>(() => ModelConfigurationFile.WriteAsync(path, Model with { GpuLayers = "0" }, _root, expectedDigest: digest));
        Assert.Equal(32768, (await ModelConfigurationFile.ReadAsync(path, Model, _root)).ContextSize);
    }

    [Fact]
    public async Task FullSnapshot_PreservesExplicitOffZeroAndInheritedOptions_WithoutAddingOptionalRuntimeFlags()
    {
        var path = ConfigurationPath("A.json");
        var profile = Model with { GpuLayers = "0", Jinja = false, ContextShiftEnabled = false, BatchSize = null, MicroBatchSize = null, CodexStreamIdleTimeoutMinutes = null };
        await ModelConfigurationFile.WriteAsync(path, profile, _root);
        var loaded = await ModelConfigurationFile.ReadAsync(path, Model, _root);
        Assert.False(loaded.Jinja);
        Assert.Equal("0", loaded.GpuLayers);
        Assert.Null(loaded.BatchSize);
        var preset = RouterPresetGenerator.Generate(loaded, _root, false);
        Assert.Contains("no-jinja = true", preset);
        Assert.DoesNotContain("batch-size", preset);
        Assert.DoesNotContain("threads =", preset);
    }

    [Fact]
    public async Task Load_ReplacesOptionsRatherThanMergingPreviousConfiguration()
    {
        var a = ConfigurationPath("A.json");
        var b = ConfigurationPath("B.json");
        await ModelConfigurationFile.WriteAsync(a, Model with { ExtraArguments = new Dictionary<string, string?> { ["threads"] = "8" } }, _root);
        await ModelConfigurationFile.WriteAsync(b, Model, _root);
        var loadedA = await ModelConfigurationFile.ReadAsync(a, Model, _root);
        var loadedB = await ModelConfigurationFile.ReadAsync(b, loadedA, _root);
        Assert.Empty(loadedB.ExtraArguments);
    }

    [Fact]
    public async Task UnsupportedFormat_AndMalformedJson_AreRejected()
    {
        var path = ConfigurationPath("A.json");
        await ModelConfigurationFile.WriteAsync(path, Model, _root);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["FormatVersion"] = 99;
        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(path, Model, _root));
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => ModelConfigurationFile.ReadAsync(path, Model, _root));
    }

    [Fact]
    public async Task MtpEvidence_IsSharedAcrossConfigurations_ButRenameAndRuntimeChangesInvalidateIt()
    {
        var draft = Path.Combine(_root, "models", "draft.gguf");
        await File.WriteAllTextAsync(draft, "mtp");
        var proof = Model with { MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = "models/draft.gguf", MtpCapabilityStatus = MtpCapabilityStatus.Verified, MtpValidatedAtUtc = DateTimeOffset.UtcNow };
        proof = proof with { MtpValidationSignature = MtpValidationFingerprint.Compute(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Mtp);
        var reused = ModelResourceCatalog.Resolve(proof with { GpuLayers = "0", MtpEnabled = true, ContextSize = 32768 }, _root);
        Assert.Equal(MtpCapabilityStatus.Verified, reused.MtpCapabilityStatus);
        Assert.True(reused.MtpEnabled);
        File.Move(draft, draft + ".renamed");
        Assert.Equal(MtpCapabilityStatus.Unknown, ModelResourceCatalog.Resolve(proof, _root).MtpCapabilityStatus);
        Assert.DoesNotContain(ModelResourceCatalog.Read(proof, _root).Records, r => ModelResourceCatalog.IsCurrent(r, _root));
    }

    [Fact]
    public async Task TemplateEvidence_TracksExactPath_AndRegistrationDoesNotSelectIt()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var template = Path.Combine(ChatTemplateFile.GetDirectory(_root), "one.jinja");
        await File.WriteAllTextAsync(template, "{{ messages }}");
        var proof = Model with { ChatTemplateRelativePath = "scripts/templates/one.jinja" };
        await ChatTemplateValidationCache.WriteAsync(proof, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Template, defaultTemplate: true);
        Assert.Null(Assert.Single((await _store.LoadAsync(_root)).Profiles).ChatTemplateRelativePath);
        var record = Assert.Single(ModelResourceCatalog.Read(proof, _root).Records);
        Assert.True(ModelResourceCatalog.IsCurrent(record, _root));
        Assert.Equal(proof.ChatTemplateRelativePath, ModelResourceCatalog.ApplyDefaultTemplate(Model, _root).ChatTemplateRelativePath);
        File.Move(template, Path.Combine(ChatTemplateFile.GetDirectory(_root), "two.jinja"));
        Assert.False(ModelResourceCatalog.IsCurrent(record, _root));
    }

    [Fact]
    public async Task VisionRecord_DoesNotBypassTemplateDirectoryRules()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "models", "vision.gguf"), "vision");
        var proof = Model with { VisionSource = VisionSourceKind.External, VisionProjectorRelativePath = "models/vision.gguf", VisionCapabilityStatus = VisionCapabilityStatus.Verified, VisionValidatedAtUtc = DateTimeOffset.UtcNow };
        proof = proof with { VisionValidationSignature = VisionValidationFingerprint.Compute(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Vision);
        Assert.Equal(VisionCapabilityStatus.Verified, ModelResourceCatalog.Resolve(proof, _root).VisionCapabilityStatus);
        Assert.Equal(VisionCapabilityStatus.Unknown, ModelResourceCatalog.Resolve(proof with { ChatTemplateRelativePath = "other.jinja" }, _root).VisionCapabilityStatus);
    }

    [Fact]
    public async Task ReasoningEvidence_IsReusableAndIndependentOfConfigurationName()
    {
        await File.WriteAllTextAsync(Path.Combine(ChatTemplateFile.GetDirectory(_root), "template.jinja"), "{{ messages }}");
        var proof = Model with
        {
            ChatTemplateRelativePath = "scripts/templates/template.jinja",
            SupportsThinkingSwitch = true,
            DefaultThinkingEnabled = true,
            ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
            SupportedReasoningLevels = ["medium", "high"],
            ReasoningResponsesVerified = true,
            ReasoningClientCompatible = true,
            ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow,
        };
        proof = proof with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.Compute(proof, _root), ReasoningValidationBasis = ReasoningValidationFingerprint.CaptureBasis(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Reasoning);
        var clean = Model with { ChatTemplateRelativePath = "scripts/templates/template.jinja", ThinkingEnabled = true, ExposeReasoningEffortInChatGpt = true, PreferredReasoningLevel = "high" };
        var resolved = ModelResourceCatalog.Resolve(clean, _root);
        Assert.Equal(ReasoningCapabilityStatus.Verified, resolved.ReasoningCapabilityStatus);
        Assert.Equal("high", resolved.PreferredReasoningLevel);
        Assert.True(resolved.ThinkingEnabled);
    }

    [Fact]
    public async Task ForgedConfigurationProofWithoutModelRecord_DoesNotAuthorizeExternalMtp()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var registered = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        await ModelResourceCatalog.EnsureMigratedAsync(registered, _root);
        await File.WriteAllTextAsync(Path.Combine(_root, "models", "fake.gguf"), "draft");
        var forged = registered with { MtpEnabled = true, MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = "models/fake.gguf", MtpCapabilityStatus = MtpCapabilityStatus.Verified, MtpValidatedAtUtc = DateTimeOffset.UtcNow };
        forged = forged with { MtpValidationSignature = MtpValidationFingerprint.Compute(forged, _root) };
        await ModelConfigurationFile.WriteAsync(registered.PreferredConfigurationPath!, forged, _root);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(registered, _root));
    }

    [Fact]
    public async Task MissingRequiredParameter_IsRejected_AndModelRegistrationRemainsVisible()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(model.PreferredConfigurationPath!))!;
        json["Parameters"]!.AsObject().Remove("GpuLayers");
        await File.WriteAllTextAsync(model.PreferredConfigurationPath!, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(model.PreferredConfigurationPath!, model, _root));
        var visible = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.NotNull(visible.ConfigurationError);
    }

    [Fact]
    public async Task DeletedResourceCatalog_DoesNotImportConfigurationProofsAgain()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        await ModelResourceCatalog.EnsureMigratedAsync(model, _root);
        var registrationPath = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), model.Id + ".json");
        var registration = JsonSerializer.Deserialize<ModelProfile>(await File.ReadAllTextAsync(registrationPath))!;
        Assert.True(registration.ResourceCatalogInitialized);
        await File.WriteAllTextAsync(Path.Combine(_root, "models", "draft.gguf"), "draft");
        var forged = registration with { MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = "models/draft.gguf", MtpCapabilityStatus = MtpCapabilityStatus.Verified, MtpValidatedAtUtc = DateTimeOffset.UtcNow };
        forged = forged with { MtpValidationSignature = MtpValidationFingerprint.Compute(forged, _root) };
        await _store.SaveRegistrationAsync(_root, forged);
        File.Delete(ModelResourceCatalog.FilePath(model, _root));
        await ModelResourceCatalog.EnsureMigratedAsync(model, _root);
        Assert.Empty(ModelResourceCatalog.Read(model, _root).Records);
    }

    [Fact]
    public async Task VisionResource_PreservesSeparateTemplateValidationContexts()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "vision.gguf"), "vision");
        await File.WriteAllTextAsync(Path.Combine(ChatTemplateFile.GetDirectory(_root), "one.jinja"), "one");
        await File.WriteAllTextAsync(Path.Combine(ChatTemplateFile.GetDirectory(_root), "two.jinja"), "two");
        foreach (var template in new[] { "one.jinja", "two.jinja" })
        {
            var proof = Model with
            {
                ChatTemplateRelativePath = "scripts/templates/" + template,
                VisionSource = VisionSourceKind.External,
                VisionProjectorRelativePath = "vision.gguf",
                VisionCapabilityStatus = VisionCapabilityStatus.Verified,
                VisionValidatedAtUtc = DateTimeOffset.UtcNow
            };
            proof = proof with { VisionValidationSignature = VisionValidationFingerprint.Compute(proof, _root) };
            await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Vision);
        }
        Assert.Equal(2, ModelResourceCatalog.Read(Model, _root).Records.Count);
        foreach (var template in new[] { "one.jinja", "two.jinja" })
            Assert.Equal(VisionCapabilityStatus.Verified, ModelResourceCatalog.Resolve(Model with
            { ChatTemplateRelativePath = "scripts/templates/" + template, VisionSource = VisionSourceKind.External, VisionProjectorRelativePath = "vision.gguf" }, _root).VisionCapabilityStatus);
    }

    [Fact]
    public async Task ReadDigest_DescribesTheLoadedBytes_NotLaterFileContent()
    {
        var path = ConfigurationPath("A.json");
        await ModelConfigurationFile.WriteAsync(path, Model, _root);
        var loaded = await ModelConfigurationFile.ReadAsync(path, Model, _root);
        Assert.Equal(ModelConfigurationFile.Digest(path), loaded.ConfigurationContentDigest);
        await ModelConfigurationFile.WriteAsync(path, Model with { GpuLayers = "0" }, _root);
        Assert.NotEqual(ModelConfigurationFile.Digest(path), loaded.ConfigurationContentDigest);
    }

    [Fact]
    public async Task ReusedFriendlyModelId_DoesNotInheritAnotherMainFilesResourceEvidence()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "draft.gguf"), "draft");
        await File.WriteAllTextAsync(Path.Combine(_root, "models", "second.gguf"), "second-main");
        var proof = Model with { MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = "draft.gguf", MtpCapabilityStatus = MtpCapabilityStatus.Verified, MtpValidatedAtUtc = DateTimeOffset.UtcNow };
        proof = proof with { MtpValidationSignature = MtpValidationFingerprint.Compute(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Mtp);
        var differentModel = proof with { ModelRelativePath = "models/second.gguf" };
        Assert.Empty(ModelResourceCatalog.Read(differentModel, _root).Records);
        Assert.Equal(MtpCapabilityStatus.Unknown, ModelResourceCatalog.Resolve(differentModel, _root).MtpCapabilityStatus);
    }

    [Fact]
    public async Task MissingSelectedVisionAsset_CanBeLoadedForRepair_ButCannotStart()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        await ModelResourceCatalog.EnsureMigratedAsync(model, _root);
        var asset = Path.Combine(_root, "models", "vision.gguf");
        await File.WriteAllTextAsync(asset, "vision");
        var configured = model with
        {
            VisionEnabled = true,
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/vision.gguf",
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionValidatedAtUtc = DateTimeOffset.UtcNow
        };
        configured = configured with { VisionValidationSignature = VisionValidationFingerprint.Compute(configured, _root) };
        await ModelResourceCatalog.RegisterAsync(configured, _root, ModelResourceKind.Vision);
        await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, configured, _root);
        File.Delete(asset);
        var editable = await ModelConfigurationFile.ReadAsync(model.PreferredConfigurationPath!, model, _root);
        Assert.True(editable.VisionEnabled);
        Assert.Equal("models/vision.gguf", editable.VisionProjectorRelativePath);
        Assert.Single((await _store.LoadAsync(_root)).Profiles);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(model, _root));
    }

    [Fact]
    public async Task DeletedMtpFromAppliedRegistration_DoesNotHideModelOrBlockResourceMigration()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        var asset = Path.Combine(_root, "models", "draft.gguf");
        await File.WriteAllTextAsync(asset, "draft");
        var configured = model with
        {
            MtpEnabled = true,
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/draft.gguf",
            MtpCapabilityStatus = MtpCapabilityStatus.Verified,
            MtpValidatedAtUtc = DateTimeOffset.UtcNow
        };
        configured = configured with { MtpValidationSignature = MtpValidationFingerprint.Compute(configured, _root) };
        configured = ModelProfileFactory.SaveCurrentParametersAsDefault(configured);
        await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, configured, _root);
        await _store.SaveRegistrationAsync(_root, configured);
        File.Delete(asset);

        var visible = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.True(visible.MtpEnabled);
        Assert.Null(visible.ConfigurationError);
        await ModelResourceCatalog.EnsureMigratedAsync(visible, _root);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(visible, _root));

        // Recover by choosing an existing plain configuration, without resurrecting the draft.
        await _store.SaveRegistrationAsync(_root, visible with { MtpEnabled = false });
        var repaired = visible with { MtpEnabled = false };
        await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, repaired, _root);
        Assert.False((await ModelStartupConfiguration.ResolveAsync(repaired, _root)).MtpEnabled);
        Assert.False(File.Exists(asset));
    }

    [Theory]
    [InlineData("outside.json")]
    [InlineData("scripts/outside.json")]
    [InlineData("scripts/profiles/model.json")]
    [InlineData("scripts/model-resources/model.json")]
    [InlineData("scripts/profiles/configurations/another-model/A.json")]
    [InlineData("scripts/profiles/configurations/model-extra/A.json")]
    [InlineData("scripts/profiles/configurations/model/nested/A.json")]
    [InlineData("scripts/profiles/configurations/model/../escaped.json")]
    [InlineData("scripts/profiles/configurations/model/A.txt")]
    [InlineData("scripts/profiles/configurations/model/.json")]
    public async Task ConfigurationOutsideModelDirectory_IsRejectedBeforeReadOrWrite(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "outside file must remain untouched");
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.ReadAsync(path, Model, _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.WriteAsync(path, Model, _root));
        Assert.Equal("outside file must remain untouched", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AllowedDirectory_NormalizesPathsAndAcceptsCaseInsensitiveJsonExtension()
    {
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), ".", "27B_64K_思考.JSON");
        await ModelConfigurationFile.WriteAsync(path, Model, _root);
        Assert.Equal(Model.ContextSize, (await ModelConfigurationFile.ReadAsync(path, Model, _root)).ContextSize);
        Assert.Equal(Path.GetFullPath(path), ModelConfigurationFile.ValidatePath(path, Model, _root));
        Assert.Equal(Path.GetDirectoryName(ModelConfigurationFile.InitialPath(Model, _root)), ModelConfigurationFile.GetDirectory(Model, _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.WriteAsync("relative.json", Model, _root));
    }

    [Fact]
    public async Task PreviouslySelectedExternalConfiguration_RemainsOnDiskButCannotStart()
    {
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        var external = Path.Combine(_root, "scripts", "previous.json");
        File.Copy(model.PreferredConfigurationPath!, external);
        await _store.SaveRegistrationAsync(_root, model with { PreferredConfigurationPath = external });
        var visible = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.NotNull(visible.ConfigurationError);
        Assert.True(File.Exists(external));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(visible, _root));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAsync(_root, visible));
        Assert.True(File.Exists(external));
        var restored = ConfigurationPath("restored.json");
        File.Copy(external, restored);
        var loaded = await ModelConfigurationFile.ReadAsync(restored, visible, _root);
        await _store.SaveRegistrationAsync(_root, loaded with { PreferredConfigurationPath = restored });
        Assert.Equal(Model.ContextSize, (await ModelStartupConfiguration.ResolveAsync(loaded with { PreferredConfigurationPath = restored }, _root)).ContextSize);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("..")]
    [InlineData("model.")]
    public void UnsafeModelIdentifier_CannotChangeConfigurationDirectory(string id) =>
        Assert.Throws<InvalidDataException>(() => ModelConfigurationFile.GetDirectory(Model with { Id = id }, _root));

    public void Dispose() => Directory.Delete(_root, true);
}
