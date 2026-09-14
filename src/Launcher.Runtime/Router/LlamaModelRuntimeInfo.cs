namespace Launcher.Runtime.Router;

public sealed record LlamaModelRuntimeInfo(
    string Id,
    string? Path,
    bool InCache,
    bool CanRemove,
    string Source,
    string Status,
    bool Failed,
    int? ExitCode,
    long? SizeBytes,
    long? ParameterCount,
    int? TrainingContextSize,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities);
