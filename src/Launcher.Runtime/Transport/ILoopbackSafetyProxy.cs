namespace Launcher.Runtime.Transport;

public interface ILoopbackSafetyProxy : IAsyncDisposable
{
    bool IsRunning { get; }

    Uri? PublicBaseUri { get; }

    Task StartAsync(
        Uri publicBaseUri,
        Uri upstreamBaseUri,
        CancellationToken cancellationToken = default,
        string? upstreamApiKey = null);

    Task StopAsync(CancellationToken cancellationToken = default);
}
