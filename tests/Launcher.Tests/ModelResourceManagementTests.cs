using System.Text;
using System.Text.Json;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ModelResourceManagementTests : IDisposable
{
    private const string Template = """
{%- for message in messages %}
    {%- set content = message.content|string|trim %}
    {%- if message.role == "system" %}
        {%- if not loop.first %}
            {{- raise_exception('System message must be at the beginning.') }}
        {%- endif %}
    {%- elif message.role == "user" %}
        {{- '<|im_start|>user\n' + content + '<|im_end|>\n' }}
    {%- endif %}
{%- endfor %}
""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-resource-management", Guid.NewGuid().ToString("N"));
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 16384,
        CompactionSafetyReserve = 4096,
        ResourceCatalogInitialized = true,
    };

    public ModelResourceManagementTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        WriteGguf(Template);
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "fixture-runtime");
        File.WriteAllText(Path.Combine(_root, "models", "draft.gguf"), "draft");
        File.WriteAllText(Path.Combine(_root, "models", "vision.gguf"), "vision");
    }

    private ModelProfile Evidence(ModelResourceKind kind) => kind switch
    {
        ModelResourceKind.Template => Model with { ChatTemplateRelativePath = "scripts/templates/template.jinja", Jinja = true },
        ModelResourceKind.Mtp => Mtp(),
        _ => Vision(),
    };

    private ModelProfile Mtp()
    {
        var profile = Model with
        {
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/draft.gguf",
            MtpCapabilityStatus = MtpCapabilityStatus.Verified,
            MtpValidatedAtUtc = DateTimeOffset.UtcNow
        };
        return profile with { MtpValidationSignature = MtpValidationFingerprint.Compute(profile, _root) };
    }

    private ModelProfile Vision()
    {
        var profile = Model with
        {
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/vision.gguf",
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionValidatedAtUtc = DateTimeOffset.UtcNow
        };
        return profile with { VisionValidationSignature = VisionValidationFingerprint.Compute(profile, _root) };
    }

    private async Task<ModelProfile> Register(ModelResourceKind kind)
    {
        var profile = Evidence(kind);
        if (kind == ModelResourceKind.Template)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, profile.ChatTemplateRelativePath!), "{{ messages }}");
            await ChatTemplateValidationCache.WriteAsync(profile, _root, CancellationToken.None);
        }
        await ModelResourceCatalog.RegisterAsync(profile, _root, kind, defaultTemplate: kind == ModelResourceKind.Template);
        return profile;
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task RepeatedRegistration_WithEquivalentPath_ReusesOneCurrentResource(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        var alternate = kind switch
        {
            ModelResourceKind.Template => profile with { ChatTemplateRelativePath = "scripts/templates/./TEMPLATE.JINJA" },
            ModelResourceKind.Mtp => profile with { MtpDraftModelRelativePath = "models/./DRAFT.GGUF" },
            _ => profile with { VisionProjectorRelativePath = "models/./VISION.GGUF" },
        };
        Assert.NotNull(ModelResourceManagement.FindReusable(alternate, _root, kind));
        await ModelResourceCatalog.RegisterAsync(profile, _root, kind);
        Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
        Assert.Null(ModelResourceManagement.FindReusable(alternate with { ModelRelativePath = "models/other.gguf" }, _root, kind));
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task MissingOrChangedAsset_CannotReuseProof(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        var record = Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
        var path = Path.Combine(_root, record.ResourcePath!);
        await File.AppendAllTextAsync(path, "changed");
        Assert.Null(ModelResourceManagement.FindReusable(profile, _root, kind));
        File.Delete(path);
        Assert.False(ModelResourceCatalog.IsCurrent(record, _root));
        Assert.Null(ModelResourceManagement.FindReusable(profile, _root, kind));
    }

    [Fact]
    public async Task VisionReuse_IsIndependentOfTemplateContext()
    {
        var profile = await Register(ModelResourceKind.Vision);
        var different = profile with { ChatTemplateRelativePath = "scripts/templates/another.jinja" };
        await File.WriteAllTextAsync(Path.Combine(_root, different.ChatTemplateRelativePath!), "{{ messages }}");
        Assert.NotNull(ModelResourceManagement.FindReusable(different, _root, ModelResourceKind.Vision));
        var revised = different with { VisionValidationSignature = VisionValidationFingerprint.Compute(different, _root) };
        await ModelResourceCatalog.RegisterAsync(revised, _root, ModelResourceKind.Vision);
        Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
        Assert.NotNull(ModelResourceManagement.FindReusable(profile, _root, ModelResourceKind.Vision));
        Assert.NotNull(ModelResourceManagement.FindReusable(revised, _root, ModelResourceKind.Vision));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VisionMetadataProof_IsUsableAcrossParameterTemplates_WithoutChangingFiles(bool builtIn, bool enabled)
    {
        var firstTemplate = Path.Combine(ChatTemplateFile.GetDirectory(_root), "first.jinja");
        var secondTemplate = Path.Combine(ChatTemplateFile.GetDirectory(_root), "second.jinja");
        await File.WriteAllTextAsync(firstTemplate, "first");
        await File.WriteAllTextAsync(secondTemplate, "second");
        var proof = Vision() with
        {
            ChatTemplateRelativePath = Path.GetRelativePath(_root, firstTemplate),
            VisionSource = builtIn ? VisionSourceKind.BuiltIn : VisionSourceKind.External,
            VisionProjectorRelativePath = builtIn ? null : "models/vision.gguf",
        };
        proof = proof with { VisionValidationSignature = VisionValidationFingerprint.Compute(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Vision);
        var catalogBefore = await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root));
        var candidate = proof with
        {
            ChatTemplateRelativePath = Path.GetRelativePath(_root, secondTemplate),
            VisionEnabled = enabled,
            VisionCapabilityStatus = VisionCapabilityStatus.Unknown,
            VisionValidationSignature = null
        };
        var resolved = ModelResourceCatalog.Resolve(candidate, _root);
        Assert.Equal(VisionCapabilityStatus.Verified, resolved.VisionCapabilityStatus);
        Assert.True(VisionValidationFingerprint.IsCurrent(resolved, _root));
        Assert.Equal(enabled, resolved.VisionEnabled);
        Assert.Equal(candidate.ChatTemplateRelativePath, resolved.ChatTemplateRelativePath);
        Assert.Equal(catalogBefore, await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root)));
        Assert.Equal("first", await File.ReadAllTextAsync(firstTemplate));
        Assert.Equal("second", await File.ReadAllTextAsync(secondTemplate));
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "vision.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ModelConfigurationFile.WriteAsync(path, resolved, _root);
        var loaded = ModelResourceCatalog.Resolve(await ModelConfigurationFile.ReadAsync(path, Model, _root), _root);
        Assert.Equal(enabled, loaded.VisionEnabled);
        Assert.Equal(VisionCapabilityStatus.Verified, loaded.VisionCapabilityStatus);
        Assert.True(VisionValidationFingerprint.IsCurrent(loaded, _root));
    }

    [Theory]
    [InlineData("models/main.gguf")]
    [InlineData("models/vision.gguf")]
    public async Task VisionMetadataReuse_StillRejectsChangedModelOrProjector(string changedPath)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mtmd.dll"), "fixture");
        var proof = await Register(ModelResourceKind.Vision);
        var candidate = proof with { ChatTemplateRelativePath = "scripts/templates/another.jinja", VisionEnabled = true };
        await File.WriteAllTextAsync(Path.Combine(_root, candidate.ChatTemplateRelativePath), "different template");
        Assert.Equal(VisionCapabilityStatus.Verified, ModelResourceCatalog.Resolve(candidate, _root).VisionCapabilityStatus);
        await File.AppendAllTextAsync(Path.Combine(_root, changedPath), "modified");
        var resolved = ModelResourceCatalog.Resolve(candidate, _root);
        Assert.Equal(VisionCapabilityStatus.Unknown, resolved.VisionCapabilityStatus);
        Assert.Null(resolved.VisionValidationSignature);
        Assert.True(resolved.VisionEnabled); // Preserve the request; startup validation must reject it rather than silently switch it off.
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task DisabledResource_InNonPreferredConfiguration_BlocksRemoval(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        var store = new JsonModelProfileStore();
        await store.SaveRegistrationAsync(_root, Model);
        var registered = Assert.Single((await store.LoadAsync(_root)).Profiles);
        var configuration = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "B.json");
        await ModelConfigurationFile.WriteAsync(configuration, profile with { VisionEnabled = false, MtpEnabled = false, Jinja = false }, _root);
        var before = await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root));
        var path = new ModelResourceRecord(kind, profile).ResourcePath!;
        var references = await ModelResourceManagement.FindReferencesAsync(Model, _root, kind, path);
        Assert.Equal(["B.json"], references);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceManagement.RemoveAsync(Model, _root, kind, path, () => true));
        Assert.Contains("B.json", error.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root)));
        Assert.Equal(registered.PreferredConfigurationPath, Assert.Single((await store.LoadAsync(_root)).Profiles).PreferredConfigurationPath);
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task RemoveUnreferencedResource_KeepsFilesAndOtherModels_AndDoesNotResurrect(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        await new JsonModelProfileStore().SaveRegistrationAsync(_root, Model);
        await ModelResourceCatalog.EnsureMigratedAsync(Model, _root);
        var other = profile with { Id = "other", Alias = "other" };
        await ModelResourceCatalog.RegisterAsync(other, _root, kind);
        var otherBefore = await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(other, _root));
        var path = new ModelResourceRecord(kind, profile).ResourcePath!;
        var content = await File.ReadAllBytesAsync(Path.Combine(_root, path));
        await ModelResourceManagement.RemoveAsync(Model, _root, kind, path.ToUpperInvariant(), () => true);
        var catalog = ModelResourceCatalog.Read(Model, _root);
        Assert.Empty(catalog.Records);
        Assert.Null(catalog.DefaultTemplatePath);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, path)));
        Assert.Equal(otherBefore, await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(other, _root)));
        await ModelResourceCatalog.EnsureMigratedAsync(Model, _root);
        Assert.Empty(ModelResourceCatalog.Read(Model, _root).Records);
    }

    [Fact]
    public async Task UnreadableConfiguration_BlocksRemovalWithoutChangingCatalog()
    {
        var profile = await Register(ModelResourceKind.Mtp);
        var directory = ModelConfigurationFile.GetDirectory(Model, _root);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "{");
        var before = await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelResourceManagement.RemoveAsync(Model, _root,
            ModelResourceKind.Mtp, profile.MtpDraftModelRelativePath!, () => true));
        Assert.Contains("broken.json", error.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root)));
    }

    [Fact]
    public async Task ServiceStartsDuringReferenceScan_StopsRemoval()
    {
        var profile = await Register(ModelResourceKind.Mtp);
        var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceManagement.RemoveAsync(Model, _root,
            ModelResourceKind.Mtp, profile.MtpDraftModelRelativePath!, () => ++checks == 1));
        Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceManagement.RemoveAsync(Model, _root,
            ModelResourceKind.Mtp, profile.MtpDraftModelRelativePath!, () => false));
    }

    [Fact]
    public async Task BuiltInEntry_CannotBeRemoved()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelResourceManagement.RemoveAsync(Model, _root,
            ModelResourceKind.Mtp, "", () => true));
    }

    [Fact]
    public async Task GeneratedTemplate_IsReusableWithoutCreatingAnotherFile_AndRecreatedAfterDeletion()
    {
        var prepared = await CodexChatTemplateCompatibility.EnsureAsync(Model with { Jinja = true }, _root,
            templateFileName: "model.original.codex-compatible.jinja");
        Assert.True(prepared.TemplateGenerated);
        await ChatTemplateValidationCache.WriteAsync(prepared.Profile, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(prepared.Profile, _root, ModelResourceKind.Template);
        var before = Directory.GetFiles(ChatTemplateFile.GetDirectory(_root)).Order().ToArray();
        Assert.NotNull(ModelResourceManagement.FindGeneratedTemplate(Model, _root));
        Assert.Equal(before, Directory.GetFiles(ChatTemplateFile.GetDirectory(_root)).Order().ToArray());
        File.Delete(Path.Combine(_root, prepared.Profile.ChatTemplateRelativePath!));
        Assert.Null(ModelResourceManagement.FindGeneratedTemplate(Model, _root));
        var replacement = await CodexChatTemplateCompatibility.EnsureAsync(Model with { Jinja = true }, _root,
            templateFileName: "model.replacement.codex-compatible.jinja");
        await ChatTemplateValidationCache.WriteAsync(replacement.Profile, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(replacement.Profile, _root, ModelResourceKind.Template);
        Assert.Equal(replacement.Profile.ChatTemplateRelativePath, ModelResourceManagement.FindGeneratedTemplate(Model, _root)!.ResourcePath);
        WriteGguf(Template + "\n{# changed #}");
        Assert.Null(ModelResourceManagement.FindGeneratedTemplate(Model, _root));
    }

    [Fact]
    public async Task GeneratedTemplate_ExpiredByRuntimeUpdate_CanBeRecheckedWithStaleCapabilities()
    {
        await Register(ModelResourceKind.Mtp);
        var mtpEvidence = JsonSerializer.Serialize(Assert.Single(ModelResourceCatalog.Read(Model, _root).Records));
        var source = Model with
        {
            ThinkingEnabled = true,
            SupportsThinkingSwitch = null,
            VisionEnabled = true,
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/deleted.gguf"
        };
        var first = await CodexChatTemplateCompatibility.EnsureAsync(source, _root,
            templateFileName: "model.old.codex-compatible.jinja");
        await ChatTemplateValidationCache.WriteAsync(first.Profile, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(first.Profile, _root, ModelResourceKind.Template);
        Assert.NotNull(ModelResourceManagement.FindGeneratedTemplate(source, _root));
        var oldPath = Path.Combine(_root, first.Profile.ChatTemplateRelativePath!);
        var oldText = await File.ReadAllTextAsync(oldPath);

        await File.AppendAllTextAsync(Path.Combine(_root, "llama-server.exe"), "updated");
        Assert.Null(ModelResourceManagement.FindGeneratedTemplate(source, _root));
        var replacement = await CodexChatTemplateCompatibility.EnsureAsync(source, _root,
            templateFileName: "model.new.codex-compatible.jinja");
        Assert.NotEmpty(Launcher.Scripts.RouterPreset.RouterPresetGenerator.Generate(
            TemplateValidationProfile.Create(replacement.Profile), _root, true));
        // Native rendering is not run with these GGUF fixtures; simulate its successful result.
        await ChatTemplateValidationCache.WriteAsync(replacement.Profile, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(replacement.Profile, _root, ModelResourceKind.Template);

        Assert.Equal(replacement.Profile.ChatTemplateRelativePath,
            ModelResourceManagement.FindGeneratedTemplate(source, _root)!.ResourcePath);
        Assert.Equal(oldText, await File.ReadAllTextAsync(oldPath));
        Assert.True(source.ThinkingEnabled);
        Assert.True(source.VisionEnabled);
        Assert.Null(source.ChatTemplateRelativePath);
        Assert.Equal(mtpEvidence, JsonSerializer.Serialize(Assert.Single(ModelResourceCatalog.Read(source, _root).Records,
            record => record.Kind == ModelResourceKind.Mtp)));
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task MissingSelectedAsset_BlocksNewStartupWithoutChangingIntent(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        profile = profile with { Jinja = kind == ModelResourceKind.Template, VisionEnabled = kind == ModelResourceKind.Vision, MtpEnabled = kind == ModelResourceKind.Mtp };
        await new JsonModelProfileStore().SaveRegistrationAsync(_root, profile);
        var registered = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        var selected = registered.PreferredConfigurationPath!;
        var before = await File.ReadAllTextAsync(selected);
        File.Delete(Path.Combine(_root, new ModelResourceRecord(kind, profile).ResourcePath!));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(registered, _root));
        Assert.Equal(before, await File.ReadAllTextAsync(selected));
        var loaded = await ModelConfigurationFile.ReadAsync(selected, Model, _root);
        Assert.Equal(profile.VisionEnabled, loaded.VisionEnabled);
        Assert.Equal(profile.MtpEnabled, loaded.MtpEnabled);
        Assert.Equal(profile.ChatTemplateRelativePath, loaded.ChatTemplateRelativePath);
    }

    [Fact]
    public async Task EmbeddedMtpCandidate_KeepsExistingStartupAdmissionWithoutSeparateCheck()
    {
        var profile = Model with { MtpEnabled = true, MtpSource = MtpSourceKind.Embedded, MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate };
        await new JsonModelProfileStore().SaveRegistrationAsync(_root, profile);
        var registered = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        var resolved = await ModelStartupConfiguration.ResolveAsync(registered, _root);
        Assert.True(resolved.MtpEnabled);
        Assert.Equal(MtpCapabilityStatus.EmbeddedCandidate, resolved.MtpCapabilityStatus);
    }

    [Fact]
    public async Task RemovingVisionPath_RemovesAllTemplateVariants()
    {
        var profile = await Register(ModelResourceKind.Vision);
        var other = profile with { ChatTemplateRelativePath = "scripts/templates/other.jinja" };
        await File.WriteAllTextAsync(Path.Combine(_root, other.ChatTemplateRelativePath!), "{{ messages }}");
        other = other with { VisionValidationSignature = VisionValidationFingerprint.Compute(other, _root) };
        await ModelResourceCatalog.RegisterAsync(other, _root, ModelResourceKind.Vision);
        Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
        await ModelResourceManagement.RemoveAsync(Model, _root, ModelResourceKind.Vision, profile.VisionProjectorRelativePath!, () => true);
        Assert.Empty(ModelResourceCatalog.Read(Model, _root).Records);
        Assert.True(File.Exists(Path.Combine(_root, profile.VisionProjectorRelativePath!)));
    }

    [Fact]
    public async Task ConfigurationWithWrongModelBinding_BlocksRemoval()
    {
        var profile = await Register(ModelResourceKind.Mtp);
        var directory = ModelConfigurationFile.GetDirectory(Model, _root);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "wrong-model.json"), JsonSerializer.Serialize(new ModelConfigurationDocument
        {
            ModelId = "another",
            ModelRelativePath = "models/another.gguf",
            Parameters = Model,
        }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelResourceManagement.RemoveAsync(Model, _root,
            ModelResourceKind.Mtp, profile.MtpDraftModelRelativePath!, () => true));
        Assert.Contains("wrong-model.json", error.Message);
        Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
    }

    [Theory]
    [InlineData(ModelResourceKind.Template)]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task MissingResource_WhenFeatureIsOff_KeepsExistingStartupBehavior(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        profile = profile with { Jinja = false, MtpEnabled = false, VisionEnabled = false };
        var store = new JsonModelProfileStore();
        await store.SaveRegistrationAsync(_root, profile);
        var registered = Assert.Single((await store.LoadAsync(_root)).Profiles);
        File.Delete(Path.Combine(_root, new ModelResourceRecord(kind, profile).ResourcePath!));
        var resolved = await ModelStartupConfiguration.ResolveAsync(registered, _root);
        Assert.False(resolved.Jinja);
        Assert.False(resolved.MtpEnabled);
        Assert.False(resolved.VisionEnabled);
    }

    [Theory]
    [InlineData(ModelResourceKind.Mtp)]
    [InlineData(ModelResourceKind.Vision)]
    public async Task EquivalentSelectedPath_KeepsResolvedProofCurrentWithoutChangingSwitches(ModelResourceKind kind)
    {
        var profile = await Register(kind);
        var alternate = kind == ModelResourceKind.Mtp
            ? profile with { MtpDraftModelRelativePath = "models/./DRAFT.GGUF", MtpEnabled = true }
            : profile with { VisionProjectorRelativePath = "models/./VISION.GGUF", VisionEnabled = true };
        var before = await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root));
        var resolved = ModelResourceCatalog.Resolve(alternate, _root);
        Assert.True(kind == ModelResourceKind.Mtp ? MtpValidationFingerprint.IsCurrent(resolved, _root) : VisionValidationFingerprint.IsCurrent(resolved, _root));
        Assert.Equal(alternate.MtpEnabled, resolved.MtpEnabled);
        Assert.Equal(alternate.VisionEnabled, resolved.VisionEnabled);
        Assert.Equal(before, await File.ReadAllTextAsync(ModelResourceCatalog.FilePath(Model, _root)));
    }

    private void WriteGguf(string template)
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(_root, Model.ModelRelativePath)), Encoding.UTF8);
        writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(3ul);
        WriteString(writer, "general.architecture"); writer.Write(8u); WriteString(writer, "qwen3");
        WriteString(writer, "tokenizer.chat_template"); writer.Write(8u); WriteString(writer, template);
        WriteString(writer, "qwen3.nextn_predict_layers"); writer.Write(4u); writer.Write(1u);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length); writer.Write(bytes);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
