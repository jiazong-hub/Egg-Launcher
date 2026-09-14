namespace Launcher.Runtime.Fit;

public sealed record LlamaFitRecommendation(
    int ContextSize,
    int GpuLayers,
    string? TensorSplit,
    string? TensorBufferOverrides,
    string NativeOutput);
