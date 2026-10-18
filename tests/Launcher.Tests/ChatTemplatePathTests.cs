using System.Text.Json.Nodes;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ChatTemplatePathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-template-paths", Guid.NewGuid().ToString("N"));
    private ModelProfile Model => new() { Id = "model", Alias = "model", DisplayName = "Model", ModelRelativePath = "models/main.gguf", ModelType = ModelType.Dense, ContextSize = 16384, CompactionSafetyReserve = 4096 };

    public ChatTemplatePathTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(ChatTemplateFile.GetDirectory(_root));
        File.WriteAllText(Path.Combine(_root, "models", "main.gguf"), "main model");
        File.WriteAllText(Path.Combine(_root, "llama-server.exe"), "runtime");
    }

    [Theory]
    [InlineData("outside.jinja")]
    [InlineData("scripts/outside.jinja")]
    [InlineData("models/outside.jinja")]
    [InlineData("scripts/templates-other/outside.jinja")]
    [InlineData("scripts/templates/nested/outside.jinja")]
    [InlineData("scripts/templates/../outside.jinja")]
    [InlineData("../outside.jinja")]
    [InlineData("scripts/templates/wrong.json")]
    [InlineData("scripts/templates/.jinja")]
    public async Task ExternalTemplateOutsideDedicatedDirectory_IsRejectedByRegistrationAndRuntime(string relative)
    {
        var profile = Model with { ChatTemplateRelativePath = relative };
        await Assert.ThrowsAsync<InvalidDataException>(() => ChatTemplateValidationCache.WriteAsync(profile, _root, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelResourceCatalog.RegisterAsync(profile, _root, ModelResourceKind.Template));
        Assert.False(ModelResourceCatalog.IsCurrent(new(ModelResourceKind.Template, profile, "old proof"), _root));
        Assert.Contains(ModelProfileValidator.Validate(profile, _root), e => e.Contains("Chat Template", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => BatchScriptGenerator.Generate(profile, _root));
        Assert.Throws<InvalidDataException>(() => RouterPresetGenerator.Generate(profile, _root, false));
    }

    [Fact]
    public async Task DirectJinjaFile_AcceptsNormalizedPathAndCaseInsensitiveExtension()
    {
        var path = Path.Combine(ChatTemplateFile.GetDirectory(_root), ".", "第三方模板.JINJA");
        await File.WriteAllTextAsync(path, "{{ messages }}");
        var profile = Model with { ChatTemplateRelativePath = Path.GetRelativePath(_root, path) };
        Assert.Equal(Path.GetFullPath(path), ChatTemplateFile.ValidatePath(path, _root));
        Assert.Empty(ModelProfileValidator.Validate(profile, _root));
        await ChatTemplateValidationCache.WriteAsync(profile, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(profile, _root, ModelResourceKind.Template);
        Assert.True(ModelResourceCatalog.IsCurrent(Assert.Single(ModelResourceCatalog.Read(profile, _root).Records), _root));
        Assert.Contains("--chat-template-file", BatchScriptGenerator.Generate(profile, _root));
        Assert.Contains("chat-template-file =", RouterPresetGenerator.Generate(profile, _root, false));
    }

    [Fact]
    public void NativeOverrideCannotBypassTemplateDirectory()
    {
        Assert.Throws<InvalidDataException>(() => RouterPresetGenerator.Generate(Model, _root, false, Path.Combine(_root, "outside.jinja")));
        Assert.Throws<InvalidDataException>(() => ChatTemplateFile.ValidatePath("relative.jinja", _root));
        Assert.Throws<InvalidDataException>(() => ChatTemplateFile.ResolveRelative(Path.Combine(ChatTemplateFile.GetDirectory(_root), "absolute.jinja"), _root));
    }

    [Fact]
    public async Task OldOutsideReference_RemainsEditableButCannotStart_UntilUserSelectsCheckedTemplateInsideDirectory()
    {
        var store = new JsonModelProfileStore();
        await store.SaveRegistrationAsync(_root, Model);
        var model = Assert.Single((await store.LoadAsync(_root)).Profiles);
        var outside = Path.Combine(_root, "scripts", "old.jinja");
        await File.WriteAllTextAsync(outside, "{{ messages }}");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(model.PreferredConfigurationPath!))!;
        document["Parameters"]!["ChatTemplateRelativePath"] = "scripts/old.jinja";
        await File.WriteAllTextAsync(model.PreferredConfigurationPath!, document.ToJsonString());
        // Simulate a prior version's registration pointing at the same old template.
        await store.SaveRegistrationAsync(_root, model with { ChatTemplateRelativePath = "scripts/old.jinja" });
        var editable = Assert.Single((await store.LoadAsync(_root)).Profiles);
        Assert.Equal("scripts/old.jinja", editable.ChatTemplateRelativePath);
        Assert.Null(editable.ConfigurationError);
        var reasoning = ReasoningValidationState.Check(editable, _root);
        Assert.Equal(ReasoningValidationStateKind.Unreadable, reasoning.State);
        Assert.Contains("templates", reasoning.Reason);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(editable, _root));
        Assert.Equal("{{ messages }}", await File.ReadAllTextAsync(outside));
        var inside = Path.Combine(ChatTemplateFile.GetDirectory(_root), "old.jinja");
        File.Copy(outside, inside);
        var corrected = editable with { ChatTemplateRelativePath = Path.GetRelativePath(_root, inside) };
        await ChatTemplateValidationCache.WriteAsync(corrected, _root, CancellationToken.None);
        await ModelResourceCatalog.RegisterAsync(corrected, _root, ModelResourceKind.Template);
        await ModelConfigurationFile.WriteAsync(model.PreferredConfigurationPath!, corrected, _root);
        Assert.Equal(corrected.ChatTemplateRelativePath, (await ModelStartupConfiguration.ResolveAsync(corrected, _root)).ChatTemplateRelativePath);
        Assert.True(File.Exists(outside));
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task LegacyRegistrationWithOutsideTemplate_MigratesWithoutDroppingModelOrReadingTemplate()
    {
        var legacy = ModelProfileFactory.SaveCurrentParametersAsDefault(Model with { ChatTemplateRelativePath = "scripts/old.jinja" });
        var registration = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(_root), "model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        await File.WriteAllTextAsync(registration, System.Text.Json.JsonSerializer.Serialize(legacy));
        var model = Assert.Single((await new JsonModelProfileStore().LoadAsync(_root)).Profiles);
        Assert.True(model.ConfigurationInitialized);
        Assert.Null(model.ConfigurationError);
        Assert.Equal("scripts/old.jinja", model.ChatTemplateRelativePath);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(legacy.DefaultParameters), System.Text.Json.JsonSerializer.Serialize(model.DefaultParameters));
        await ModelResourceCatalog.EnsureMigratedAsync(model, _root);
        Assert.Empty(ModelResourceCatalog.Read(model, _root).Records);
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelStartupConfiguration.ResolveAsync(model, _root));
        Assert.False(File.Exists(Path.Combine(_root, "scripts", "old.jinja")));
    }
}
