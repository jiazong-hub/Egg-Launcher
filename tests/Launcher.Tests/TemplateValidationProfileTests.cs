using System.Text.Json;
using Launcher.Models.Profiles;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class TemplateValidationProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-template-probe", Guid.NewGuid().ToString("N"));
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 16384,
        CompactionSafetyReserve = 4096,
        ChatTemplateRelativePath = "scripts/templates/selected.jinja",
    };

    public TemplateValidationProfileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        File.WriteAllText(Path.Combine(_root, Model.ModelRelativePath), "fixture-model");
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "fixture-runtime");
        File.WriteAllText(Path.Combine(_root, Model.ChatTemplateRelativePath!), "{{ messages }}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleThinkingSwitch_NoLongerBlocksRender_ButStillBlocksNormalStartup(bool enabled)
    {
        var source = Model with { ThinkingEnabled = enabled, SupportsThinkingSwitch = null };
        var original = JsonSerializer.Serialize(source);
        Assert.Throws<InvalidDataException>(() => RouterPresetGenerator.Generate(source, _root, true));

        var probe = TemplateValidationProfile.Create(source);
        var preset = RouterPresetGenerator.Generate(probe, _root, true);

        Assert.Null(probe.ThinkingEnabled);
        Assert.DoesNotContain("reasoning =", preset);
        Assert.DoesNotContain("reasoning-budget", preset);
        Assert.Contains("chat-template-file =", preset);
        Assert.Equal(original, JsonSerializer.Serialize(source));
        Assert.Throws<InvalidDataException>(() => RouterPresetGenerator.Generate(source, _root, true));
    }

    [Fact]
    public void MissingAttachmentsAndUnrelatedInvalidSettings_AreExcludedFromProbe()
    {
        var source = Model with
        {
            MtpEnabled = true,
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/deleted-mtp.gguf",
            MtpDraftMaxTokens = -1,
            VisionEnabled = true,
            VisionSource = VisionSourceKind.External,
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionProjectorRelativePath = "models/deleted-mmproj.gguf",
            VisionImageMaxTokens = -1,
            ContextShiftEnabled = true,
            BatchSize = -1,
            MicroBatchSize = -1,
            Ngram = new() { Enabled = true, Types = ["invalid-strategy"] },
            RuntimeParameters = new() { DraftMicroBatchSize = -1 },
            CustomParameters = new()
            {
                Llama = [new() { Name = "--future-feature", Value = "custom-value" }],
                Codex = [new() { Name = "future_feature", Value = "true" }],
            },
            DefaultParameters = new() { ThinkingEnabled = true },
            ExtraArguments = new Dictionary<string, string?>
            {
                ["reasoning"] = "on",
                ["chat-template-kwargs"] = "{\"enable_thinking\":true}",
                ["keep"] = "16000",
                ["ctx-checkpoints"] = "bad",
                ["future-feature"] = "unsafe-for-probe",
            },
        };
        var before = JsonSerializer.Serialize(source);
        var probe = TemplateValidationProfile.Create(source);
        var preset = RouterPresetGenerator.Generate(probe, _root, true);

        Assert.Empty(ModelProfileValidator.Validate(probe, _root));
        Assert.False(probe.MtpEnabled);
        Assert.False(probe.VisionEnabled);
        Assert.Null(probe.MtpDraftModelRelativePath);
        Assert.Null(probe.VisionProjectorRelativePath);
        Assert.Null(probe.DefaultParameters);
        Assert.Empty(probe.CustomParameters.Llama);
        Assert.Empty(probe.CustomParameters.Codex);
        Assert.Empty(probe.ExtraArguments);
        Assert.Contains("no-mmproj = true", preset);
        foreach (var excluded in new[] { "spec-", "lookup-cache", "deleted-", "reasoning", "future-feature", "keep =", "ctx-checkpoints" })
            Assert.DoesNotContain(excluded, preset);
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Theory]
    [InlineData(ModelType.Dense)]
    [InlineData(ModelType.MoE)]
    public void ModelIdentityAndLoadingControls_ArePreserved(ModelType type)
    {
        var source = Model with
        {
            ModelType = type,
            ModelTypeSource = ModelTypeSource.UserSelected,
            GpuLayers = "12",
            Device = "Vulkan0",
            FlashAttention = "on",
            CacheTypeK = "q8_0",
            MoeExpertPlacement = type == ModelType.MoE ? MoeExpertPlacement.CpuFirstLayers : null,
            CpuMoeLayers = type == ModelType.MoE ? 4 : null,
            ExtraArguments = new Dictionary<string, string?>
            {
                ["load-mode"] = "mmap",
                ["fit"] = "on",
                ["tensor-split"] = "1,1",
                ["threads"] = "8",
            },
        };
        var probe = TemplateValidationProfile.Create(source);
        var preset = RouterPresetGenerator.Generate(probe, _root, true);
        Assert.Equal(source.Id, probe.Id);
        Assert.Equal(source.Alias, probe.Alias);
        Assert.Equal(source.ModelRelativePath, probe.ModelRelativePath);
        Assert.Equal(source.ModelType, probe.ModelType);
        Assert.Equal(source.ModelTypeSource, probe.ModelTypeSource);
        Assert.Contains("n-gpu-layers = 12", preset);
        Assert.Contains("device = Vulkan0", preset);
        Assert.Contains("cache-type-k = q8_0", preset);
        foreach (var pair in source.ExtraArguments) Assert.Contains($"{pair.Key} = {pair.Value}", preset);
        Assert.Equal(type == ModelType.MoE, preset.Contains("n-cpu-moe = 4", StringComparison.Ordinal));
        Assert.Equal(4096, probe.ContextSize);
        Assert.Equal(1, probe.Parallel);
        Assert.Equal(-1, probe.IdleSleepSeconds);
    }

    [Theory]
    [InlineData("../escaped.gguf", "scripts/templates/selected.jinja")]
    [InlineData("models/main.gguf", "scripts/outside.jinja")]
    public void ModelAndTemplatePathGuards_StillApply(string model, string template)
    {
        var probe = TemplateValidationProfile.Create(Model with { ModelRelativePath = model, ChatTemplateRelativePath = template });
        Assert.Throws<InvalidDataException>(() => RouterPresetGenerator.Generate(probe, _root, true));
    }

    [Fact]
    public void InvalidLoadingControl_IsStillRejected()
    {
        var probe = TemplateValidationProfile.Create(Model with { GpuLayers = "invalid" });
        Assert.Throws<InvalidDataException>(() => RouterPresetGenerator.Generate(probe, _root, true));
    }

    [Theory]
    [InlineData("models/main.gguf")]
    [InlineData("scripts/templates/selected.jinja")]
    public void MissingModelOrTemplate_CannotReachNativeCheck(string deletedPath)
    {
        File.Delete(Path.Combine(_root, deletedPath));
        // PrepareChatTemplateAsync captures this signature before starting its native render probe.
        Assert.Throws<FileNotFoundException>(() => ChatTemplateValidationCache.Signature(Model, _root));
    }

    [Fact]
    public async Task ExpiredTemplate_CanBeRegisteredAgain_WithoutChangingConfigurationOrOtherEvidence()
    {
        var source = Model with
        {
            ConfigurationInitialized = true,
            ThinkingEnabled = true,
            SupportsThinkingSwitch = null,
            MtpEnabled = true,
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/missing.gguf",
        };
        var configurationDirectory = ModelConfigurationFile.GetDirectory(source, _root);
        Directory.CreateDirectory(configurationDirectory);
        var configuration = Path.Combine(configurationDirectory, "chosen.json");
        source = source with { PreferredConfigurationPath = configuration };
        var saved = JsonSerializer.Serialize(new ModelConfigurationDocument
        {
            ModelId = source.Id,
            ModelRelativePath = source.ModelRelativePath,
            Parameters = source,
        });
        await File.WriteAllTextAsync(configuration, saved);
        await ChatTemplateValidationCache.WriteAsync(source, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(source, _root, ModelResourceKind.Template);
        Assert.NotNull(ModelResourceManagement.FindReusable(source, _root, ModelResourceKind.Template));

        await File.AppendAllTextAsync(Path.Combine(_root, "llama-server.exe"), "updated-runtime");
        Assert.Null(ModelResourceManagement.FindReusable(source, _root, ModelResourceKind.Template));
        var resolved = ModelResourceCatalog.Resolve(source, _root);
        var probe = TemplateValidationProfile.Create(resolved);
        Assert.NotEmpty(RouterPresetGenerator.Generate(probe, _root, true));
        // Simulate successful native rendering: only this step is replaced by a fixture.
        await ChatTemplateValidationCache.WriteAsync(source, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(source, _root, ModelResourceKind.Template);
        Assert.NotNull(ModelResourceManagement.FindReusable(source, _root, ModelResourceKind.Template));
        var record = Assert.Single(ModelResourceCatalog.Read(source, _root).Records);
        Assert.Equal(ModelResourceKind.Template, record.Kind);
        Assert.True(record.Evidence.ThinkingEnabled);
        Assert.True(record.Evidence.MtpEnabled);
        Assert.Equal(saved, await File.ReadAllTextAsync(configuration));
        Assert.Equal(configuration, source.PreferredConfigurationPath);
        Assert.Null(ModelResourceManagement.FindReusable(source, _root, ModelResourceKind.Reasoning));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
