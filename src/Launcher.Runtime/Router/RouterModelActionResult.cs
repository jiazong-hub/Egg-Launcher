namespace Launcher.Runtime.Router;

public sealed record RouterModelActionResult(
    bool Succeeded,
    int? StatusCode,
    string? Diagnostic);
