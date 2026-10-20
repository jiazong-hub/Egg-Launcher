namespace Launcher.Models.Profiles;

/// <summary>The raw-completion shift check depends on loading and memory settings, not chat presentation.</summary>
public static class ContextShiftProbeProfile
{
    private static readonly HashSet<string> RelevantArguments = new(StringComparer.Ordinal)
    {
        "fit", "fit-target", "fit-ctx", "threads", "threads-batch", "load-mode", "lazy-mode",
        "n-cpu-ffn", "split-mode", "tensor-split", "main-gpu", "numa", "override-tensor",
        "kv-offload", "no-kv-offload", "repack", "no-repack", "op-offload", "no-op-offload",
        "no-host", "cpu-moe", "n-cpu-moe", "keep", "ctx-checkpoints", "swa-checkpoints", "swa-full",
        "rope-scaling", "rope-scale", "rope-freq-base", "rope-freq-scale", "yarn-orig-ctx",
        "yarn-ext-factor", "yarn-attn-factor", "yarn-beta-slow", "yarn-beta-fast",
    };

    public static ModelProfile Create(ModelProfile source) => new()
    {
        Id = source.Id,
        Alias = source.Alias,
        DisplayName = source.DisplayName,
        ModelRelativePath = source.ModelRelativePath,
        SourceKind = source.SourceKind,
        RemoteModelId = source.RemoteModelId,
        RemoteRepositoryId = source.RemoteRepositoryId,
        RemoteQuantization = source.RemoteQuantization,
        KnownSizeBytes = source.KnownSizeBytes,
        KnownShardCount = source.KnownShardCount,
        ModelType = source.ModelType,
        ModelTypeSource = source.ModelTypeSource,
        ContextSize = source.ContextSize,
        ContextShiftEnabled = true,
        CompactionSafetyReserve = ModelProfile.CalculateInitialCompactionSafetyReserve(source.ContextSize),
        GpuLayers = source.GpuLayers,
        Device = source.Device,
        MoeExpertPlacement = source.MoeExpertPlacement,
        CpuMoeLayers = source.CpuMoeLayers,
        FlashAttention = source.FlashAttention,
        CacheTypeK = source.CacheTypeK,
        CacheTypeV = source.CacheTypeV,
        Parallel = source.Parallel,
        BatchSize = source.BatchSize,
        MicroBatchSize = source.MicroBatchSize,
        IdleSleepSeconds = -1,
        Jinja = false,
        MtpEnabled = source.MtpEnabled,
        MtpSource = source.MtpEnabled ? source.MtpSource : MtpSourceKind.Embedded,
        MtpDraftModelRelativePath = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftModelRelativePath : null,
        MtpCapabilityStatus = source.MtpEnabled ? source.MtpCapabilityStatus : MtpCapabilityStatus.Unknown,
        MtpValidationSignature = source.MtpEnabled ? source.MtpValidationSignature : null,
        MtpValidatedAtUtc = source.MtpEnabled ? source.MtpValidatedAtUtc : null,
        MtpDraftMaxTokens = source.MtpEnabled ? source.MtpDraftMaxTokens : null,
        MtpDraftMinTokens = source.MtpEnabled ? source.MtpDraftMinTokens : null,
        MtpDraftMinimumProbability = source.MtpEnabled ? source.MtpDraftMinimumProbability : null,
        MtpDraftSplitProbability = source.MtpEnabled ? source.MtpDraftSplitProbability : null,
        MtpBackendSampling = source.MtpEnabled ? source.MtpBackendSampling : null,
        MtpDraftGpuLayers = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftGpuLayers : null,
        MtpDraftDevice = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftDevice : null,
        MtpDraftCacheTypeK = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftCacheTypeK : null,
        MtpDraftCacheTypeV = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftCacheTypeV : null,
        MtpDraftThreads = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftThreads : null,
        MtpDraftBatchThreads = source.MtpEnabled && source.MtpSource == MtpSourceKind.External ? source.MtpDraftBatchThreads : null,
        RuntimeParameters = new RuntimeParameterSettings
        {
            KvUnified = source.RuntimeParameters.KvUnified,
            MoeCacheMiB = source.ModelType == ModelType.MoE ? source.RuntimeParameters.MoeCacheMiB : null,
            DraftMicroBatchSize = source.MtpEnabled ? source.RuntimeParameters.DraftMicroBatchSize : null,
            DraftSampling = source.MtpEnabled ? source.RuntimeParameters.DraftSampling : null,
            CheckpointMinimumStep = source.RuntimeParameters.CheckpointMinimumStep,
        },
        ExtraArguments = source.ExtraArguments.Where(pair => RelevantArguments.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };
}
