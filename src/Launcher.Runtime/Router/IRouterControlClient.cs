namespace Launcher.Runtime.Router;

public interface IRouterControlClient : IRouterHealthClient
{
    Task<ResponsesApiRouteProbeResult> ProbeResponsesRouteAsync(
        Uri baseUri,
        CancellationToken cancellationToken = default);

    Task<RouterModelActionResult> UnloadModelAsync(
        Uri baseUri,
        string modelId,
        CancellationToken cancellationToken = default);
}
