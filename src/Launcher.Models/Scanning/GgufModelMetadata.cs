using Launcher.Models.Profiles;

namespace Launcher.Models.Scanning;

public sealed record GgufModelMetadata(
    string? Architecture,
    int? ContextLength,
    int? ExpertCount,
    int? ExpertUsedCount,
    int? NextNPredictLayers,
    bool HasVisionEncoder = false)
{
    public bool HasEmbeddedMtp => NextNPredictLayers is > 0;

    public ModelType DetectModelType() =>
        ExpertCount is > 0
            ? ModelType.MoE
            : !string.IsNullOrWhiteSpace(Architecture)
                ? ModelType.Dense
                : ModelType.Unknown;
}
