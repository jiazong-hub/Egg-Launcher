using System.Text.Json;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ReasoningValidationScopeTests
{
    [Theory]
    [InlineData("fit")]
    [InlineData("fit-target")]
    [InlineData("fit-ctx")]
    [InlineData("threads")]
    [InlineData("threads-batch")]
    [InlineData("load-mode")]
    [InlineData("lazy-mode")]
    [InlineData("n-cpu-ffn")]
    [InlineData("split-mode")]
    [InlineData("tensor-split")]
    [InlineData("main-gpu")]
    [InlineData("numa")]
    [InlineData("override-tensor")]
    [InlineData("kv-offload")]
    [InlineData("no-kv-offload")]
    [InlineData("repack")]
    [InlineData("no-repack")]
    [InlineData("op-offload")]
    [InlineData("no-op-offload")]
    [InlineData("no-host")]
    [InlineData("ctx-checkpoints")]
    [InlineData("swa-checkpoints")]
    [InlineData("keep")]
    [InlineData("cpu-moe")]
    [InlineData("n-cpu-moe")]
    public void IndependentArgumentEditsPreserveProofAndPreferences(string key)
    {
        using var f = new Fixture();
        var changed = f.Profile with { ExtraArguments = new Dictionary<string, string?> { [key] = "changed" } };
        var check = ReasoningValidationState.Check(changed, f.Root);
        Assert.Equal(ReasoningValidationStateKind.Current, check.State);
        Assert.Same(changed, check.ApplyTo(changed));
        Assert.Equal(f.Profile.ReasoningCapabilityCheckedAtUtc, changed.ReasoningCapabilityCheckedAtUtc);
        Assert.Equal("medium", changed.PreferredReasoningLevel);
        Assert.True(changed.ShowThinkingProcess);
        Assert.True(changed.ExposeReasoningEffortInChatGpt);
        Assert.True(changed.ThinkingEnabled);
        var removed = changed with { ExtraArguments = new Dictionary<string, string?>() };
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(removed, f.Root).State);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("reasoning-effort")]
    [InlineData("reasoning-budget")]
    [InlineData("chat-template-kwargs")]
    [InlineData("rope-scaling")]
    [InlineData("yarn-ext-factor")]
    [InlineData("swa-full")]
    [InlineData("unknown-future-option")]
    public void RelatedAndUnknownArgumentsStillRequireVerification(string key)
    {
        using var f = new Fixture();
        var changed = f.Profile with { ExtraArguments = new Dictionary<string, string?> { [key] = "changed" } };
        var check = ReasoningValidationState.Check(changed, f.Root);
        Assert.Equal(ReasoningValidationStateKind.Expired, check.State);
        Assert.Contains("相关参数", check.Reason);
        Assert.DoesNotContain("过期", check.Reason);
        var reset = check.ApplyTo(changed);
        Assert.False(ReasoningValidationState.RequiresCurrentProof(reset));
        Assert.True(reset.ShowThinkingProcess);
        Assert.Equal("medium", reset.PreferredReasoningLevel);
        Assert.Null(reset.ThinkingEnabled);
        Assert.Empty(reset.SupportedReasoningLevels);
    }

    [Fact]
    public void LegacyProofMigratesBeforeIndependentEditAndKeepsTimestamp()
    {
        using var f = new Fixture();
        var original = f.Profile with { ExtraArguments = new Dictionary<string, string?> { ["fit-target"] = "1024" }, ReasoningValidationBasis = null };
        original = original with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.ComputeLegacy(original, f.Root) };
        var originalSignature = original.ReasoningCapabilitySignature;
        var upgraded = ReasoningValidationState.Check(original, f.Root).ApplyTo(original);
        Assert.NotEqual(originalSignature, upgraded.ReasoningCapabilitySignature);
        Assert.Equal(originalSignature, original.ReasoningCapabilitySignature);
        Assert.Null(original.ReasoningValidationBasis);
        Assert.Equal(original.ReasoningCapabilityCheckedAtUtc, upgraded.ReasoningCapabilityCheckedAtUtc);
        var changed = upgraded with { ExtraArguments = new Dictionary<string, string?> { ["fit-target"] = "2048" } };
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(changed, f.Root).State);
        var restored = ModelProfileFactory.RestoreModelDefaults(ModelProfileFactory.SaveCurrentParametersAsDefault(upgraded)) with
        { ExtraArguments = new Dictionary<string, string?> { ["threads"] = "8" } };
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(restored, f.Root).State);
        Assert.Equal(ContextShiftCapabilityCache.Fingerprint(original, f.Root), ContextShiftCapabilityCache.Fingerprint(upgraded, f.Root));
        var roundTrip = JsonSerializer.Deserialize<ModelProfile>(JsonSerializer.Serialize(upgraded))!;
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(roundTrip, f.Root).State);
    }

    [Theory]
    [InlineData("model.gguf", "模型文件")]
    [InlineData("active.jinja", "聊天模板")]
    [InlineData("llama-server.exe", "可执行文件")]
    [InlineData("runtime.dll", "DLL")]
    [InlineData("codex.exe", "CLI")]
    public void FileChangesAreIdentifiedAndLegacyProofCannotBypassThem(string filename, string label)
    {
        using var f = new Fixture();
        var legacy = f.Profile with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.ComputeLegacy(f.Profile, f.Root), ReasoningValidationBasis = null };
        File.AppendAllText(Path.Combine(f.Root, filename), "changed");
        var check = ReasoningValidationState.Check(f.Profile, f.Root);
        Assert.Equal(ReasoningValidationStateKind.Expired, check.State);
        Assert.Contains(label, check.Reason);
        Assert.Equal(ReasoningValidationStateKind.Expired, ReasoningValidationState.Check(legacy, f.Root).State);
    }

    [Fact]
    public void NoAgeLimitAndUnreadableTemplateIsNotReportedAsExpiry()
    {
        using var f = new Fixture();
        var old = f.Profile with { ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow.AddYears(-10) };
        Assert.Equal(ReasoningValidationStateKind.Current, ReasoningValidationState.Check(old, f.Root).State);
        File.Delete(Path.Combine(f.Root, "active.jinja"));
        var check = ReasoningValidationState.Check(old, f.Root);
        Assert.Equal(ReasoningValidationStateKind.Unreadable, check.State);
        Assert.Contains("读取失败", check.Reason);
        Assert.DoesNotContain("过期", check.Reason);
    }

    [Fact]
    public void LegacyProofWithChangedRelevantArgumentsIsNotMigrated()
    {
        using var f = new Fixture();
        var legacy = f.Profile with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.ComputeLegacy(f.Profile, f.Root), ReasoningValidationBasis = null };
        var changed = legacy with { Jinja = false };
        Assert.Equal(ReasoningValidationStateKind.Expired, ReasoningValidationState.Check(changed, f.Root).State);
        Assert.Null(ReasoningValidationState.Check(changed, f.Root).ApplyTo(changed).ReasoningCapabilitySignature);
    }

    [Fact]
    public async Task ActiveSaveAndTrueInvalidationKeepDisplayPreferenceWithoutBlockingConfigurationSync()
    {
        using var f = new Fixture();
        var home = Directory.CreateDirectory(Path.Combine(f.Root, "home")).FullName;
        var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(f.Root, "data"));
        var config = Path.Combine(home, "config.toml");
        await File.WriteAllTextAsync(config, "model = \"official\"\nshow_raw_agent_reasoning = false\n");
        using var settings = new JsonSettingsStore(paths.SettingsFile);
        var stopped = new Detector();
        var coordinator = new ModeSwitchCoordinator(settings, stopped, new ChatGptConfigTransactionService(stopped), paths);
        Task Sync(ModelProfile p, CancellationToken token) => coordinator.SwitchToLocalAsync(new LocalModeSwitchRequest
        { CodexHome = home, RuntimeRoot = f.Root, Profile = p, RouterPort = 18080 }, token);
        await Sync(f.Profile, default);
        var changed = f.Profile with { ExtraArguments = new Dictionary<string, string?> { ["fit-target"] = "2048", ["threads"] = "8" } };
        var store = new JsonModelProfileStore();
        var writer = new ModelArtifactWriter(store, new ModelArtifactTransaction());
        await writer.SaveAsync(changed, f.Root, paths.RouterPresetFile, paths.LocalModelCatalogFile, true, synchronizeActiveCodexConfiguration: Sync);
        var saved = (await store.LoadAsync(f.Root)).Profiles.Single();
        Assert.Equal(ReasoningCapabilityStatus.Verified, saved.ReasoningCapabilityStatus);
        Assert.Equal("2048", saved.ExtraArguments["fit-target"]);
        Assert.True(saved.ShowThinkingProcess);
        Assert.Contains("medium", await File.ReadAllTextAsync(config));
        Assert.Contains("show_raw_agent_reasoning = true", await File.ReadAllTextAsync(config));
        Assert.True(saved.ThinkingEnabled);
        Assert.Contains("fit-target = 2048", await File.ReadAllTextAsync(paths.RouterPresetFile));
        await writer.SaveAsync(saved with { ThinkingEnabled = false }, f.Root, paths.RouterPresetFile, paths.LocalModelCatalogFile, true, synchronizeActiveCodexConfiguration: Sync);
        saved = (await store.LoadAsync(f.Root)).Profiles.Single();
        Assert.False(saved.ThinkingEnabled);
        Assert.True(saved.ExposeReasoningEffortInChatGpt);
        Assert.True(saved.ShowThinkingProcess);
        Assert.Contains("model_reasoning_effort = \"medium\"", await File.ReadAllTextAsync(config));
        Assert.Contains("show_raw_agent_reasoning = false", await File.ReadAllTextAsync(config));
        Assert.Contains("reasoning = off", await File.ReadAllTextAsync(paths.RouterPresetFile));
        File.AppendAllText(Path.Combine(f.Root, "active.jinja"), "changed");
        await writer.SaveAsync(saved, f.Root, paths.RouterPresetFile, paths.LocalModelCatalogFile, true, synchronizeActiveCodexConfiguration: Sync);
        var reset = (await store.LoadAsync(f.Root)).Profiles.Single();
        Assert.True(reset.ShowThinkingProcess);
        Assert.Equal("medium", reset.PreferredReasoningLevel);
        Assert.Null(reset.ThinkingEnabled);
        Assert.DoesNotContain("enable_thinking", await File.ReadAllTextAsync(paths.RouterPresetFile));
        Assert.Contains("show_raw_agent_reasoning = false", await File.ReadAllTextAsync(config));
        var result = await ReasoningStartupGuard.EnsureAsync(reset, await settings.LoadAsync(), settings, paths, stopped, default);
        Assert.Same(reset, result);
        await coordinator.SwitchToOpenAIAsync();
        Assert.Contains("model = \"official\"", await File.ReadAllTextAsync(config));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void DisplayPreferenceRequiresProofOnlyWhenItWouldActuallyBeEnabled(bool? supports, bool? defaultEnabled, bool expected)
    {
        using var f = new Fixture();
        var p = f.Profile with { ThinkingEnabled = null, ExposeReasoningEffortInChatGpt = false, SupportsThinkingSwitch = supports, DefaultThinkingEnabled = defaultEnabled };
        Assert.Equal(expected, ReasoningValidationState.RequiresCurrentProof(p));
        Assert.True(ReasoningValidationState.RequiresCurrentProof(p with { ThinkingEnabled = false }));
        Assert.True(ReasoningValidationState.RequiresCurrentProof(p with { ExposeReasoningEffortInChatGpt = true }));
    }

    private sealed class Detector : IChatGptClientDetector { public bool IsRunning() => false; }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ReasoningScope", Guid.NewGuid().ToString("N"))).FullName;
        public ModelProfile Profile { get; }
        public Fixture()
        {
            foreach (var name in new[] { "model.gguf", "active.jinja", "llama-server.exe", "runtime.dll", "codex.exe" })
                File.WriteAllText(Path.Combine(Root, name), "fixture");
            var p = new ModelProfile
            {
                Id = "scope",
                Alias = "scope",
                DisplayName = "Scope",
                ModelRelativePath = "model.gguf",
                ModelType = ModelType.Dense,
                ContextSize = 8192,
                CompactionSafetyReserve = 2048,
                ChatTemplateRelativePath = "active.jinja",
                SupportsThinkingSwitch = true,
                DefaultThinkingEnabled = true,
                ThinkingEnabled = true,
                ShowThinkingProcess = true,
                ExposeReasoningEffortInChatGpt = true,
                ReverseReasoningLevelDisplayOrder = true,
                ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
                ReasoningResponsesVerified = true,
                ReasoningClientCompatible = true,
                ReasoningClientExecutablePath = Path.Combine(Root, "codex.exe"),
                SupportedReasoningLevels = ["xhigh", "medium", "low"],
                DefaultReasoningLevel = "xhigh",
                PreferredReasoningLevel = "medium",
                ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow,
            };
            Profile = ReasoningValidationState.Check(p with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.Compute(p, Root) }, Root).ApplyTo(p with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.Compute(p, Root) });
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
