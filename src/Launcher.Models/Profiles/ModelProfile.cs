namespace Launcher.Models.Profiles;

public sealed record ModelProfile
{
    public const int CurrentSchemaVersion = 6;

    public const int DefaultCompactionSafetyReserve = 8_192;

    public const int MinimumCompactionSafetyReserve = 1_024;

    public const int RecommendedMinimumCodexContext = 16_384;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string ModelRelativePath { get; init; }

    public ModelSourceKind SourceKind { get; init; } = ModelSourceKind.LocalFile;

    public string? RemoteModelId { get; init; }

    public string? RemoteRepositoryId { get; init; }

    public string? RemoteQuantization { get; init; }

    public long? KnownSizeBytes { get; init; }

    public int? KnownShardCount { get; init; }

    public ModelType ModelType { get; init; } = ModelType.Unknown;

    public ModelTypeSource ModelTypeSource { get; init; } = ModelTypeSource.Legacy;

    public required string Alias { get; init; }

    /// <summary>Whether this model is visible as a selectable card in Egg Launcher.</summary>
    public bool ShowInModePage { get; init; } = true;

    /// <summary>Stable user-defined order for Local model cards.</summary>
    public int DisplayOrder { get; init; } = int.MaxValue;

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 8080;

    public int ContextSize { get; init; }

    /// <summary>
    /// Minimum free context Codex should retain before starting the next inference.
    /// This is not a per-response output token limit.
    /// </summary>
    public int CompactionSafetyReserve { get; init; } = MinimumCompactionSafetyReserve;

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

    /// <summary>Whether native llama.cpp multi-token prediction is enabled for this model.</summary>
    public bool MtpEnabled { get; init; }

    /// <summary>Whether MTP heads are embedded in the main GGUF or supplied by a companion GGUF.</summary>
    public MtpSourceKind MtpSource { get; init; } = MtpSourceKind.Embedded;

    /// <summary>Detection and one-time native-load validation state.</summary>
    public MtpCapabilityStatus MtpCapabilityStatus { get; init; } = MtpCapabilityStatus.Unknown;

    /// <summary>Runtime-relative path to a model-specific external MTP companion.</summary>
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

    /// <summary>Signature of the exact target, companion and runtime last validated for MTP compatibility.</summary>
    public string? MtpValidationSignature { get; init; }

    public DateTimeOffset? MtpValidatedAtUtc { get; init; }

    /// <summary>Whether verified llama.cpp multimodal image input is enabled for this model.</summary>
    public bool VisionEnabled { get; init; }

    public VisionSourceKind VisionSource { get; init; } = VisionSourceKind.BuiltIn;

    public VisionCapabilityStatus VisionCapabilityStatus { get; init; } = VisionCapabilityStatus.Unknown;

    /// <summary>Runtime-relative path to an external mmproj GGUF.</summary>
    public string? VisionProjectorRelativePath { get; init; }

    public bool? VisionProjectorOffload { get; init; }

    public string? VisionProjectorDevice { get; init; }

    public int? VisionImageMinTokens { get; init; }

    public int? VisionImageMaxTokens { get; init; }

    public int? VisionBatchMaxTokens { get; init; }

    public string? VisionValidationSignature { get; init; }

    public DateTimeOffset? VisionValidatedAtUtc { get; init; }

    /// <summary>Capability state captured explicitly from Local Model Management through native llama.cpp /props.</summary>
    public ReasoningCapabilityStatus ReasoningCapabilityStatus { get; init; } = ReasoningCapabilityStatus.Unknown;

    /// <summary>Exact native levels, populated only when llama.cpp reports a complete recognized list.</summary>
    public IReadOnlyList<string> SupportedReasoningLevels { get; init; } = Array.Empty<string>();

    public string? DefaultReasoningLevel { get; init; }

    /// <summary>Whether verified levels should be exposed to ChatGPT for this model.</summary>
    public bool ExposeReasoningEffortInChatGpt { get; init; }

    /// <summary>Signature of the model, optional template, and runtime used for the explicit capability probe.</summary>
    public string? ReasoningCapabilitySignature { get; init; }

    public DateTimeOffset? ReasoningCapabilityCheckedAtUtc { get; init; }

    public IReadOnlyDictionary<string, string?> ExtraArguments { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public ModelParameterDefaults? DefaultParameters { get; init; }

    public int AutoCompactTokenLimit => ContextSize - CompactionSafetyReserve;

    public static int CalculateInitialCompactionSafetyReserve(int contextSize)
    {
        if (contextSize <= 0)
        {
            return DefaultCompactionSafetyReserve;
        }

        return Math.Max(MinimumCompactionSafetyReserve, Math.Min(DefaultCompactionSafetyReserve, contextSize / 4));
    }
}
