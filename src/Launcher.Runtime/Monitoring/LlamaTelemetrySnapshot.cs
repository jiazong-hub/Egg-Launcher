namespace Launcher.Runtime.Monitoring;

public sealed record LlamaTelemetrySnapshot(
    int SlotCount,
    int ProcessingSlots,
    int? ContextCapacityTokens,
    int? ContextUsedTokens,
    double? KvCacheUsageRatio,
    long? PromptTokensTotal,
    long? PredictedTokensTotal,
    double? PromptTokensPerSecond,
    double? PredictedTokensPerSecond,
    int? RequestsProcessing,
    int? RequestsDeferred,
    string? Diagnostic);
