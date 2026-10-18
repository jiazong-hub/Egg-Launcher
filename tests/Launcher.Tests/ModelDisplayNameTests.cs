using System.Text.Json.Nodes;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ModelDisplayNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-display-name", Guid.NewGuid().ToString("N"));
    private readonly JsonModelProfileStore _store = new();
    private ModelProfile Model => new()
    {
        Id = "stable-id",
        Alias = "stable-alias",
        DisplayName = "Original name",
        ModelRelativePath = "models/real-model.gguf",
        ModelType = ModelType.Dense,
        ContextSize = 16_384,
        CompactionSafetyReserve = 4096,
        ResourceCatalogInitialized = true,
    };
    private string RegistrationPath => Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), Model.Id + ".json");

    private async Task<ModelProfile> Initialize()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        await File.WriteAllTextAsync(Path.Combine(_root, Model.ModelRelativePath), "model fixture");
        await File.WriteAllTextAsync(Path.Combine(_root, "llama-server.exe"), "runtime fixture");
        await _store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        await ModelResourceCatalog.EnsureMigratedAsync(model, _root);
        await ModelConfigurationFile.WriteAsync(Path.Combine(ModelConfigurationFile.GetDirectory(model, _root), "B.json"), model with { GpuLayers = "4" }, _root);
        await File.WriteAllTextAsync(Path.Combine(_root, "scripts", "stable-id.bat"), "existing generated file");
        return model;
    }

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_root, path), ModelConfigurationFile.Digest, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Rename_ChangesOnlyDisplayName_AndPreservesBindingsAndAllOtherFiles()
    {
        var model = await Initialize();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(RegistrationPath))!.AsObject();
        json["FutureField"] = new JsonObject { ["note"] = "keep" };
        await File.WriteAllTextAsync(RegistrationPath, json.ToJsonString());
        var before = Snapshot();
        var renamed = await _store.UpdateDisplayNameAsync(_root, model, "  任意卡片名称 / 用户自选  ", () => true);
        Assert.Equal("任意卡片名称 / 用户自选", renamed.DisplayName);
        Assert.Equal(model.Id, renamed.Id);
        Assert.Equal(model.Alias, renamed.Alias);
        Assert.Equal(model.ModelRelativePath, renamed.ModelRelativePath);
        Assert.Equal(model.PreferredConfigurationPath, renamed.PreferredConfigurationPath);
        Assert.Equal(ModelConfigurationFile.GetDirectory(model, _root), ModelConfigurationFile.GetDirectory(renamed, _root));
        var after = JsonNode.Parse(await File.ReadAllTextAsync(RegistrationPath))!.AsObject();
        json.Remove("DisplayName"); after.Remove("DisplayName");
        Assert.True(JsonNode.DeepEquals(json, after));
        var snapshot = Snapshot();
        Assert.Equal(before.Keys.Order(), snapshot.Keys.Order());
        foreach (var pair in before.Where(pair => pair.Key != Path.GetRelativePath(_root, RegistrationPath))) Assert.Equal(pair.Value, snapshot[pair.Key]);
        var loaded = Assert.Single((await _store.LoadAsync(_root)).Profiles);
        Assert.Equal(renamed.DisplayName, loaded.DisplayName);
        Assert.Equal(model.Id, (await ModelStartupConfiguration.ResolveAsync(loaded, _root)).Id);
        foreach (var file in Directory.GetFiles(ModelConfigurationFile.GetDirectory(model, _root), "*.json"))
            Assert.Equal(renamed.DisplayName, (await ModelConfigurationFile.ReadAsync(file, renamed, _root)).DisplayName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("damaged")]
    [InlineData("type-mismatch")]
    public async Task Rename_DoesNotRequireAUsableConfiguration_AndNeverRepairsIt(string state)
    {
        var model = await Initialize();
        if (state == "missing") File.Delete(model.PreferredConfigurationPath!);
        else if (state == "damaged") await File.WriteAllTextAsync(model.PreferredConfigurationPath!, "broken json");
        else model = await _store.UpdateModelTypeAsync(_root, model, ModelType.MoE, () => true);
        var before = Snapshot();
        var renamed = await _store.UpdateDisplayNameAsync(_root, model, "New", () => true);
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var pair in before.Where(pair => pair.Key != Path.GetRelativePath(_root, RegistrationPath))) Assert.Equal(pair.Value, after[pair.Key]);
        Assert.Equal(model.PreferredConfigurationPath, renamed.PreferredConfigurationPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("bad\0name")]
    public async Task InvalidName_DoesNotWrite(string name)
    {
        var model = await Initialize();
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.UpdateDisplayNameAsync(_root, model, name, () => true));
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceGuard_DoesNotWrite(bool changesDuringSave)
    {
        var model = await Initialize();
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.UpdateDisplayNameAsync(_root, model, "New", () => changesDuringSave && ++calls == 1));
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
    }

    [Fact]
    public async Task ExternalChange_DoesNotGetOverwritten()
    {
        var model = await Initialize();
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => _store.UpdateDisplayNameAsync(_root, model, "New", () =>
        {
            if (++calls == 2) File.AppendAllText(RegistrationPath, " ");
            return true;
        }));
        Assert.EndsWith(" ", await File.ReadAllTextAsync(RegistrationPath));
        Assert.Equal(model.DisplayName, (await _store.ReadRegistrationAsync(_root, model)).DisplayName);
    }

    [Fact]
    public async Task StaleName_RejectsSave_WhileLatestOtherMetadataIsRetained()
    {
        var model = await Initialize();
        var otherType = await _store.UpdateModelTypeAsync(_root, model, ModelType.MoE, () => true);
        var renamed = await _store.UpdateDisplayNameAsync(_root, model, "New", () => true);
        Assert.Equal(otherType.ModelType, renamed.ModelType);
        var before = await File.ReadAllBytesAsync(RegistrationPath);
        await Assert.ThrowsAsync<IOException>(() => _store.UpdateDisplayNameAsync(_root, model, "Stale", () => true));
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
        await _store.UpdateDisplayNameAsync(_root, renamed, " New ", () => true);
        Assert.Equal(before, await File.ReadAllBytesAsync(RegistrationPath));
    }

    [Fact]
    public async Task SameDisplayNameAcrossModels_DoesNotMergeIdentity()
    {
        var first = await Initialize();
        var second = Model with { Id = "another-id", Alias = "another-alias" };
        await _store.SaveRegistrationAsync(_root, second);
        second = (await _store.LoadAsync(_root)).Profiles.Single(p => p.Id == second.Id);
        var renamed = await _store.UpdateDisplayNameAsync(_root, second, first.DisplayName, () => true);
        Assert.NotEqual(first.Id, renamed.Id);
        Assert.NotEqual(first.Alias, renamed.Alias);
        Assert.NotEqual(first.PreferredConfigurationPath, renamed.PreferredConfigurationPath);
        Assert.Equal(2, (await _store.LoadAsync(_root)).Profiles.Count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
