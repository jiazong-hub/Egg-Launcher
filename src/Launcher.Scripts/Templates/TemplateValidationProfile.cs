using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

/// <summary>Builds a text-render probe without inheriting runtime capability requirements.</summary>
public static class TemplateValidationProfile
{
    // Keep model placement/loading controls, never arbitrary feature overrides or custom arguments.
    private static readonly HashSet<string> LoadingArguments = new(StringComparer.Ordinal)
    {
        "fit", "fit-target", "fit-ctx", "threads", "threads-batch", "load-mode", "lazy-mode",
        "n-cpu-ffn", "split-mode", "tensor-split", "main-gpu", "numa", "override-tensor",
        "kv-offload", "no-kv-offload", "repack", "no-repack", "op-offload", "no-op-offload",
        "no-host", "cpu-moe", "n-cpu-moe",
    };

    public static ModelProfile Create(ModelProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Start fresh: disabling a feature alone does not clear its paths, proof or invalid parameters.
        // This profile is only for the temporary Router preset, never for saving user configuration.
        return new ModelProfile
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
            GpuLayers = source.GpuLayers,
            Device = source.Device,
            MoeExpertPlacement = source.MoeExpertPlacement,
            CpuMoeLayers = source.CpuMoeLayers,
            FlashAttention = source.FlashAttention,
            CacheTypeK = source.CacheTypeK,
            CacheTypeV = source.CacheTypeV,
            ChatTemplateRelativePath = source.ChatTemplateRelativePath,
            Jinja = true,
            ContextSize = 4096,
            CompactionSafetyReserve = 2048,
            ToolOutputTokenLimit = 1024,
            Parallel = 1,
            IdleSleepSeconds = -1,
            ExtraArguments = source.ExtraArguments.Where(pair => LoadingArguments.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }
}
