using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Core.Diagnostics;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Runtime.Detection;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Tests;

public sealed class NgramTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-ngram", Guid.NewGuid().ToString("N"));
    private ModelProfile Model => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Fixture",
        ModelRelativePath = "models/main.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 16384,
        CompactionSafetyReserve = 4096,
        MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate,
    };

    public NgramTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllText(Path.Combine(_root, "models/main.gguf"), "fixture");
    }

    public static IEnumerable<object[]> Combinations() => Enumerable.Range(0, 64).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task All64Combinations_RoundTripAndGenerateExactlyOneMatchingStrategyList(int mask)
    {
        var types = NgramOptions.Types.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        var profile = Model with { MtpEnabled = (mask & 32) != 0, Ngram = new() { Enabled = types.Length > 0, Types = types } };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(profile, _root), "A.json");
        await ModelConfigurationFile.WriteAsync(path, profile, _root);
        var read = await ModelConfigurationFile.ReadAsync(path, Model, _root);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            Assert.Equal(types.Length > 0 ? 2 : 1, document.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal(JsonSerializer.Serialize(profile.Ngram), JsonSerializer.Serialize(read.Ngram));
        Assert.Equal(profile.MtpEnabled, read.MtpEnabled);
        var expected = (profile.MtpEnabled ? new[] { "draft-mtp" } : []).Concat(types).ToArray();
        var bat = BatchScriptGenerator.Generate(read, _root);
        var preset = RouterPresetGenerator.Generate(read, _root, false);
        Assert.Equal(expected.Length > 0 ? 1 : 0, Regex.Matches(bat, "--spec-type ").Count);
        Assert.Equal(expected.Length > 0 ? 1 : 0, Regex.Matches(preset, "(?m)^spec-type = ").Count);
        if (expected.Length > 0)
        {
            Assert.Contains("--spec-type \"" + string.Join(',', expected) + "\"", bat);
            Assert.Contains("spec-type = " + string.Join(',', expected) + "\n", preset);
        }
    }

    [Fact]
    public async Task EveryParameter_ExplicitAndNullAndDisabledSettingsSurviveDiskAndDefaults()
    {
        var settings = new NgramSettings
        {
            Enabled = true,
            Types = NgramOptions.Types,
            Parameters = NgramOptions.Parameters.ToDictionary(p => p.Key, p => (int?)(p.Minimum + 2)),
        };
        foreach (var enabled in new[] { true, false })
        {
            var candidate = Model with { Ngram = settings with { Enabled = enabled } };
            var path = Path.Combine(ModelConfigurationFile.GetDirectory(candidate, _root), "parameters.json");
            await ModelConfigurationFile.WriteAsync(path, candidate, _root);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
                Assert.Equal(2, document.RootElement.GetProperty("FormatVersion").GetInt32());
            var read = await ModelConfigurationFile.ReadAsync(path, Model, _root);
            Assert.Equal(JsonSerializer.Serialize(candidate.Ngram), JsonSerializer.Serialize(read.Ngram));
            Assert.Equal(JsonSerializer.Serialize(candidate.Ngram), JsonSerializer.Serialize(ModelParameterDefaults.FromProfile(candidate).ApplyTo(Model).Ngram));
            var bat = BatchScriptGenerator.Generate(read, _root);
            var preset = RouterPresetGenerator.Generate(read, _root, false);
            foreach (var pair in candidate.Ngram.Parameters)
            {
                Assert.Equal(enabled, bat.Contains("--" + pair.Key + " \"" + pair.Value + "\""));
                Assert.Equal(enabled, preset.Contains(pair.Key + " = " + pair.Value + "\n"));
            }
        }
        var defaults = Model with { Ngram = new() { Enabled = true, Types = NgramOptions.Types } };
        var generated = RouterPresetGenerator.Generate(defaults, _root, false);
        Assert.All(NgramOptions.Parameters, p => Assert.DoesNotContain(p.Key + " =", generated));
        Assert.All(JsonSerializer.SerializeToElement(defaults.Ngram).GetProperty("Parameters").EnumerateObject(), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
    }

    [Fact]
    public void OnlySelectedStrategiesEmitTheirOwnParameters()
    {
        foreach (var type in NgramOptions.Types)
        {
            var settings = new NgramSettings
            {
                Enabled = true,
                Types = [type],
                Parameters = NgramOptions.Parameters.ToDictionary(p => p.Key, p => (int?)p.Minimum),
            };
            var preset = RouterPresetGenerator.Generate(Model with { Ngram = settings }, _root, false);
            foreach (var parameter in NgramOptions.Parameters)
                Assert.Equal(parameter.Type == type, preset.Contains(parameter.Key + " ="));
        }
    }

    [Fact]
    public async Task OldJsonDefaultsToDisabled_UnknownFieldsAndInvalidChoicesAreRejectedWithoutRewriting()
    {
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "old.json");
        await ModelConfigurationFile.WriteAsync(path, Model, _root);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Parameters"]!.AsObject().Remove("Ngram");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var read = await ModelConfigurationFile.ReadAsync(path, Model, _root);
        Assert.False(read.Ngram.Enabled);
        Assert.Empty(read.Ngram.Types);
        foreach (var bad in new[]
        {
            "null", "{\"Enabled\":true,\"Types\":[]}",
            "{\"Enabled\":true,\"Types\":[\"typo\"]}",
            "{\"Types\":[\"ngram-mod\",\"ngram-mod\"]}",
            "{\"Enabled\":false,\"UnexpectedValue\":123}",
            "{\"Parameters\":{\"spec-ngram-mod-n-max\":\"64\"}}",
        })
        {
            json["Parameters"]!["Ngram"] = JsonNode.Parse(bad);
            var text = json.ToJsonString();
            await File.WriteAllTextAsync(path, text);
            await Assert.ThrowsAnyAsync<Exception>(() => ModelConfigurationFile.ReadAsync(path, Model, _root));
            Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public void BoundsAndUnknownKeys_AreCheckedEvenWhileDisabled()
    {
        foreach (var parameter in NgramOptions.Parameters)
        {
            NgramSettings Settings(int value) => new() { Parameters = new Dictionary<string, int?> { [parameter.Key] = value } };
            Assert.Empty(NgramOptions.Validate(Settings(parameter.Minimum), _root));
            Assert.Empty(NgramOptions.Validate(Settings(parameter.Maximum), _root));
            Assert.NotEmpty(NgramOptions.Validate(Settings(parameter.Minimum - 1), _root));
            if (parameter.Maximum < int.MaxValue) Assert.NotEmpty(NgramOptions.Validate(Settings(parameter.Maximum + 1), _root));
        }
        Assert.NotEmpty(NgramOptions.Validate(new() { Parameters = new Dictionary<string, int?> { ["typo"] = null } }, _root));
        Assert.NotEmpty(NgramOptions.Validate(new() { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-min"] = 50, ["spec-ngram-mod-n-max"] = 49 } }, _root));
        foreach (var key in new[] { "spec-default", "spec-ngram-mod-n-match", "spec-ngram-size-n", "lookup-cache-static", "lcd", "SPEC-DEFAULT", "spec-synth-len" })
            Assert.NotEmpty(ModelProfileValidator.Validate(Model with { ExtraArguments = new Dictionary<string, string?> { [key] = "1" } }, _root));
    }

    [Fact]
    public void RuntimeEvidence_RequiresActualTypeAndOptionsAndAccountsForImplicitDefaults()
    {
        const string help = "--spec-type [none|draft-mtp|ngram-mod|ngram-map-k4v]\n   comma-separated types (default: none)\n--spec-ngram-mod-n-min N minimum (default: 48)\n--spec-ngram-mod-n-max N maximum (default: 64)\n--spec-default enables ngram-cache\n";
        var caps = LlamaRuntimeOptionDetector.ParseHelp(help);
        Assert.Contains("spec-type:ngram-mod", caps);
        Assert.Contains("spec-type:draft-mtp", caps);
        Assert.DoesNotContain("spec-type:ngram-map-k", caps);
        Assert.DoesNotContain("spec-type:ngram-cache", caps);
        var settings = new NgramSettings { Enabled = true, Types = ["ngram-mod"] };
        Assert.Empty(NgramOptions.ValidateRuntime(settings, true, caps));
        Assert.NotEmpty(NgramOptions.ValidateRuntime(settings, false, new HashSet<string> { "spec-type" }));
        Assert.NotEmpty(NgramOptions.ValidateRuntime(settings with { Types = ["ngram-cache"] }, false, caps));
        Assert.NotEmpty(NgramOptions.ValidateRuntime(settings with { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-match"] = 24 } }, false, caps));
        Assert.NotEmpty(NgramOptions.ValidateRuntime(settings with { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-max"] = 8 } }, false, caps));
        Assert.Empty(NgramOptions.ValidateRuntime(settings with { Enabled = false }, true, null));
        var noDefaults = LlamaRuntimeOptionDetector.ParseHelp("--spec-type [ngram-mod]\n--spec-ngram-mod-n-min N\n--spec-ngram-mod-n-max N\n");
        Assert.NotEmpty(NgramOptions.ValidateRuntime(settings with { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-max"] = 64 } }, false, noDefaults));
        Assert.Empty(NgramOptions.ValidateRuntime(settings with { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-min"] = 48, ["spec-ngram-mod-n-max"] = 64 } }, false, noDefaults));
        var wrapped = LlamaRuntimeOptionDetector.ParseHelp("--spec-type [none|draft-mtp|ngram-mod]\n--spec-ngram-mod-n-min N\n    minimum draft tokens (default: 48)\n--spec-ngram-mod-n-max N\n    maximum draft tokens (default: 64)\n");
        Assert.Contains("option-default:spec-ngram-mod-n-min:48", wrapped);
        Assert.Contains("option-default:spec-ngram-mod-n-max:64", wrapped);
        Assert.NotEmpty(NgramOptions.ValidateRuntime(settings with { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-max"] = 8 } }, false, wrapped));
    }

    [Fact]
    public async Task InvalidSaveAndUnsupportedStartup_PreserveDocumentAndDoNotGenerateArtifacts()
    {
        var profile = Model with { Ngram = new() { Enabled = true, Types = ["ngram-mod"] } };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "selected.json");
        await ModelConfigurationFile.WriteAsync(path, profile, _root);
        var before = await File.ReadAllBytesAsync(path);
        var bad = profile with { Ngram = profile.Ngram with { Parameters = new Dictionary<string, int?> { ["spec-ngram-mod-n-match"] = -1 } } };
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelConfigurationFile.WriteAsync(path, bad, _root));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        // This fixture has no runtime executable: confirmation must fail without disabling n-gram.
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(profile with { ConfigurationInitialized = true, PreferredConfigurationPath = path }, _root));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "*.bat", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_root, "*.ini", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeletedCache_DoesNotHideModelOrPreventMetadataChanges_AndNeverRewritesConfiguration()
    {
        var cache = Path.Combine(_root, "cache.bin");
        await File.WriteAllBytesAsync(cache, [1, 2, 3, 4]);
        var store = new JsonModelProfileStore();
        await store.SaveAsync(_root, Model with { Ngram = new() { Enabled = true, Types = ["ngram-cache"], StaticCachePath = cache } });
        var model = Assert.Single((await store.LoadAsync(_root)).Profiles);
        var original = await File.ReadAllTextAsync(model.PreferredConfigurationPath!);
        File.Delete(cache);
        var loaded = Assert.Single((await store.LoadAsync(_root)).Profiles);
        Assert.True(loaded.Ngram.Enabled);
        Assert.Equal(cache, loaded.Ngram.StaticCachePath);
        await store.UpdateModelTypeAsync(_root, loaded, ModelType.MoE, () => true);
        Assert.Equal(original, await File.ReadAllTextAsync(model.PreferredConfigurationPath!));
    }

    [Fact]
    public async Task MissingCache_CanBeLoadedForRepairButCannotBeSavedOrStartedWhenEnabled()
    {
        var cache = Path.Combine(_root, "模型缓存 100%.bin");
        await File.WriteAllBytesAsync(cache, [1, 2, 3, 4]);
        var candidate = Model with { Ngram = new() { Enabled = true, Types = ["ngram-cache"], StaticCachePath = cache, DynamicCachePath = cache } };
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(Model, _root), "cache.json");
        await ModelConfigurationFile.WriteAsync(path, candidate, _root);
        var preset = RouterPresetGenerator.Generate(candidate, _root, false);
        Assert.Contains("lookup-cache-static = " + cache, preset);
        var bat = BatchScriptGenerator.Generate(candidate, _root);
        Assert.Contains("模型缓存 100%%.bin", bat);
        File.Delete(cache);
        var read = await ModelConfigurationFile.ReadAsync(path, Model, _root);
        Assert.Equal(cache, read.Ngram.StaticCachePath);
        Assert.True(read.Ngram.Enabled);
        await ModelConfigurationFile.WriteAsync(path, read, _root);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(read with { ConfigurationInitialized = true, PreferredConfigurationPath = path }, _root));
        await ModelConfigurationFile.WriteAsync(path, read with { Ngram = read.Ngram with { Enabled = false } }, _root);
        Assert.Equal(cache, (await ModelConfigurationFile.ReadAsync(path, Model, _root)).Ngram.StaticCachePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticDetails_AreWrittenOnlyInDetailedMode(bool detailed)
    {
        var entries = new List<DiagnosticEvent>();
        var journal = new DiagnosticJournal(e => { entries.Add(e); return Task.CompletedTask; });
        journal.SetDetailed(detailed);
        await journal.RunAsync("startup_configuration", null, operation =>
        {
            operation.Set("ngramEnabled", true);
            operation.Set("ngramTypes", "ngram-mod");
            operation.SetDetail("ngramParameters", new Dictionary<string, int?> { ["spec-ngram-mod-n-match"] = 24 });
            return Task.FromResult(true);
        });
        var json = JsonSerializer.Serialize(entries.Last());
        Assert.Contains("ngramEnabled", json);
        Assert.Equal(detailed, json.Contains("spec-ngram-mod-n-match"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
