using Launcher.Models.Profiles;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ChatTemplateValidationCacheTests
{
    [Fact]
    public async Task ValidationExpiresWhenTemplateRuntimeOrModelChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemplateCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "templates"));
        var model = Path.Combine(root, "models", "model.gguf");
        var runtime = Path.Combine(root, "llama-server.exe");
        var template = Path.Combine(root, "scripts", "templates", "template.jinja");
        var profile = new ModelProfile { Id = "model", Alias = "model", DisplayName = "model", ModelRelativePath = "models/model.gguf", ChatTemplateRelativePath = "scripts/templates/template.jinja", Jinja = true };
        try
        {
            await File.WriteAllTextAsync(model, "model");
            await File.WriteAllTextAsync(runtime, "runtime");
            await File.WriteAllTextAsync(template, "template");
            await ChatTemplateValidationCache.WriteAsync(profile, root, CancellationToken.None);
            Assert.True(ChatTemplateValidationCache.IsCurrent(profile, root));
            await File.WriteAllTextAsync(template, "changed");
            Assert.False(ChatTemplateValidationCache.IsCurrent(profile, root));
            await ChatTemplateValidationCache.WriteAsync(profile, root, CancellationToken.None);
            await File.AppendAllTextAsync(runtime, "updated");
            Assert.False(ChatTemplateValidationCache.IsCurrent(profile, root));
            await ChatTemplateValidationCache.WriteAsync(profile, root, CancellationToken.None);
            await File.AppendAllTextAsync(model, "updated");
            Assert.False(ChatTemplateValidationCache.IsCurrent(profile, root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidCacheIsNotTreatedAsVerified()
    {
        var root = Path.Combine(Path.GetTempPath(), "TemplateCacheTests", Guid.NewGuid().ToString("N"));
        var profile = new ModelProfile { Id = "model", Alias = "model", DisplayName = "model", ModelRelativePath = "models/model.gguf" };
        var path = ChatTemplateValidationCache.PathFor(profile, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "not json");
            Assert.False(ChatTemplateValidationCache.IsCurrent(profile, root));
        }
        finally { Directory.Delete(root, true); }
    }
}
