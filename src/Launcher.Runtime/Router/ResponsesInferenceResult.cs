namespace Launcher.Runtime.Router;

public sealed record ResponsesInferenceResult(
    bool Succeeded,
    int? StatusCode,
    string? ResponseId,
    string? Model,
    string? ResponseStatus,
    string? IncompleteReason,
    string? OutputText,
    string? Diagnostic,
    TimeSpan Elapsed);
