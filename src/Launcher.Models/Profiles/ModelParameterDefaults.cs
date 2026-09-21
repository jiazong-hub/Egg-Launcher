namespace Launcher.Models.Profiles;

/// <summary>
/// A parameter-only snapshot owned by one model Profile.
/// Model identity, display name and file location are deliberately excluded.
/// </summary>
public sealed record ModelParameterDefaults
{
    public int ContextSize { get; init; }

    public int CompactionSafetyReserve { get; init; } = ModelProfile.MinimumCompactionSafetyReserve;

    public string GpuLayers { get; init; } = "auto";

    public string? Device { get; init; }

    public MoeExpertPlacement? MoeExpertPlacement { get; init; }

    public int? CpuMoeLayers { get; init; }

    public string FlashAttention { get; init; } = "auto";

    public string CacheTypeK { get; init; } = "f16";

    public string CacheTypeV { get; init; } = "f16";

    public int Parallel { get; init; } = -1;

    public bool Jinja { get; init; } = true;

    public int? BatchSize { get; init; }

    public int? MicroBatchSize { get; init; }

    public int IdleSleepSeconds { get; init; } = 300;

    public string? ChatTemplateRelativePath { get; init; }

    public bool MtpEnabled { get; init; }

    public MtpSourceKind MtpSource { get; init; } = MtpSourceKind.Embedded;

    public string? MtpDraftModelRelativePath { get; init; }

    public int? MtpDraftMaxTokens { get; init; }

    public int? MtpDraftMinTokens { get; init; }

    public double? MtpDraftMinimumProbability { get; init; }

    public double? MtpDraftSplitProbability { get; init; }

    public bool? MtpBackendSampling { get; init; }

    public string? MtpDraftGpuLayers { get; init; }

    public string? MtpDraftDevice { get; init; }

    public string? MtpDraftCacheTypeK { get; init; }

    public string? MtpDraftCacheTypeV { get; init; }

    public int? MtpDraftThreads { get; init; }

    public int? MtpDraftBatchThreads { get; init; }

    public bool VisionEnabled { get; init; }

    public bool? VisionProjectorOffload { get; init; }

    public string? VisionProjectorDevice { get; init; }

    public int? VisionImageMinTokens { get; init; }

    public int? VisionImageMaxTokens { get; init; }

    public int? VisionBatchMaxTokens { get; init; }

