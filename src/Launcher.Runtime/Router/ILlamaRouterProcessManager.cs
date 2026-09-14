namespace Launcher.Runtime.Router;

public interface ILlamaRouterProcessManager : IAsyncDisposable
{
    int? OwnedProcessId { get; }

    Task<LlamaRouterProcessInfo> StartAsync(
        LlamaRouterStartRequest request,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
