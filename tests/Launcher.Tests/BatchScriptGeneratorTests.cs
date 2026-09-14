using Launcher.Models.Profiles;
using Launcher.Scripts.Batch;

namespace Launcher.Tests;

public sealed class BatchScriptGeneratorTests
{
    [Fact]
    public void Generate_DerivesRuntimeFromScriptLocationAndUsesRelativeModelPath()
    {
        var profile = CreateProfile();

        var script = BatchScriptGenerator.Generate(profile, @"D:\portable\llama.cpp");

        Assert.Contains(BatchScriptGenerator.OwnershipMarker, script, StringComparison.Ordinal);
        Assert.Contains("set \"LLAMA_ROOT=%~dp0..\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "--model \"%LLAMA_ROOT%\\models\\Coder-Q4_K_M.gguf\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\portable\llama.cpp", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--host \"127.0.0.1\"", script, StringComparison.Ordinal);
        Assert.Contains("--port 8080", script, StringComparison.Ordinal);
        Assert.Contains("--sleep-idle-seconds 300", script, StringComparison.Ordinal);
        Assert.Contains(
            "--chat-template-file \"%LLAMA_ROOT%\\scripts\\templates\\coder.jinja\"",
            script,
            StringComparison.Ordinal);
        Assert.EndsWith("exit /b %ERRORLEVEL%\r\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteOwnedAsync_ReplacesOwnedGeneratedScript()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profile = CreateProfile();
            var firstPath = await BatchScriptGenerator.WriteOwnedAsync(root, profile);
            var updated = profile with { ContextSize = 16384 };

            var secondPath = await BatchScriptGenerator.WriteOwnedAsync(root, updated);

            Assert.Equal(firstPath, secondPath);
            var content = await File.ReadAllTextAsync(secondPath);
            Assert.Contains("--ctx-size 16384", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteOwnedAsync_DoesNotOverwriteHandWrittenScript()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = BatchScriptGenerator.GetOutputPath(root, "coder");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "@echo off\r\necho user script\r\n");

            var exception = await Assert.ThrowsAsync<BatchScriptOwnershipException>(
                () => BatchScriptGenerator.WriteOwnedAsync(root, CreateProfile()));

            Assert.Contains("拒绝覆盖", exception.Message, StringComparison.Ordinal);
            Assert.Equal("@echo off\r\necho user script\r\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Generate_EscapesPercentAndRejectsQuotesInArgumentValues()
    {
        var escaped = BatchScriptGenerator.Generate(
            CreateProfile() with { Device = "GPU%1" },
            @"D:\llama.cpp");
        Assert.Contains("--device \"GPU%%1\"", escaped, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => BatchScriptGenerator.Generate(
            CreateProfile() with { Device = "GPU\"1" },
            @"D:\llama.cpp"));
    }

    private static ModelProfile CreateProfile() => new()
    {
        Id = "coder",
        DisplayName = "Coder",
        ModelRelativePath = @"models\Coder-Q4_K_M.gguf",
        Alias = "coder",
        ContextSize = 8192,
        GpuLayers = "auto",
        FlashAttention = "auto",
        CacheTypeK = "f16",
        CacheTypeV = "f16",
        Parallel = 1,
        Jinja = true,
        ChatTemplateRelativePath = @"scripts\templates\coder.jinja",
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ChatGPTLocalLauncher.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
