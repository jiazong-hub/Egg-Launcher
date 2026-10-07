using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ReasoningStartupGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredProofIsSynchronizedWhenClosedAndBlockedWhenRunning(bool running)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ThinkingStartupTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var runtime = Directory.CreateDirectory(Path.Combine(root, "runtime")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(root, "codex")).FullName;
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            const string official = "model = \"official\"\nmodel_reasoning_effort = \"high\"\nplan_mode_reasoning_effort = \"high\"\n";
            var config = Path.Combine(home, "config.toml");
            await File.WriteAllTextAsync(config, official);
            await File.WriteAllTextAsync(Path.Combine(runtime, "model.gguf"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(runtime, "llama-server.exe"), "fixture");
            var template = Path.Combine(runtime, "active.jinja");
            await File.WriteAllTextAsync(template, "fixture template");
            var profile = new ModelProfile
            {
                Id = "test",
                Alias = "test",
                DisplayName = "Test",
                ModelRelativePath = "model.gguf",
                ContextSize = 8192,
                CompactionSafetyReserve = 2048,
                ModelType = ModelType.Dense,
                ChatTemplateRelativePath = "active.jinja",
                ThinkingEnabled = true,
                ShowThinkingProcess = true,
                SupportsThinkingSwitch = true,
                DefaultThinkingEnabled = true,
                ExposeReasoningEffortInChatGpt = true,
                ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
                SupportedReasoningLevels = ["low", "Vendor_Default"],
                DefaultReasoningLevel = "Vendor_Default",
                ReasoningResponsesVerified = true,
                ReasoningClientCompatible = true,
                ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow,
            };
            profile = profile with { ReasoningCapabilitySignature = ReasoningValidationFingerprint.Compute(profile, runtime) };
            await new JsonModelProfileStore().SaveAsync(runtime, profile);
            using var store = new JsonSettingsStore(paths.SettingsFile);
            var stopped = new Detector(false);
            var coordinator = new ModeSwitchCoordinator(store, stopped, new ChatGptConfigTransactionService(stopped), paths);
            await coordinator.SwitchToLocalAsync(new LocalModeSwitchRequest { CodexHome = home, RuntimeRoot = runtime, Profile = profile, RouterPort = 18080 });
            var settings = await store.LoadAsync();
            Assert.Same(profile, await ReasoningStartupGuard.EnsureAsync(profile, settings, store, paths, new Detector(running), default));
            var beforeConfig = await File.ReadAllTextAsync(config);
            var beforePreset = await File.ReadAllTextAsync(paths.RouterPresetFile);
            var beforeCatalog = await File.ReadAllTextAsync(paths.LocalModelCatalogFile);
            await File.AppendAllTextAsync(template, " changed");
            if (running)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReasoningStartupGuard.EnsureAsync(profile, settings, store, paths, new Detector(true), default));
                Assert.Contains("关闭 Codex", error.Message);
                Assert.Equal(beforeConfig, await File.ReadAllTextAsync(config));
                Assert.Equal(beforePreset, await File.ReadAllTextAsync(paths.RouterPresetFile));
                Assert.Equal(beforeCatalog, await File.ReadAllTextAsync(paths.LocalModelCatalogFile));
                Assert.True((await new JsonModelProfileStore().LoadAsync(runtime)).Profiles.Single().ThinkingEnabled);
            }
            else
            {
                var reset = await ReasoningStartupGuard.EnsureAsync(profile, settings, store, paths, stopped, default);
                Assert.Null(reset.ThinkingEnabled);
                Assert.True(reset.ShowThinkingProcess);
                Assert.False(reset.ExposeReasoningEffortInChatGpt);
                Assert.Null((await new JsonModelProfileStore().LoadAsync(runtime)).Profiles.Single().ThinkingEnabled);
                Assert.DoesNotContain("enable_thinking", await File.ReadAllTextAsync(paths.RouterPresetFile));
                Assert.DoesNotContain("Vendor_Default", await File.ReadAllTextAsync(paths.LocalModelCatalogFile));
                Assert.DoesNotContain("model_reasoning_effort", await File.ReadAllTextAsync(config));
                await coordinator.SwitchToOpenAIAsync();
                var restored = await File.ReadAllTextAsync(config);
                Assert.Contains("model = \"official\"", restored);
                Assert.Contains("model_reasoning_effort = \"high\"", restored);
                Assert.Contains("plan_mode_reasoning_effort = \"high\"", restored);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Detector(bool running) : IChatGptClientDetector { public bool IsRunning() => running; }
}
