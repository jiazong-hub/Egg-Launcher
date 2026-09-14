namespace Launcher.Runtime.Monitoring;

public sealed record WindowsPerformanceSnapshot(
    double? CpuUsagePercent,
    double? LlamaCpuUsagePercent,
    long TotalMemoryBytes,
    long UsedMemoryBytes,
    long LlamaWorkingSetBytes,
    double? GpuUsagePercent,
    double? ComputeUsagePercent,
    double? LlamaGpuUsagePercent,
    long? DedicatedGpuMemoryBytes,
    long? LlamaDedicatedGpuMemoryBytes,
    int LlamaProcessCount,
    string? GpuDiagnostic);
