namespace Launcher.Runtime.Detection;

public sealed record LlamaRuntimeProbeResult(
    string ExecutablePath,
    bool IsValid,
    string? VersionText,
    string DeviceOutput,
    LlamaRuntimeCapabilities Capabilities,
    IReadOnlyList<string> Diagnostics);

