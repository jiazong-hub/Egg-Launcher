using System.Text.Json;
using System.Text;
using Launcher.Core.Diagnostics;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ModelResourceRecheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-resource-recheck", Guid.NewGuid().ToString("N"));
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 16384,
        CompactionSafetyReserve = 4096,
        ResourceCatalogInitialized = true
    };

    public ModelResourceRecheckTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        File.WriteAllText(Path.Combine(_root, "models/main.gguf"), "main");
        File.WriteAllText(Path.Combine(_root, "models/draft.gguf"), "draft");
        File.WriteAllText(Path.Combine(_root, "models/vision.gguf"), "vision");
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "runtime");
        File.WriteAllText(Path.Combine(ChatTemplateFile.GetDirectory(_root), "selected.jinja"), "{{ messages }}");
    }

    private ModelProfile Fresh(ModelResourceKind kind) => kind switch
    {
        ModelResourceKind.Template => Model with { ChatTemplateRelativePath = "scripts/templates/selected.jinja" },
        ModelResourceKind.Mtp => Mtp(),
        _ => Vision(),
    };
    private ModelProfile Mtp()
    {
        var model = Model with
        {
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/draft.gguf",
            MtpCapabilityStatus = MtpCapabilityStatus.Verified,
            MtpValidatedAtUtc = DateTimeOffset.UtcNow
        };
        return model with { MtpValidationSignature = MtpValidationFingerprint.Compute(model, _root) };
    }
    private ModelProfile Vision()
    {
        var model = Model with
        {
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/vision.gguf",
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionValidatedAtUtc = DateTimeOffset.UtcNow
        };
        return model with { VisionValidationSignature = VisionValidationFingerprint.Compute(model, _root) };
    }
    private async Task<ModelProfile> Register(ModelResourceKind kind)
    {
        var proof = Fresh(kind);
        if (kind == ModelResourceKind.Template) await ChatTemplateValidationCache.WriteAsync(proof, _root, default);
        await ModelResourceCatalog.RegisterAsync(proof, _root, kind, defaultTemplate: kind == ModelResourceKind.Template);
        return proof;
    }
    private string Catalog => ModelResourceCatalog.FilePath(Model, _root);
    private async Task<ModelProfile> Configuration(ModelProfile proof, string name = "chosen")
    {
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), name + ".json");
        proof = proof with
        {
            MtpEnabled = proof.MtpSource == MtpSourceKind.External,
            VisionEnabled = proof.VisionSource == VisionSourceKind.External
        };
        await ModelConfigurationFile.WriteAsync(path, proof, _root);
        return proof with { ConfigurationInitialized = true, PreferredConfigurationPath = path };
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task ValidOrExpiredResource_CanBeRecheckedWhileReferenced_WithoutDuplicatesOrJsonChanges(ModelResourceKind kind)
    {
        var proof = await Register(kind);
        var selected = await Configuration(proof);
        var another = await Configuration(proof, "another");
        var before = await File.ReadAllTextAsync(selected.PreferredConfigurationPath!);
        var anotherBefore = await File.ReadAllTextAsync(another.PreferredConfigurationPath!);
        await new JsonModelProfileStore().SaveRegistrationAsync(_root, selected);
        var registrationPath = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), Model.Id + ".json");
        var registrationBefore = await File.ReadAllTextAsync(registrationPath);
        for (var check = 0; check < 2; check++)
        {
            if (check == 1)
            {
                await File.AppendAllTextAsync(Path.Combine(_root, kind == ModelResourceKind.Vision ? "models/vision.gguf" : "llama-server.exe"), "updated");
                Assert.Null(ModelResourceManagement.FindReusable(proof, _root, kind));
            }
            var result = await ModelResourceRecheck.RunAsync(proof, _root, kind, async _ =>
            {
                var fresh = Fresh(kind);
                if (kind == ModelResourceKind.Template) await ChatTemplateValidationCache.WriteAsync(fresh, _root, default);
                return fresh;
            }, () => true);
            Assert.True(result);
            Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
            Assert.NotNull(ModelResourceManagement.FindReusable(Fresh(kind), _root, kind));
            Assert.Equal(before, await File.ReadAllTextAsync(selected.PreferredConfigurationPath!));
            Assert.Equal(anotherBefore, await File.ReadAllTextAsync(another.PreferredConfigurationPath!));
            Assert.Equal(registrationBefore, await File.ReadAllTextAsync(registrationPath));
            Assert.Equal(2, (await ModelResourceManagement.FindReferencesAsync(Model, _root, kind, new ModelResourceRecord(kind, proof).ResourcePath!)).Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceManagement.RemoveAsync(Model, _root, kind,
                new ModelResourceRecord(kind, proof).ResourcePath!, () => true));
        }
        if (kind == ModelResourceKind.Template)
            Assert.Equal("scripts/templates/selected.jinja", ModelResourceCatalog.Read(Model, _root).DefaultTemplatePath);
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task ExplicitRejection_InvalidatesOnlyTargetProof_BlocksStartup_AndCanRecover(ModelResourceKind kind)
    {
        var proof = await Register(kind);
        var selected = await Configuration(proof);
        var configBefore = await File.ReadAllTextAsync(selected.PreferredConfigurationPath!);
        var otherKind = kind == ModelResourceKind.Template ? ModelResourceKind.Mtp : ModelResourceKind.Template;
        await Register(otherKind);
        var otherBefore = JsonSerializer.Serialize(ModelResourceCatalog.Read(Model, _root).Records.Single(r => r.Kind == otherKind));
        await Assert.ThrowsAsync<ModelResourceValidationException>(() => ModelResourceRecheck.RunAsync(proof, _root, kind,
            _ => throw new ModelResourceValidationException("fixture-rejected"), () => true));
        Assert.Null(ModelResourceManagement.FindReusable(proof, _root, kind));
        Assert.Equal(otherBefore, JsonSerializer.Serialize(ModelResourceCatalog.Read(Model, _root).Records.Single(r => r.Kind == otherKind)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(selected, _root));
        Assert.Equal(configBefore, await File.ReadAllTextAsync(selected.PreferredConfigurationPath!));

        Assert.True(await ModelResourceRecheck.RunAsync(proof, _root, kind, _ => Task.FromResult<ModelProfile?>(Fresh(kind)), () => true));
        Assert.NotNull(ModelResourceManagement.FindReusable(proof, _root, kind));
        Assert.Equal(2, ModelResourceCatalog.Read(Model, _root).Records.Count);
    }

    [Theory]
    [InlineData(ModelResourceKind.Template, "cancel")]
    [InlineData(ModelResourceKind.Mtp, "cancel")]
    [InlineData(ModelResourceKind.Vision, "cancel")]
    [InlineData(ModelResourceKind.Template, "timeout")]
    [InlineData(ModelResourceKind.Mtp, "timeout")]
    [InlineData(ModelResourceKind.Vision, "timeout")]
    [InlineData(ModelResourceKind.Template, "busy")]
    [InlineData(ModelResourceKind.Mtp, "busy")]
    [InlineData(ModelResourceKind.Vision, "busy")]
    public async Task IncompleteCheck_KeepsOriginalProof(ModelResourceKind kind, string outcome)
    {
        var proof = await Register(kind);
        var before = await File.ReadAllTextAsync(Catalog);
        if (outcome == "cancel") Assert.False(await ModelResourceRecheck.RunAsync(proof, _root, kind, _ => Task.FromResult<ModelProfile?>(null), () => true));
        else if (outcome == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => ModelResourceRecheck.RunAsync(proof, _root, kind,
            _ => throw new TimeoutException(), () => true));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceRecheck.RunAsync(proof, _root, kind,
            _ => throw new InvalidOperationException("busy"), () => true));
        Assert.Equal(before, await File.ReadAllTextAsync(Catalog));
        Assert.NotNull(ModelResourceManagement.FindReusable(proof, _root, kind));
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task SourceChangesDuringCheck_DoNotRegisterNewProof(ModelResourceKind kind)
    {
        var proof = await Register(kind);
        var before = await File.ReadAllTextAsync(Catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceRecheck.RunAsync(proof, _root, kind, async _ =>
        {
            await File.AppendAllTextAsync(Path.Combine(_root, kind == ModelResourceKind.Vision ? "models/vision.gguf" : "llama-server.exe"), "updated");
            return Fresh(kind);
        }, () => true));
        Assert.Equal(before, await File.ReadAllTextAsync(Catalog));
        Assert.Null(ModelResourceManagement.FindReusable(proof, _root, kind));
    }

    [Fact]
    public async Task RuntimeStartsDuringCheck_BlocksCommit_WithoutChangingOriginalRecord()
    {
        var proof = await Register(ModelResourceKind.Mtp);
        var before = await File.ReadAllTextAsync(Catalog);
        var allowed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceRecheck.RunAsync(proof, _root, ModelResourceKind.Mtp,
            _ => { allowed = false; return Task.FromResult<ModelProfile?>(Fresh(ModelResourceKind.Mtp)); }, () => allowed));
        Assert.Equal(before, await File.ReadAllTextAsync(Catalog));
    }

    [Fact]
    public async Task EmbeddedTemplate_ExplicitFailureBlocksStartup_UntilSuccessfulRecheck()
    {
        using (var writer = new BinaryWriter(File.Create(Path.Combine(_root, Model.ModelRelativePath)), Encoding.UTF8))
        {
            writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(1ul);
            WriteString(writer, "tokenizer.chat_template"); writer.Write(8u); WriteString(writer, "{{ messages }}");
        }
        var selected = await Configuration(Model);
        Assert.NotNull(await ModelStartupConfiguration.ResolveAsync(selected, _root));
        await Assert.ThrowsAsync<ModelResourceValidationException>(() => ModelResourceRecheck.RunAsync(Model, _root, ModelResourceKind.Template,
            _ => throw new ModelResourceValidationException("render-failed"), () => true));
        Assert.True(Assert.Single(ModelResourceCatalog.Read(Model, _root).Records).ValidationRejected);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(selected, _root));
        Assert.True(await ModelResourceRecheck.RunAsync(Model, _root, ModelResourceKind.Template,
            _ => Task.FromResult<ModelProfile?>(Model), () => true));
        Assert.False(Assert.Single(ModelResourceCatalog.Read(Model, _root).Records).ValidationRejected);
        Assert.NotNull(await ModelStartupConfiguration.ResolveAsync(selected, _root));

        static void WriteString(BinaryWriter writer, string value)
        { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
    }

    [Fact]
    public async Task CancelledToken_DoesNotInvalidateProofOrRunValidation()
    {
        var proof = await Register(ModelResourceKind.Mtp);
        var before = await File.ReadAllTextAsync(Catalog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModelResourceRecheck.RunAsync(proof, _root, ModelResourceKind.Mtp,
            _ => { invoked = true; return Task.FromResult<ModelProfile?>(proof); }, () => true, cancellation.Token));
        Assert.False(invoked);
        Assert.Equal(before, await File.ReadAllTextAsync(Catalog));
    }

    [Fact]
    public async Task EmbeddedMtp_DoesNotGainANewManualValidationWorkflow()
    {
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceRecheck.RunAsync(Model, _root, ModelResourceKind.Mtp,
            _ => { invoked = true; return Task.FromResult<ModelProfile?>(Model); }, () => true));
        Assert.False(invoked);
        Assert.False(File.Exists(Catalog));
    }

    [Fact]
    public async Task DiscoveryFailure_DoesNotGrantProofFromMalformedConfiguration()
    {
        var directory = ModelConfigurationFile.GetDirectory(Model, _root);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "forged.json"), JsonSerializer.Serialize(new ModelConfigurationDocument
        {
            ModelId = "wrong-model",
            ModelRelativePath = Model.ModelRelativePath,
            Parameters = Mtp() with { ThinkingEnabled = true, MtpEnabled = true },
        }));
        var found = await ModelResourceRecheck.DiscoverAsync(Model, _root);
        Assert.Empty(found.References);
        Assert.Single(found.Warnings);
        Assert.Empty(ModelResourceCatalog.Read(Model, _root).Records);
    }

    [Fact]
    public async Task RecheckCannotSilentlySwitchResourcePaths()
    {
        var proof = await Register(ModelResourceKind.Mtp);
        var before = await File.ReadAllTextAsync(Catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelResourceRecheck.RunAsync(proof, _root, ModelResourceKind.Mtp,
            _ => Task.FromResult<ModelProfile?>(proof with { MtpDraftModelRelativePath = "models/vision.gguf" }), () => true));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelResourceRecheck.RunAsync(proof, _root, ModelResourceKind.Mtp,
            _ => Task.FromResult<ModelProfile?>(proof with { MtpSource = MtpSourceKind.Embedded }), () => true));
        Assert.Equal(before, await File.ReadAllTextAsync(Catalog));
    }

    [Fact]
    public async Task LegacyDiscovery_ListsReferencedResourcesOnly_WithoutTrustingFlagsOrWritingFiles()
    {
        var parameters = Model with
        {
            ModelType = ModelType.MoE,
            ChatTemplateRelativePath = "scripts/templates/selected.jinja",
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/draft.gguf",
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/vision.gguf"
        };
        var directory = ModelConfigurationFile.GetDirectory(Model, _root);
        Directory.CreateDirectory(directory);
        var document = new ModelConfigurationDocument { ModelId = Model.Id, ModelRelativePath = Model.ModelRelativePath, Parameters = parameters };
        var json = JsonSerializer.Serialize(document);
        await File.WriteAllTextAsync(Path.Combine(directory, "first.json"), json);
        await File.WriteAllTextAsync(Path.Combine(directory, "second.json"), json);
        await File.WriteAllTextAsync(Path.Combine(directory, "foreign.json"), JsonSerializer.Serialize(document with { ModelId = "another-model" }));
        await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "bad-json");
        File.Delete(Path.Combine(_root, "models/vision.gguf"));
        var found = await ModelResourceRecheck.DiscoverAsync(Model, _root);
        Assert.Equal(3, found.References.Count);
        Assert.Equal(2, found.Warnings.Count);
        Assert.Empty(ModelResourceCatalog.Read(Model, _root).Records);
        Assert.False(File.Exists(Catalog));
        Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(directory, "first.json")));
        Assert.Contains(found.References, r => r.Kind == ModelResourceKind.Vision && r.Path == "models/vision.gguf");
    }

    [Fact]
    public void MtpProbe_DoesNotInheritStaleReasoningMissingVisionOrCustomSettings()
    {
        var source = Model with
        {
            ThinkingEnabled = true,
            SupportsThinkingSwitch = null,
            VisionEnabled = true,
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/missing.gguf",
            ChatTemplateRelativePath = "scripts/templates/missing.jinja",
            MtpDraftGpuLayers = "0",
            ExtraArguments = new Dictionary<string, string?> { ["reasoning"] = "on", ["keep"] = "16000" },
            CustomParameters = new() { Llama = [new() { Name = "--future", Value = "unused" }] }
        };
        var before = JsonSerializer.Serialize(source);
        var probe = ExternalMtpValidationProfile.Create(source, "models/draft.gguf");
        var preset = RouterPresetGenerator.Generate(probe, _root, true);
        Assert.Contains("spec-type = draft-mtp", preset);
        Assert.Contains("spec-draft-ngl = 0", preset);
        Assert.Contains("no-jinja = true", preset);
        Assert.Contains("no-mmproj = true", preset);
        Assert.DoesNotContain("chat-template-file", preset);
        Assert.DoesNotContain("reasoning =", preset);
        Assert.DoesNotContain("future", preset);
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecheckJournal_UsesExistingConciseAndDetailedRules(bool detailed)
    {
        var events = new List<DiagnosticEvent>();
        var journal = new DiagnosticJournal(e => { events.Add(e); return Task.CompletedTask; });
        journal.SetDetailed(detailed);
        await journal.RunAsync("resource_recheck", new Dictionary<string, object?> { ["resourceKind"] = "Template" }, async operation =>
        { await operation.StageAsync("validate_resource"); return true; });
        Assert.Equal(detailed ? 3 : 1, events.Count);
        Assert.Equal("resource_recheck", events.Last().EventName);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
