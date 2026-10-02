namespace Launcher.Runtime.Router;

public interface ILlamaRouterProcessManager : IAsyncDisposable
{
    int? OwnedProcessId { get; }

    int? LastExitCode => null;

    string? LastDiagnosticRunId => null;

    string? ContextShiftDisabledReason => null;

    void SetDetailedDiagnosticsEnabled(bool enabled) { }

    Task<LlamaRouterProcessInfo> StartAsync(
        LlamaRouterStartRequest request,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
