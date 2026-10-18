using System.Text.Json.Nodes;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;

namespace Launcher.Tests;

public sealed class ModelConfigurationSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EggLauncher.ConfigurationSelection", Guid.NewGuid().ToString("N"));
    private readonly JsonModelProfileStore _store = new();

    private async Task<ModelProfile> InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "main.gguf"), "fixture");
        await _store.SaveRegistrationAsync(_root, new ModelProfile
        {
            Id = "model",
            Alias = "model",
            DisplayName = "Model",
            ModelRelativePath = "main.gguf",
            ModelType = ModelType.Dense,
            ContextSize = 16384,
            CompactionSafetyReserve = 4096,
        });
        return Assert.Single((await _store.LoadAsync(_root)).Profiles);
    }

    [Fact]
    public async Task List_TracksCopiesRenamesAndDeletes_WithoutCreatingOrParsingFiles()
    {
        var model = await InitializeAsync();
        var directory = ModelConfigurationFile.GetDirectory(model, _root);
        var initial = model.PreferredConfigurationPath!;
        var b = Path.Combine(directory, "B.JSON");
        File.Copy(initial, b);
        await File.WriteAllTextAsync(Path.Combine(directory, "damaged.json"), "not json");
        await File.WriteAllTextAsync(Path.Combine(directory, "ignored.json.bak"), "backup");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.Copy(initial, Path.Combine(directory, "nested", "nested.json"));
        Directory.CreateDirectory(Path.Combine(directory, "directory.json"));
        var files = ModelConfigurationSelection.ListFiles(model, _root);
        Assert.Equal(3, files.Count);
        Assert.Contains(files, file => file.Path == b);
        Assert.Contains(files, file => file.Name == "damaged");
        var renamed = Path.Combine(directory, "C.json");
        File.Move(b, renamed);
        File.Delete(initial);
        files = ModelConfigurationSelection.ListFiles(model, _root);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.Path == renamed);
        Assert.False(ModelConfigurationSelection.Inspect(model, _root).Exists);
        Assert.Null(ModelConfigurationSelection.Inspect(model, _root).Name);
        Assert.Equal(initial, (await _store.ReadRegistrationAsync(_root, model)).PreferredConfigurationPath);
        _ = await _store.LoadAsync(_root);
        Assert.False(File.Exists(initial));
    }

    [Fact]
    public async Task MissingDirectory_ListsEmpty_AndIsNotRecreated()
    {
        var model = await InitializeAsync();
        var directory = ModelConfigurationFile.GetDirectory(model, _root);
        Directory.Delete(directory, true);
        Assert.Empty(ModelConfigurationSelection.ListFiles(model, _root));
        Assert.False(ModelConfigurationSelection.Inspect(model, _root).Exists);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("wrong-model")]
    [InlineData("wrong-type")]
    public async Task Selection_DoesNotValidateContents_StartupStillRejects(string kind)
    {
        var model = await InitializeAsync();
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(model, _root), "B.json");
        File.Copy(model.PreferredConfigurationPath!, path);
        if (kind == "damaged") await File.WriteAllTextAsync(path, "not json");
        else
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (kind == "wrong-model") json["ModelId"] = "different-model";
            else json["Parameters"]!["ModelType"] = (int)ModelType.MoE;
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        var bytes = await File.ReadAllBytesAsync(path);
        var selected = await _store.UpdatePreferredConfigurationAsync(_root, model, path, () => true);
        Assert.Equal(path, selected.PreferredConfigurationPath);
        Assert.Equal("B", ModelConfigurationSelection.Inspect(selected, _root).Name);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.NotNull(await Record.ExceptionAsync(() => ModelStartupConfiguration.ResolveAsync(selected, _root)));
        Assert.Equal(path, (await _store.ReadRegistrationAsync(_root, model)).PreferredConfigurationPath);
    }

    [Fact]
    public async Task Selection_UpdatesOnlyPointer_PreservingNewMetadataAndAllOtherFiles()
    {
        var model = await InitializeAsync();
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(model, _root), "B.json");
        File.Copy(model.PreferredConfigurationPath!, path);
        await File.WriteAllTextAsync(Path.Combine(_root, "generated.bat"), "original generated");
        var registrationPath = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), model.Id + ".json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(registrationPath))!.AsObject();
        json["DisplayName"] = "New name";
        json["ModelType"] = (int)ModelType.MoE;
        json["FutureMetadata"] = new JsonObject { ["Note"] = "preserve me" };
        await File.WriteAllTextAsync(registrationPath, json.ToJsonString());
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
        var selected = await _store.UpdatePreferredConfigurationAsync(_root, model, path, () => true);
        Assert.Equal("New name", selected.DisplayName);
        Assert.Equal(ModelType.MoE, selected.ModelType);
        var after = JsonNode.Parse(await File.ReadAllTextAsync(registrationPath))!.AsObject();
        json.Remove("PreferredConfigurationPath");
        after.Remove("PreferredConfigurationPath");
        Assert.True(JsonNode.DeepEquals(json, after));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before.Where(pair => pair.Key != registrationPath))
            Assert.Equal(pair.Value, await File.ReadAllBytesAsync(pair.Key));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("outside")]
    [InlineData("nested")]
    [InlineData("directory")]
    [InlineData("running")]
    [InlineData("became-running")]
    [InlineData("deleted-during-selection")]
    public async Task RejectedSelection_DoesNotChangeRegistration(string reason)
    {
        var model = await InitializeAsync();
        var path = Path.Combine(ModelConfigurationFile.GetDirectory(model, _root), "B.json");
        File.Copy(model.PreferredConfigurationPath!, path);
        if (reason == "missing") File.Delete(path);
        if (reason == "outside") path = model.PreferredConfigurationPath!; // Replaced below with an actual outside file.
        if (reason is "outside" or "nested")
        {
            path = reason == "outside" ? Path.Combine(_root, "outside.json")
                : Path.Combine(ModelConfigurationFile.GetDirectory(model, _root), "nested", "B.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(model.PreferredConfigurationPath!, path);
        }
        if (reason == "directory") { File.Delete(path); Directory.CreateDirectory(path); }
        var registration = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), model.Id + ".json");
        var before = await File.ReadAllBytesAsync(registration);
        var calls = 0;
        bool CanChange()
        {
            calls++;
            if (reason == "deleted-during-selection" && calls == 2) File.Delete(path);
            return reason != "running" && !(reason == "became-running" && calls == 2);
        }
        Assert.NotNull(await Record.ExceptionAsync(() => _store.UpdatePreferredConfigurationAsync(_root, model, path, CanChange)));
        Assert.Equal(before, await File.ReadAllBytesAsync(registration));
    }

    [Fact]
    public async Task RestoredFile_IsVisibleAgain_WithoutChangingSelectionOrBytes()
    {
        var model = await InitializeAsync();
        var path = model.PreferredConfigurationPath!;
        var bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path);
        Assert.False(ModelConfigurationSelection.Inspect(model, _root).Exists);
        await File.WriteAllBytesAsync(path, bytes);
        Assert.True(ModelConfigurationSelection.Inspect(model, _root).Exists);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), ModelConfigurationSelection.Inspect(model, _root).Name);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
