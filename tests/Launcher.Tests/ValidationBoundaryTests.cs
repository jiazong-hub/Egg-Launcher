using System.Reflection;
using System.Text.Json;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ValidationBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-validation-boundaries", Guid.NewGuid().ToString("N"));
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 65536,
        CompactionSafetyReserve = 8192,
        ResourceCatalogInitialized = true,
    };

    public ValidationBoundaryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        foreach (var file in new[] { "models/main.gguf", "models/draft.gguf", "models/vision.gguf", "llama-server.exe", "runtime.dll", "scripts/templates/active.jinja" })
            File.WriteAllText(Path.Combine(_root, file), file);
    }

    [Fact]
    public async Task StaleEnabledIntent_CanBeSavedAndAppliedButStillCannotStart()
    {
        var requested = Model with
        {
            MtpEnabled = true,
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = "models/missing-mtp.gguf",
            VisionEnabled = true,
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/missing-vision.gguf",
            ThinkingEnabled = true,
            ExposeReasoningEffortInChatGpt = true,
            ChatTemplateRelativePath = "scripts/templates/missing.jinja",
        };
        Assert.Empty(ModelProfileValidator.Validate(requested, _root, requireRuntimeEvidence: false));
        Assert.NotEmpty(ModelProfileValidator.Validate(requested, _root));
        var store = new JsonModelProfileStore();
        await store.SaveRegistrationAsync(_root, requested);
        var registered = await store.ReadRegistrationAsync(_root, requested);
        var original = await File.ReadAllTextAsync(registered.PreferredConfigurationPath!);
        var loaded = await ModelConfigurationFile.ReadAsync(registered.PreferredConfigurationPath!, registered, _root);
        await ModelConfigurationFile.WriteAsync(registered.PreferredConfigurationPath!, loaded, _root, requireExisting: true);
        await store.UpdatePreferredConfigurationAsync(_root, registered, registered.PreferredConfigurationPath!, () => true);
        Assert.Equal(original, await File.ReadAllTextAsync(registered.PreferredConfigurationPath!));
        var reloaded = Assert.Single((await store.LoadAsync(_root)).Profiles);
        Assert.True(reloaded.MtpEnabled);
        Assert.True(reloaded.VisionEnabled);
        Assert.True(reloaded.ThinkingEnabled);
        Assert.True(reloaded.ExposeReasoningEffortInChatGpt);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(reloaded, _root));
    }

    [Theory]
    [InlineData("missing-path")]
    [InlineData("range")]
    [InlineData("conflict")]
    [InlineData("outside-template")]
    [InlineData("duplicate-command")]
    public async Task SaveStillRejectsInvalidDataAndConflicts(string problem)
    {
        var candidate = problem switch
        {
            "missing-path" => Model with { MtpEnabled = true, MtpSource = MtpSourceKind.External },
            "range" => Model with { MtpDraftMinTokens = 20, MtpDraftMaxTokens = 10 },
            "conflict" => Model with { ContextShiftEnabled = true, VisionEnabled = true },
            "outside-template" => Model with { Jinja = false, ChatTemplateRelativePath = "models/outside.jinja" },
            _ => Model with { CustomParameters = new() { Llama = [new() { Name = "--ctx-size", Value = "4096" }] } },
        };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "invalid.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.WriteAsync(path, candidate, _root));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTemplate_OnlyBlocksWhenJinjaUsesIt(bool jinja)
    {
        var candidate = Model with { Jinja = jinja, ChatTemplateRelativePath = "scripts/templates/missing.jinja" };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "template.json");
        await ModelConfigurationFile.WriteAsync(path, candidate, _root);
        var registered = candidate with { ConfigurationInitialized = true, PreferredConfigurationPath = path };
        if (jinja)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(registered, _root));
            return;
        }
        var resolved = await ModelStartupConfiguration.ResolveAsync(registered, _root);
        typeof(ModeSwitchCoordinator).GetMethod("ValidateRuntimeAndProfile", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [resolved, _root]);
        Assert.DoesNotContain("chat-template-file", RouterPresetGenerator.Generate(resolved, _root, false));
        Assert.DoesNotContain("chat-template-file", BatchScriptGenerator.Generate(resolved, _root));
        Assert.Equal(candidate.ChatTemplateRelativePath, resolved.ChatTemplateRelativePath);
    }

    [Theory]
    [InlineData("display")]
    [InlineData("sleep")]
    [InlineData("disabled-mtp")]
    [InlineData("disabled-vision")]
    [InlineData("disabled-template")]
    [InlineData("custom")]
    [InlineData("cache")]
    public void ContextShift_IgnoresUnrelatedSettings(string change)
    {
        ContextShiftCapabilityCache.Write(Model, _root, new(true, "proof", DateTimeOffset.UtcNow));
        var changed = change switch
        {
            "display" => Model with { ReverseReasoningLevelDisplayOrder = true, DisplayName = "renamed" },
            "sleep" => Model with { IdleSleepSeconds = 600 },
            "disabled-mtp" => Model with { MtpDraftMaxTokens = 99, MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = "models/deleted.gguf" },
            "disabled-vision" => Model with { VisionImageMaxTokens = 8192, VisionProjectorRelativePath = "models/deleted.gguf" },
            "disabled-template" => Model with { Jinja = false, ChatTemplateRelativePath = "scripts/templates/deleted.jinja", ThinkingEnabled = true },
            "custom" => Model with { CustomParameters = new() { Llama = [new() { Name = "--future", Value = "1" }] } },
            _ => Model with { RuntimeParameters = new() { CacheRamMiB = 1024, CachePrompt = true } },
        };
        Assert.True(ContextShiftCapabilityCache.Read(changed, _root)?.Supported);
        Assert.Equal(ContextShiftCapabilityCache.Fingerprint(Model, _root), ContextShiftCapabilityCache.Fingerprint(changed, _root));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("parallel")]
    [InlineData("cache")]
    [InlineData("runtime")]
    [InlineData("model")]
    [InlineData("keep")]
    [InlineData("mtp")]
    public void ContextShift_RelevantChangesStillRequireVerification(string change)
    {
        var before = ContextShiftCapabilityCache.Fingerprint(Model, _root);
        var changed = change switch
        {
            "context" => Model with { ContextSize = 32768 },
            "parallel" => Model with { Parallel = 2 },
            "cache" => Model with { CacheTypeK = "q8_0" },
            "keep" => Model with { ExtraArguments = new Dictionary<string, string?> { ["keep"] = "16" } },
            "mtp" => Model with { MtpEnabled = true },
            _ => Model,
        };
        if (change is "runtime" or "model") File.AppendAllText(Path.Combine(_root, change == "runtime" ? "llama-server.exe" : "models/main.gguf"), "changed");
        Assert.NotEqual(before, ContextShiftCapabilityCache.Fingerprint(changed, _root));
    }

    [Fact]
    public void ExactLegacyShiftProof_MigratesThenSurvivesIndependentEdits()
    {
        var result = new ContextShiftCapabilityResult(true, "old proof", DateTimeOffset.UtcNow.AddYears(-2));
        var directory = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), ".context-shift-cache");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ContextShiftCapabilityCache.LegacyFingerprint(Model, _root) + ".json"), JsonSerializer.Serialize(result));
        Assert.Equal(result, ContextShiftCapabilityCache.Read(Model, _root));
        Assert.Equal(result, ContextShiftCapabilityCache.Read(Model with { IdleSleepSeconds = 600 }, _root));
        Assert.Null(ContextShiftCapabilityCache.Read(Model with { ContextSize = 32768 }, _root));
    }

    [Fact]
    public void NativeProbeProfiles_ExcludeUnrelatedRequirementsAndRemainValidAtLargeContext()
    {
        var source = Model with
        {
            ThinkingEnabled = true,
            VisionEnabled = false,
            VisionProjectorRelativePath = "models/deleted.gguf",
            MtpDraftMinTokens = -1,
            ChatTemplateRelativePath = "scripts/templates/active.jinja",
            ExtraArguments = new Dictionary<string, string?> { ["keep"] = "16000", ["threads"] = "4" },
        };
        var shift = ContextShiftProbeProfile.Create(source);
        Assert.Empty(ModelProfileValidator.Validate(shift, _root));
        Assert.Contains("keep = 16000", RouterPresetGenerator.Generate(shift, _root, true));
        var reasoning = TemplateValidationProfile.Create(source);
        Assert.Empty(ModelProfileValidator.Validate(reasoning, _root));
        Assert.DoesNotContain("keep =", RouterPresetGenerator.Generate(reasoning, _root, true));
        Assert.True(source.ThinkingEnabled);
        Assert.Equal("16000", source.ExtraArguments["keep"]);
    }

    [Fact]
    public async Task LegacyVisionProof_IsUpgradedWithoutRecheckingAndSurvivesRuntimeChange()
    {
        var proof = Model with
        {
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/vision.gguf",
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionValidatedAtUtc = DateTimeOffset.UtcNow.AddYears(-1),
            ChatTemplateRelativePath = "scripts/templates/active.jinja"
        };
        proof = proof with { VisionValidationSignature = VisionValidationFingerprint.ComputeLegacy(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Vision);
        await ModelResourceCatalog.EnsureMigratedAsync(Model, _root);
        var upgraded = Assert.Single(ModelResourceCatalog.Read(Model, _root).Records);
        Assert.Equal(VisionValidationFingerprint.Compute(proof, _root), upgraded.Evidence.VisionValidationSignature);
        Assert.Equal(proof.VisionValidatedAtUtc, upgraded.Evidence.VisionValidatedAtUtc);
        File.AppendAllText(Path.Combine(_root, "llama-server.exe"), "changed");
        File.Delete(Path.Combine(_root, "scripts/templates/active.jinja"));
        Assert.True(ModelResourceCatalog.IsCurrent(upgraded, _root));
        Assert.NotNull(ModelResourceManagement.FindReusable(proof with { ChatTemplateRelativePath = null }, _root, ModelResourceKind.Vision));
        File.AppendAllText(Path.Combine(_root, "models/vision.gguf"), "changed");
        Assert.False(ModelResourceCatalog.IsCurrent(upgraded, _root));
    }

    [Fact]
    public void ExpiredReasoningProof_ClearsAuthorityButPreservesRequestedSwitchesForEditing()
    {
        var requested = Model with { ThinkingEnabled = true, ExposeReasoningEffortInChatGpt = true, PreferredReasoningLevel = "high" };
        var edited = ReasoningValidationState.Check(requested, _root).ApplyTo(requested, preserveIntent: true);
        Assert.True(edited.ThinkingEnabled);
        Assert.True(edited.ExposeReasoningEffortInChatGpt);
        Assert.Equal("high", edited.PreferredReasoningLevel);
        Assert.Null(edited.SupportsThinkingSwitch);
        Assert.Empty(ModelProfileValidator.Validate(edited, _root, requireRuntimeEvidence: false));
        Assert.NotEmpty(ModelProfileValidator.Validate(edited, _root));
    }

    [Fact]
    public async Task CurrentVersion5ReasoningRecord_MigratesWithoutLosingTimeOrPreferences()
    {
        var proof = Model with
        {
            ThinkingEnabled = true,
            SupportsThinkingSwitch = true,
            ShowThinkingProcess = true,
            ChatTemplateRelativePath = "scripts/templates/active.jinja",
            ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow.AddYears(-1),
            ExtraArguments = new Dictionary<string, string?> { ["rope-scale"] = "2" },
        };
        proof = proof with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.ComputeVersion5(proof, _root) };
        await ModelResourceCatalog.RegisterAsync(proof, _root, ModelResourceKind.Reasoning);
        await ModelResourceCatalog.EnsureMigratedAsync(Model, _root);
        var upgraded = Assert.Single(ModelResourceCatalog.Read(Model, _root).Records).Evidence;
        Assert.Equal(ReasoningValidationFingerprint.Compute(upgraded, _root), upgraded.ReasoningCapabilitySignature);
        Assert.Equal(proof.ReasoningCapabilityCheckedAtUtc, upgraded.ReasoningCapabilityCheckedAtUtc);
        Assert.True(upgraded.ThinkingEnabled);
        Assert.True(upgraded.ShowThinkingProcess);
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(upgraded with { ExtraArguments = new Dictionary<string, string?>() }, _root).State);
        File.AppendAllText(Path.Combine(_root, "scripts/templates/active.jinja"), "changed");
        Assert.Equal(ReasoningValidationStateKind.Expired, ReasoningValidationState.Check(upgraded, _root).State);
    }

    [Theory]
    [InlineData(ModelResourceKind.Vision)]
    [InlineData(ModelResourceKind.Reasoning)]
    public async Task ExpiredLegacyEvidence_IsNotUpgradedOrRemoved(ModelResourceKind kind)
    {
        var proof = Model with
        {
            ChatTemplateRelativePath = "scripts/templates/active.jinja",
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = "models/vision.gguf",
            VisionValidatedAtUtc = DateTimeOffset.UtcNow
        };
        proof = proof with
        {
            VisionValidationSignature = VisionValidationFingerprint.ComputeLegacy(proof, _root),
            ReasoningCapabilitySignature = ReasoningValidationFingerprint.ComputeVersion5(proof, _root)
        };
        await ModelResourceCatalog.RegisterAsync(proof, _root, kind);
        File.AppendAllText(Path.Combine(_root, "models/main.gguf"), "changed");
        var before = File.ReadAllText(ModelResourceCatalog.FilePath(Model, _root));
        await ModelResourceCatalog.EnsureMigratedAsync(Model, _root);
        Assert.Equal(before, File.ReadAllText(ModelResourceCatalog.FilePath(Model, _root)));
        Assert.False(ModelResourceCatalog.IsCurrent(Assert.Single(ModelResourceCatalog.Read(Model, _root).Records), _root));
    }

    [Fact]
    public async Task SchemaUpgrade_DoesNotDiscardVerifiableReasoningRecordOrUserIntent()
    {
        var source = Model with
        {
            SchemaVersion = 12,
            ResourceCatalogInitialized = false,
            ThinkingEnabled = true,
            SupportsThinkingSwitch = true,
            ChatTemplateRelativePath = "scripts/templates/active.jinja",
            ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow.AddYears(-1)
        };
        source = source with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.ComputeVersion5(source, _root) };
        var directory = JsonModelProfileStore.GetProfilesDirectory(_root);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "model.json"), JsonSerializer.Serialize(source));
        var loaded = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        Assert.True(loaded.ThinkingEnabled);
        Assert.Equal(source.ReasoningCapabilityCheckedAtUtc, loaded.ReasoningCapabilityCheckedAtUtc);
        await ModelResourceCatalog.EnsureMigratedAsync(loaded, _root);
        var resolved = ModelResourceCatalog.Resolve(loaded, _root);
        Assert.True(resolved.SupportsThinkingSwitch);
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(resolved, _root).State);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
