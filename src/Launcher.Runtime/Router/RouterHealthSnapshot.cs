namespace Launcher.Runtime.Router;

public sealed record RouterHealthSnapshot(
    bool IsHealthy,
    int? HealthStatusCode,
    int? ModelsStatusCode,
    IReadOnlyList<string> ModelIds,
    string? Diagnostic);