    public IReadOnlyDictionary<string, string?> ExtraArguments { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public static ModelParameterDefaults FromProfile(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ModelParameterDefaults
        {
            ContextSize = profile.ContextSize,
            CompactionSafetyReserve = profile.CompactionSafetyReserve,
            GpuLayers = profile.GpuLayers,
            Device = profile.Device,
            MoeExpertPlacement = profile.MoeExpertPlacement,
            CpuMoeLayers = profile.CpuMoeLayers,
            FlashAttention = profile.FlashAttention,
            CacheTypeK = profile.CacheTypeK,
            CacheTypeV = profile.CacheTypeV,
            Parallel = profile.Parallel,
            Jinja = profile.Jinja,
            BatchSize = profile.BatchSize,
            MicroBatchSize = profile.MicroBatchSize,
            IdleSleepSeconds = profile.IdleSleepSeconds,
            ChatTemplateRelativePath = profile.ChatTemplateRelativePath,
            MtpEnabled = profile.MtpEnabled,
            MtpSource = profile.MtpSource,
            MtpDraftModelRelativePath = profile.MtpDraftModelRelativePath,
            MtpDraftMaxTokens = profile.MtpDraftMaxTokens,
            MtpDraftMinTokens = profile.MtpDraftMinTokens,
            MtpDraftMinimumProbability = profile.MtpDraftMinimumProbability,
            MtpDraftSplitProbability = profile.MtpDraftSplitProbability,
            MtpBackendSampling = profile.MtpBackendSampling,
            MtpDraftGpuLayers = profile.MtpDraftGpuLayers,
            MtpDraftDevice = profile.MtpDraftDevice,
            MtpDraftCacheTypeK = profile.MtpDraftCacheTypeK,
            MtpDraftCacheTypeV = profile.MtpDraftCacheTypeV,
            MtpDraftThreads = profile.MtpDraftThreads,
            MtpDraftBatchThreads = profile.MtpDraftBatchThreads,
            VisionEnabled = profile.VisionEnabled,
            VisionProjectorOffload = profile.VisionProjectorOffload,
            VisionProjectorDevice = profile.VisionProjectorDevice,
            VisionImageMinTokens = profile.VisionImageMinTokens,
            VisionImageMaxTokens = profile.VisionImageMaxTokens,
            VisionBatchMaxTokens = profile.VisionBatchMaxTokens,
            ExtraArguments = profile.ExtraArguments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
        };
    }

    public ModelProfile ApplyTo(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with
        {
            ContextSize = ContextSize,
            CompactionSafetyReserve = CompactionSafetyReserve,
            GpuLayers = GpuLayers,
            Device = Device,
            MoeExpertPlacement = MoeExpertPlacement,
            CpuMoeLayers = CpuMoeLayers,
            FlashAttention = FlashAttention,
            CacheTypeK = CacheTypeK,
            CacheTypeV = CacheTypeV,
            Parallel = Parallel,
            Jinja = Jinja,
            BatchSize = BatchSize,
            MicroBatchSize = MicroBatchSize,
            IdleSleepSeconds = IdleSleepSeconds,
            ChatTemplateRelativePath = ChatTemplateRelativePath,
            MtpEnabled = MtpEnabled && IsMtpSourceReady(profile),
            // MTP source association is model metadata, not a tunable default. Preserve the
            // current model's verified/detected source instead of restoring a stale file path.
            MtpSource = profile.MtpSource,
            MtpDraftModelRelativePath = profile.MtpDraftModelRelativePath,
            MtpDraftMaxTokens = MtpDraftMaxTokens,
            MtpDraftMinTokens = MtpDraftMinTokens,
            MtpDraftMinimumProbability = MtpDraftMinimumProbability,
            MtpDraftSplitProbability = MtpDraftSplitProbability,
            MtpBackendSampling = MtpBackendSampling,
            MtpDraftGpuLayers = MtpDraftGpuLayers,
            MtpDraftDevice = MtpDraftDevice,
            MtpDraftCacheTypeK = MtpDraftCacheTypeK,
            MtpDraftCacheTypeV = MtpDraftCacheTypeV,
            MtpDraftThreads = MtpDraftThreads,
            MtpDraftBatchThreads = MtpDraftBatchThreads,
            MtpCapabilityStatus = profile.MtpCapabilityStatus,
            MtpValidationSignature = profile.MtpValidationSignature,
            MtpValidatedAtUtc = profile.MtpValidatedAtUtc,
            VisionEnabled = VisionEnabled && VisionValidationFingerprint.HasUsableSource(profile),
            VisionSource = profile.VisionSource,
            VisionCapabilityStatus = profile.VisionCapabilityStatus,
            VisionProjectorRelativePath = profile.VisionProjectorRelativePath,
            VisionProjectorOffload = VisionProjectorOffload,
            VisionProjectorDevice = VisionProjectorDevice,
            VisionImageMinTokens = VisionImageMinTokens,
            VisionImageMaxTokens = VisionImageMaxTokens,
            VisionBatchMaxTokens = VisionBatchMaxTokens,
            VisionValidationSignature = profile.VisionValidationSignature,
            VisionValidatedAtUtc = profile.VisionValidatedAtUtc,
            ExtraArguments = ExtraArguments is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : ExtraArguments.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
        };
    }

    private static bool IsMtpSourceReady(ModelProfile profile) =>
        profile.MtpSource == MtpSourceKind.External
            ? profile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
              && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath)
            : profile.MtpCapabilityStatus is MtpCapabilityStatus.EmbeddedCandidate
                or MtpCapabilityStatus.Verified;
}
