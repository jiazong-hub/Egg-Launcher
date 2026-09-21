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

    [Fact]
    public void Generate_EmitsTypedMoePlacementOnlyForMoeModel()
    {
        var profile = new ModelProfile
        {
            Id = "moe",
            DisplayName = "MoE",
            ModelRelativePath = @"models\moe.gguf",
            Alias = "moe",
            ContextSize = 16384,
            ModelType = ModelType.MoE,
            MoeExpertPlacement = MoeExpertPlacement.CpuFirstLayers,
            CpuMoeLayers = 12,
        };

        var result = RouterPresetGenerator.Generate(profile, @"D:\llama.cpp", loadOnStartup: false);

        Assert.Contains("n-cpu-moe = 12\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\ncpu-moe =", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EmitsEmbeddedMtpWithoutDraftModel()
    {
        var profile = new ModelProfile
        {
            Id = "mtp-embedded",
            DisplayName = "MTP Embedded",
            ModelRelativePath = @"models\mtp.gguf",
            Alias = "mtp-embedded",
            ContextSize = 16384,
            MtpEnabled = true,
            MtpSource = MtpSourceKind.Embedded,
            MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate,
            MtpDraftMaxTokens = 4,
            MtpDraftMinimumProbability = 0.25,
        };

        var result = RouterPresetGenerator.Generate(profile, @"D:\llama.cpp", loadOnStartup: false);

        Assert.Contains("spec-type = draft-mtp\n", result, StringComparison.Ordinal);
        Assert.Contains("spec-draft-n-max = 4\n", result, StringComparison.Ordinal);
        Assert.Contains("spec-draft-p-min = 0.25\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("spec-draft-model", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EmitsExternalMtpCompanionAndResourceSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-mtp-preset-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var models = Directory.CreateDirectory(Path.Combine(root, "models"));
            File.WriteAllBytes(Path.Combine(models.FullName, "mtp-model.gguf"), [1]);
            var profile = new ModelProfile
            {
                Id = "mtp-external",
                DisplayName = "MTP External",
                ModelRelativePath = @"models\model.gguf",
                Alias = "mtp-external",
                ContextSize = 16384,
                MtpEnabled = true,
                MtpSource = MtpSourceKind.External,
                MtpCapabilityStatus = MtpCapabilityStatus.Verified,
                MtpDraftModelRelativePath = @"models\mtp-model.gguf",
                MtpValidationSignature = "verified",
                MtpValidatedAtUtc = DateTimeOffset.UtcNow,
                MtpDraftGpuLayers = "12",
                MtpBackendSampling = false,
            };

            var result = RouterPresetGenerator.Generate(profile, root, loadOnStartup: false);

            Assert.Contains("spec-type = draft-mtp\n", result, StringComparison.Ordinal);
            Assert.Contains($"spec-draft-model = {Path.Combine(root, "models", "mtp-model.gguf")}\n", result, StringComparison.Ordinal);
            Assert.Contains("spec-draft-ngl = 12\n", result, StringComparison.Ordinal);
            Assert.Contains("no-spec-draft-backend-sampling = true\n", result, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecreateForModelType_DropsPreviousRuntimeConfiguration()
    {
        var profile = new ModelProfile
        {
            Id = "dense",
            DisplayName = "Dense",
            ModelRelativePath = @"models\dense.gguf",
            Alias = "dense",
            ContextSize = 65536,
            GpuLayers = "all",
            ModelType = ModelType.Dense,
            Device = "CUDA0",
            ExtraArguments = new Dictionary<string, string?> { ["threads"] = "12" },
            DefaultParameters = new ModelParameterDefaults { ContextSize = 65536 },
        };

        var recreated = ModelProfileFactory.RecreateForModelType(profile, ModelType.MoE);

        Assert.Equal(ModelType.MoE, recreated.ModelType);
        Assert.Equal(ModelTypeSource.UserSelected, recreated.ModelTypeSource);
        Assert.Equal("auto", recreated.GpuLayers);
        Assert.Null(recreated.Device);
        Assert.Empty(recreated.ExtraArguments);
        Assert.Null(recreated.DefaultParameters);
    }

    [Fact]
    public void Generate_WhenVisionDisabled_ExplicitlyDisablesProjectorForThisModel()
    {
        var result = RouterPresetGenerator.Generate(
            new ModelProfile
            {
                Id = "text-only",
                DisplayName = "Text only",
                ModelRelativePath = @"models\text.gguf",
                Alias = "text-only",
                ContextSize = 8192,
            },
            @"D:\llama.cpp",
            false);

        Assert.Contains("no-mmproj = true\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WhenExternalVisionEnabled_EmitsProjectorAndTuning()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-vision-preset-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "models"));
            File.WriteAllBytes(Path.Combine(root, "models", "mmproj.gguf"), [1]);
            var result = RouterPresetGenerator.Generate(
                new ModelProfile
                {
                    Id = "vision",
                    DisplayName = "Vision",
                    ModelRelativePath = @"models\model.gguf",
                    Alias = "vision",
                    ContextSize = 8192,
                    VisionEnabled = true,
                    VisionSource = VisionSourceKind.External,
                    VisionCapabilityStatus = VisionCapabilityStatus.Verified,
                    VisionProjectorRelativePath = @"models\mmproj.gguf",
                    VisionProjectorOffload = true,
                    VisionImageMinTokens = 64,
                    VisionImageMaxTokens = 1024,
                    VisionBatchMaxTokens = 2048,
                    VisionValidationSignature = "verified",
                    VisionValidatedAtUtc = DateTimeOffset.UtcNow,
                },
                root,
                false);

            Assert.Contains($"mmproj = {Path.Combine(root, "models", "mmproj.gguf")}\n", result, StringComparison.Ordinal);
            Assert.Contains("mmproj-offload = true\n", result, StringComparison.Ordinal);
            Assert.Contains("image-min-tokens = 64\n", result, StringComparison.Ordinal);
            Assert.Contains("image-max-tokens = 1024\n", result, StringComparison.Ordinal);
            Assert.Contains("mtmd-batch-max-tokens = 2048\n", result, StringComparison.Ordinal);
            Assert.DoesNotContain("no-mmproj = true", result, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
