using Launcher.Models.Profiles;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Tests;

public sealed class ModelFeatureArgumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-features", Guid.NewGuid().ToString("N"));

    public ModelFeatureArgumentsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllBytes(Path.Combine(_root, "models", "mtp 100%.gguf"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "models", "mmproj.gguf"), [1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Generate_BatchAndRouterAgreeOnExternalFeatures(bool positiveFlags)
    {
        var profile = CreateProfile() with
        {
            MtpEnabled = true,
            MtpSource = MtpSourceKind.External,
            MtpDraftModelRelativePath = @"models\mtp 100%.gguf",
            MtpCapabilityStatus = MtpCapabilityStatus.Verified,
            MtpValidationSignature = "verified",
            MtpValidatedAtUtc = DateTimeOffset.UtcNow,
            MtpDraftMaxTokens = 3,
            MtpDraftMinTokens = 1,
            MtpDraftMinimumProbability = 0.25,
            MtpDraftSplitProbability = 0.5,
            MtpBackendSampling = positiveFlags,
            MtpDraftGpuLayers = "12",
            MtpDraftDevice = "Vulkan0",
            MtpDraftCacheTypeK = "q8_0",
            MtpDraftCacheTypeV = "q8_0",
            MtpDraftThreads = 8,
            MtpDraftBatchThreads = 4,
            VisionEnabled = true,
            VisionSource = VisionSourceKind.External,
            VisionProjectorRelativePath = @"models\mmproj.gguf",
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionValidationSignature = "verified",
            VisionValidatedAtUtc = DateTimeOffset.UtcNow,
            VisionProjectorOffload = positiveFlags,
            VisionProjectorDevice = "Vulkan0",
            VisionImageMinTokens = 64,
            VisionImageMaxTokens = 1024,
            VisionBatchMaxTokens = 2048,
        };
        var batch = BatchScriptGenerator.Generate(profile, _root);
        var preset = RouterPresetGenerator.Generate(profile, _root, false);
        var values = new Dictionary<string, string>
        {
            ["spec-type"] = "draft-mtp",
            ["spec-draft-n-max"] = "3",
            ["spec-draft-n-min"] = "1",
            ["spec-draft-p-min"] = "0.25",
            ["spec-draft-p-split"] = "0.5",
            ["spec-draft-ngl"] = "12",
            ["spec-draft-device"] = "Vulkan0",
            ["spec-draft-type-k"] = "q8_0",
            ["spec-draft-type-v"] = "q8_0",
            ["spec-draft-threads"] = "8",
            ["spec-draft-threads-batch"] = "4",
            ["mmproj-device"] = "Vulkan0",
            ["image-min-tokens"] = "64",
            ["image-max-tokens"] = "1024",
            ["mtmd-batch-max-tokens"] = "2048",
        };
        foreach (var pair in values)
        {
            Assert.Contains($"--{pair.Key} \"{pair.Value}\"", batch);
            Assert.Contains($"{pair.Key} = {pair.Value}\n", preset);
        }
        foreach (var flag in new[]
                 {
                     positiveFlags ? "spec-draft-backend-sampling" : "no-spec-draft-backend-sampling",
                     positiveFlags ? "mmproj-offload" : "no-mmproj-offload",
                 })
        {
            Assert.Contains($"--{flag} ^\r\n", batch);
            Assert.DoesNotContain($"--{flag} \"true\"", batch);
            Assert.Contains($"{flag} = true\n", preset);
        }
        Assert.Contains("--spec-draft-model \"%LLAMA_ROOT%\\models\\mtp 100%%.gguf\"", batch);
        Assert.Contains($"spec-draft-model = {Path.Combine(_root, "models", "mtp 100%.gguf")}\n", preset);
        Assert.Contains("--mmproj \"%LLAMA_ROOT%\\models\\mmproj.gguf\"", batch);
        Assert.DoesNotContain("--no-mmproj ^", batch);
    }

    [Fact]
    public void Generate_EmbeddedMtpDoesNotEmitCompanionResourceArguments()
    {
        var profile = CreateProfile() with
        {
            MtpEnabled = true,
            MtpSource = MtpSourceKind.Embedded,
            MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate,
            MtpDraftGpuLayers = "12",
            MtpDraftDevice = "Vulkan0",
            MtpDraftMaxTokens = 3,
        };
        var batch = BatchScriptGenerator.Generate(profile, _root);
        Assert.Contains("--spec-type \"draft-mtp\"", batch);
        Assert.DoesNotContain("--spec-draft-model", batch);
        Assert.DoesNotContain("--spec-draft-ngl", batch);
        Assert.DoesNotContain("--spec-draft-device", batch);
    }

    [Fact]
    public void Generate_DisabledFeaturesOmitMtpAndDisableVision()
    {
        var batch = BatchScriptGenerator.Generate(CreateProfile(), _root);
        Assert.DoesNotContain("--spec-", batch);
        Assert.Contains("--no-mmproj", batch);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Generate_ParallelMatchesRouterIncludingAutomaticDefault(int parallel)
    {
        var profile = CreateProfile() with { Parallel = parallel };
        Assert.Contains($"--parallel {parallel}", BatchScriptGenerator.Generate(profile, _root));
        Assert.Contains($"parallel = {parallel}\n", RouterPresetGenerator.Generate(profile, _root, false));
    }

    private static ModelProfile CreateProfile() => new()
    {
        Id = "model",
        Alias = "model",
        DisplayName = "Model",
        ModelRelativePath = @"models\model.gguf",
        ContextSize = 8192,
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
