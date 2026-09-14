using Launcher.Models.Profiles;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Tests;

public sealed class RouterPresetGeneratorTests
{
    [Fact]
    public void Generate_ProducesVersionedSingleModelPreset()
    {
        var profile = new ModelProfile
        {
            Id = "coder-q4",
            DisplayName = "Coder Q4",
            ModelRelativePath = @"models\coder-Q4_K_M.gguf",
            Alias = "coder-q4",
            ContextSize = 8192,
            GpuLayers = "all",
            FlashAttention = "on",
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            Parallel = 1,
            Jinja = true,
            BatchSize = 2048,
            MicroBatchSize = 512,
            ChatTemplateRelativePath = @"scripts\templates\coder.jinja",
        };

        var result = RouterPresetGenerator.Generate(profile, @"D:\llama.cpp", loadOnStartup: false);

        Assert.StartsWith("version = 1\n\n[coder-q4]\n", result, StringComparison.Ordinal);
        Assert.Contains("model = D:\\llama.cpp\\models\\coder-Q4_K_M.gguf\n", result, StringComparison.Ordinal);
        Assert.Contains("c = 8192\n", result, StringComparison.Ordinal);
        Assert.Contains("n-gpu-layers = all\n", result, StringComparison.Ordinal);
        Assert.Contains("sleep-idle-seconds = 300\n", result, StringComparison.Ordinal);
        Assert.Contains(
            "chat-template-file = D:\\llama.cpp\\scripts\\templates\\coder.jinja\n",
            result,
            StringComparison.Ordinal);
        Assert.Contains("load-on-startup = false\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_UsesExplicitChatTemplateOverrideForIsolatedSmokeTest()
    {
        var profile = new ModelProfile
        {
            Id = "coder-q4",
            DisplayName = "Coder Q4",
            ModelRelativePath = @"models\coder.gguf",
            Alias = "coder-q4",
            ContextSize = 8192,
            Jinja = true,
        };

        var result = RouterPresetGenerator.Generate(
            profile,
            @"D:\llama.cpp",
            loadOnStartup: false,
            @"C:\Temp\codex-compatible.jinja");

        Assert.Contains(
            "chat-template-file = C:\\Temp\\codex-compatible.jinja\n",
            result,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_PassesThroughOnlyValidatedLlamaFitArguments()
    {
        var profile = new ModelProfile
        {
            Id = "coder-fit",
            DisplayName = "Coder Fit",
            ModelRelativePath = @"models\coder.gguf",
            Alias = "coder-fit",
            ContextSize = 16384,
            ExtraArguments = new Dictionary<string, string?>
            {
                ["tensor-split"] = "3,1",
                ["override-tensor"] = "blk\\..*=CUDA0",
            },
        };

        var result = RouterPresetGenerator.Generate(profile, @"D:\llama.cpp", loadOnStartup: false);

        Assert.Contains("tensor-split = 3,1\n", result, StringComparison.Ordinal);
        Assert.Contains("override-tensor = blk\\..*=CUDA0\n", result, StringComparison.Ordinal);
    }
}
