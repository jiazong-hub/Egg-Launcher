namespace Launcher.Core.Diagnostics;

public sealed record DiagnosticEvent(
    string EventName,
    string Level,
    string? Message = null,
    IReadOnlyDictionary<string, object?>? Properties = null);
