namespace Launcher.Runtime.Router;

public sealed record LlamaRouterStartRequest
{
    public bool? ContextShiftRequested { get; init; }

    public Action<string>? ContextShiftDisabledObserver { get; init; }

    public required string ExecutablePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string LogDirectory { get; init; }

    public required LlamaRouterOptions Options { get; init; }

    public string? ModelCacheDirectory { get; init; }

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);
}
