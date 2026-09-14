namespace Launcher.Runtime.Router;

public sealed record ResponsesApiRouteProbeResult(
    bool IsAvailable,
    int? StatusCode,
    string? Diagnostic);
