namespace Launcher.Models.Profiles;

public sealed record ModelProfile
{
    public const int CurrentSchemaVersion = 3;

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

    public required string Alias { get; init; }

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

    public string FlashAttention { get; init; } = "auto";

    public string CacheTypeK { get; init; } = "f16";

    public string CacheTypeV { get; init; } = "f16";

    public int Parallel { get; init; } = -1;

    public bool Jinja { get; init; } = true;

    public int? BatchSize { get; init; }

    public int? MicroBatchSize { get; init; }

    public int IdleSleepSeconds { get; init; } = 300;

    public string? ChatTemplateRelativePath { get; init; }

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
