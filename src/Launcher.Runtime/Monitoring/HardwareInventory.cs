namespace Launcher.Runtime.Monitoring;

public sealed record MemoryModuleInfo(string Manufacturer, string PartNumber, long CapacityBytes, string MemoryType, int? SpeedMHz);

public sealed record GraphicsAdapterInfo(string Name, string Manufacturer, long? DedicatedBytes);

public sealed record HardwareInventory(
    string CpuName,
    int LogicalProcessorCount,
    long InstalledMemoryBytes,
    IReadOnlyList<MemoryModuleInfo> MemoryModules,
    IReadOnlyList<GraphicsAdapterInfo> GraphicsAdapters,
    string? Diagnostic);
