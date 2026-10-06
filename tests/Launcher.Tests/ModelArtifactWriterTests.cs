using System.Text.Json;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Tests;

public sealed class ModelArtifactWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-artifact-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonModelProfileStore _store = new();
    private string Preset => Path.Combine(_root, "state", "models.ini");
    private string Catalog => Path.Combine(_root, "state", "catalog.json");

    public ModelArtifactWriterTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllBytes(Path.Combine(_root, "models", "model.gguf"), [1]);
    }

    [Fact]
    public async Task SaveAsync_UpdatesAllActiveArtifactsAndPreservesDedicatedDefaults()
    {
        var writer = new ModelArtifactWriter(_store, new ModelArtifactTransaction());
        var original = CreateProfile() with
        {
            CpuMoeLayers = 48,
            DefaultParameters = new ModelParameterDefaults { ContextSize = 8192, CpuMoeLayers = 48 },
        };
        await writer.SaveAsync(original, _root, Preset, Catalog, true);
        await writer.SaveAsync(original with { CpuMoeLayers = 46, ContextSize = 262144, CompactionSafetyReserve = 32768 }, _root, Preset, Catalog, true);

        // A new store simulates reopening the launcher: saved values, not its former UI snapshot.
        var saved = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        Assert.Equal(46, saved.CpuMoeLayers);
        Assert.Equal(48, saved.DefaultParameters!.CpuMoeLayers);
        Assert.Equal(262144, saved.ContextSize);
        Assert.Contains("--n-cpu-moe 46", await File.ReadAllTextAsync(BatchScriptGenerator.GetOutputPath(_root, saved.Id)));
        Assert.Equal(RouterPresetGenerator.Generate(saved, _root, false), await File.ReadAllTextAsync(Preset));
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Catalog));
        Assert.Equal(262144, catalog.RootElement.GetProperty("models")[0].GetProperty("context_window").GetInt32());
    }

    [Fact]
    public async Task SaveAsync_InactiveModelDoesNotReplaceActiveRouterOrCatalog()
    {
        var writer = new ModelArtifactWriter(_store, new ModelArtifactTransaction());
        await writer.SaveAsync(CreateProfile(), _root, Preset, Catalog, true);
        var preset = await File.ReadAllTextAsync(Preset);
        var catalog = await File.ReadAllTextAsync(Catalog);
        var other = CreateProfile() with { Id = "other", Alias = "other", CpuMoeLayers = 46 };
        await writer.SaveAsync(other, _root, Preset, Catalog, false);
        Assert.Equal(preset, await File.ReadAllTextAsync(Preset));
        Assert.Equal(catalog, await File.ReadAllTextAsync(Catalog));
        Assert.Contains("--n-cpu-moe 46", await File.ReadAllTextAsync(BatchScriptGenerator.GetOutputPath(_root, other.Id)));
    }

    [Fact]
    public async Task SaveAsync_WhenPresetWriteFails_RollsBackProfileBatchAndCatalog()
    {
        var writer = new ModelArtifactWriter(_store, new ModelArtifactTransaction());
        var profile = CreateProfile();
        await writer.SaveAsync(profile, _root, Preset, Catalog, true);
        var profilePath = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), profile.Id + ".json");
        var batchPath = BatchScriptGenerator.GetOutputPath(_root, profile.Id);
        var originals = new Dictionary<string, string>();
        foreach (var path in new[] { profilePath, profilePath + ".bak", batchPath, Preset, Catalog })
            originals[path] = await File.ReadAllTextAsync(path);

        // Permit reads for transaction snapshots, deny overwrite of the last artifact.
        using (var lockedPreset = new FileStream(Preset, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => writer.SaveAsync(
                profile with { CpuMoeLayers = 46, ContextSize = 16384, CompactionSafetyReserve = 4096 }, _root, Preset, Catalog, true));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        }
        foreach (var pair in originals)
            Assert.Equal(pair.Value, await File.ReadAllTextAsync(pair.Key));
    }

    private static ModelProfile CreateProfile() => new()
    {
        Id = "moe",
        Alias = "moe",
        DisplayName = "MoE",
        ModelRelativePath = @"models\model.gguf",
        ContextSize = 8192,
        ModelType = ModelType.MoE,
        MoeExpertPlacement = MoeExpertPlacement.CpuFirstLayers,
        CpuMoeLayers = 48,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
