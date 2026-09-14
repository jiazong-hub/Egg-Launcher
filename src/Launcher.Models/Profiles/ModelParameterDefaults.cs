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

    public static ModelParameterDefaults FromProfile(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ModelParameterDefaults
        {
            ContextSize = profile.ContextSize,
            CompactionSafetyReserve = profile.CompactionSafetyReserve,
            GpuLayers = profile.GpuLayers,
            Device = profile.Device,
            FlashAttention = profile.FlashAttention,
            CacheTypeK = profile.CacheTypeK,
            CacheTypeV = profile.CacheTypeV,
            Parallel = profile.Parallel,
            Jinja = profile.Jinja,
            BatchSize = profile.BatchSize,
            MicroBatchSize = profile.MicroBatchSize,
            IdleSleepSeconds = profile.IdleSleepSeconds,
            ChatTemplateRelativePath = profile.ChatTemplateRelativePath,
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
            FlashAttention = FlashAttention,
            CacheTypeK = CacheTypeK,
            CacheTypeV = CacheTypeV,
            Parallel = Parallel,
            Jinja = Jinja,
            BatchSize = BatchSize,
            MicroBatchSize = MicroBatchSize,
            IdleSleepSeconds = IdleSleepSeconds,
            ChatTemplateRelativePath = ChatTemplateRelativePath,
            ExtraArguments = ExtraArguments is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : ExtraArguments.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
        };
    }
}
