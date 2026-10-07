using System.Text.Json;
using Launcher.ChatGPT.Catalog;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ReasoningDisplayOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Catalog_ReordersEntriesWithoutChangingValuesDescriptionsOrDefault(bool reverse)
    {
        string[] native = ["xhigh", "Vendor_Default", "medium", "low"];
        var options = new LocalModelCatalogOptions
        {
            Slug = "model",
            DisplayName = "Model",
            ContextWindow = 8192,
            SupportedReasoningLevels = native,
            DefaultReasoningLevel = "xhigh",
            ReverseReasoningLevelDisplayOrder = reverse,
        };
        using var original = JsonDocument.Parse(LocalModelCatalogBuilder.BuildJson(options with { ReverseReasoningLevelDisplayOrder = false }));
        using var actual = JsonDocument.Parse(LocalModelCatalogBuilder.BuildJson(options));
        var entries = actual.RootElement.GetProperty("models")[0].GetProperty("supported_reasoning_levels").EnumerateArray().ToArray();
        Assert.Equal(reverse ? native.Reverse() : native, entries.Select(entry => entry.GetProperty("effort").GetString()));
        var descriptions = original.RootElement.GetProperty("models")[0].GetProperty("supported_reasoning_levels").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("effort").GetString()!, entry => entry.GetProperty("description").GetString());
        foreach (var entry in entries)
            Assert.Equal(descriptions[entry.GetProperty("effort").GetString()!], entry.GetProperty("description").GetString());
        Assert.Equal("xhigh", actual.RootElement.GetProperty("models")[0].GetProperty("default_reasoning_level").GetString());
        Assert.Equal(["xhigh", "Vendor_Default", "medium", "low"], native);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Catalog_EmptyOrSingleLevelRemainsValid(int count)
    {
        using var document = JsonDocument.Parse(LocalModelCatalogBuilder.BuildJson(new LocalModelCatalogOptions
        {
            Slug = "model",
            DisplayName = "Model",
            ContextWindow = 8192,
            SupportedReasoningLevels = count == 0 ? [] : ["xhigh"],
            DefaultReasoningLevel = count == 0 ? null : "xhigh",
            ReverseReasoningLevelDisplayOrder = true,
        }));
        Assert.Equal(count, document.RootElement.GetProperty("models")[0].GetProperty("supported_reasoning_levels").GetArrayLength());
    }

    [Fact]
    public async Task Preference_PersistsAndUpdatesCatalogWithoutChangingNativePresetOrValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "EggLauncher.DisplayOrder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ModelProfile
            {
                Id = "model",
                Alias = "model",
                DisplayName = "Model",
                ModelRelativePath = "model.gguf",
                ContextSize = 8192,
                CompactionSafetyReserve = 2048,
                ChatTemplateRelativePath = "active.jinja",
                ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
                ReasoningResponsesVerified = true,
                ReasoningClientCompatible = true,
                DefaultThinkingEnabled = true,
                ExposeReasoningEffortInChatGpt = true,
                SupportedReasoningLevels = ["xhigh", "medium", "low"],
                DefaultReasoningLevel = "xhigh",
            };
            Assert.False(profile.ReverseReasoningLevelDisplayOrder);
            await File.WriteAllTextAsync(Path.Combine(root, "active.jinja"), "template");
            var catalog = Path.Combine(root, "catalog.json");
            var preset = Path.Combine(root, "preset.ini");
            await LocalModelConfigurationWriter.WriteAsync(profile, root, preset, catalog);
            var nativePreset = await File.ReadAllTextAsync(preset);
            var fingerprint = ReasoningValidationFingerprint.Compute(profile, root);
            var reversed = profile with { ReverseReasoningLevelDisplayOrder = true };
            var restored = JsonSerializer.Deserialize<ModelProfile>(JsonSerializer.Serialize(reversed))!;
            Assert.True(restored.ReverseReasoningLevelDisplayOrder);
            Assert.False(ModelParameterDefaults.FromProfile(profile).ApplyTo(restored).ReverseReasoningLevelDisplayOrder);
            Assert.True(ModelParameterDefaults.FromProfile(restored).ApplyTo(profile).ReverseReasoningLevelDisplayOrder);
            Assert.Equal(fingerprint, ReasoningValidationFingerprint.Compute(restored, root));
            await LocalModelConfigurationWriter.WriteAsync(restored, root, preset, catalog);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(catalog));
            Assert.Equal(["low", "medium", "xhigh"], document.RootElement.GetProperty("models")[0]
                .GetProperty("supported_reasoning_levels").EnumerateArray().Select(entry => entry.GetProperty("effort").GetString()));
            Assert.Equal(nativePreset, await File.ReadAllTextAsync(preset));
            Assert.Equal(["xhigh", "medium", "low"], restored.SupportedReasoningLevels);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
