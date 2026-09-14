namespace Launcher.Runtime.Router;

public interface IRouterHealthClient
{
    Task<RouterHealthSnapshot> ProbeAsync(Uri baseUri, CancellationToken cancellationToken = default);

    Task<RouterHealthSnapshot> WaitUntilReadyAsync(
        Uri baseUri,
        TimeSpan timeout,
        Func<bool>? hasExited = null,
        CancellationToken cancellationToken = default);
}

