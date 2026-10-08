using System.Text.Json;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ReasoningDefaultSelectionTests
{
    private static ModelProfile Profile => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = "model.gguf",
        ContextSize = 81920,
        CompactionSafetyReserve = 20480,
        SupportsThinkingSwitch = true,
        DefaultThinkingEnabled = true,
        ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
        SupportedReasoningLevels = ["xhigh", "medium", "low"],
        DefaultReasoningLevel = "xhigh",
        ExposeReasoningEffortInChatGpt = true,
        ReasoningResponsesVerified = true,
        ReasoningClientCompatible = true
    };

    [Theory]
    [InlineData(null, "xhigh")]
    [InlineData("medium", "medium")]
    [InlineData("low", "low")]
    [InlineData("high", "xhigh")]
    [InlineData("Medium", "xhigh")]
    public void ExactVerifiedPreferenceOverridesNativeDefaultWithoutMapping(string? preference, string expected)
    {
        var profile = Profile with { PreferredReasoningLevel = preference, ReasoningLevelAliases = new Dictionary<string, string> { ["high"] = "xhigh" } };
        Assert.Equal(expected, ReasoningDefaultSelection.ForCodex(profile));
        Assert.Equal(expected, ReasoningDefaultSelection.ForCodex(profile with { ReverseReasoningLevelDisplayOrder = true }));
        Assert.Equal("xhigh", profile.DefaultReasoningLevel);
        Assert.Equal(["xhigh", "medium", "low"], profile.SupportedReasoningLevels);
    }

    [Fact]
    public void UnavailableControlDoesNotTransmitDefaultAndPreservesPreference()
    {
        var profile = Profile with { PreferredReasoningLevel = "medium" };
        foreach (var unavailable in new[]
        {
            profile with { ExposeReasoningEffortInChatGpt = false }, profile with { ReasoningResponsesVerified = false },
            profile with { ReasoningClientCompatible = null }, profile with { ReasoningCapabilityStatus = ReasoningCapabilityStatus.Unknown }
        })
        {
            Assert.Null(ReasoningDefaultSelection.ForCodex(unavailable));
            Assert.Equal("medium", unavailable.PreferredReasoningLevel);
        }
    }

    [Fact]
    public void CustomNativeValueIsPassedExactlyAndMissingNativeDefaultIsNotInvented()
    {
        var custom = Profile with { SupportedReasoningLevels = ["Vendor_Level", "low"], DefaultReasoningLevel = null, PreferredReasoningLevel = "Vendor_Level" };
        Assert.Equal("Vendor_Level", ReasoningDefaultSelection.ForCodex(custom));
        Assert.Null(ReasoningDefaultSelection.ForCodex(custom with { PreferredReasoningLevel = null }));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(null, false, true)]
    [InlineData(null, null, false)]
    [InlineData(true, true, true)]
    public async Task VerifiedEffortIsIndependentOfThinkingState(bool? thinking, bool? defaultThinking, bool supportsSwitch)
    {
        var root = Path.Combine(Path.GetTempPath(), "egg-independent-effort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Profile with
            {
                ThinkingEnabled = thinking,
                DefaultThinkingEnabled = defaultThinking,
                SupportsThinkingSwitch = supportsSwitch,
                PreferredReasoningLevel = "medium",
                ReverseReasoningLevelDisplayOrder = true,
            };
            Assert.Equal("medium", ReasoningDefaultSelection.ForCodex(profile));
            var preset = Path.Combine(root, "models.ini");
            var catalog = Path.Combine(root, "models.json");
            await LocalModelConfigurationWriter.WriteAsync(profile, root, preset, catalog);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(catalog));
            var model = document.RootElement.GetProperty("models")[0];
            Assert.Equal("medium", model.GetProperty("default_reasoning_level").GetString());
            Assert.Equal(["low", "medium", "xhigh"], model.GetProperty("supported_reasoning_levels")
                .EnumerateArray().Select(level => level.GetProperty("effort").GetString()!).ToArray());
            if (thinking == false)
            {
                var text = await File.ReadAllTextAsync(preset);
                Assert.Contains("reasoning = off", text);
                Assert.Contains("reasoning-budget = 0", text);
            }
            await LocalModelConfigurationWriter.WriteAsync(profile with { ExposeReasoningEffortInChatGpt = false }, root, preset, catalog);
            using var disabled = JsonDocument.Parse(await File.ReadAllTextAsync(catalog));
            Assert.Empty(disabled.RootElement.GetProperty("models")[0].GetProperty("supported_reasoning_levels").EnumerateArray());
            Assert.False(disabled.RootElement.GetProperty("models")[0].TryGetProperty("default_reasoning_level", out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PreferenceRoundTripsThroughProfileAndDefaultSnapshotIndependentlyOfNativeDefault()
    {
        var profile = Profile with { PreferredReasoningLevel = "medium" };
        var loaded = JsonSerializer.Deserialize<ModelProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal("medium", loaded.PreferredReasoningLevel);
        var defaults = ModelParameterDefaults.FromProfile(loaded);
        var restored = defaults.ApplyTo(profile with { PreferredReasoningLevel = "low", DefaultReasoningLevel = "low" });
        Assert.Equal("medium", restored.PreferredReasoningLevel);
        Assert.Equal("low", restored.DefaultReasoningLevel);
        Assert.Null(ModelParameterDefaults.FromProfile(Profile).PreferredReasoningLevel);
    }

    [Fact]
    public async Task GeneratedCatalogUsesPreferenceWhileNativePresetAndCapabilityFingerprintsRemainUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "egg-default-effort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "active.jinja"), "test template");
            var original = Profile with { ChatTemplateRelativePath = "active.jinja" };
            var preferred = original with { PreferredReasoningLevel = "medium", ReverseReasoningLevelDisplayOrder = true };
            var preset = Path.Combine(root, "models.ini");
            var catalog = Path.Combine(root, "models.json");
            await LocalModelConfigurationWriter.WriteAsync(original, root, preset, catalog);
            var originalPreset = await File.ReadAllTextAsync(preset);
            await LocalModelConfigurationWriter.WriteAsync(preferred, root, preset, catalog);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(catalog));
            var model = document.RootElement.GetProperty("models")[0];
            Assert.Equal("medium", model.GetProperty("default_reasoning_level").GetString());
            Assert.Equal("low", model.GetProperty("supported_reasoning_levels")[0].GetProperty("effort").GetString());
            Assert.Equal(originalPreset, await File.ReadAllTextAsync(preset));
            var preferenceOnly = original with { PreferredReasoningLevel = "medium" };
            Assert.Equal(ReasoningValidationFingerprint.Compute(original, root), ReasoningValidationFingerprint.Compute(preferenceOnly, root));
            Assert.Equal(ContextShiftCapabilityCache.Fingerprint(original, root), ContextShiftCapabilityCache.Fingerprint(preferenceOnly, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
