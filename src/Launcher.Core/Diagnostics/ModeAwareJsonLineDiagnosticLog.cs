namespace Launcher.Core.Diagnostics;

/// <summary>
/// Writes each Agent event to the file selected by the user's current diagnostic mode.
/// </summary>
public sealed class ModeAwareJsonLineDiagnosticLog
{
    private readonly JsonLineDiagnosticLog _conciseLog;
    private readonly JsonLineDiagnosticLog _fullLog;
    private int _detailedDiagnosticsEnabled;
    private string? _routerRunId;

    public ModeAwareJsonLineDiagnosticLog(
        string concisePath,
        string fullPath,
        bool detailedDiagnosticsEnabled = false)
    {
        _conciseLog = new JsonLineDiagnosticLog(concisePath);
        _fullLog = new JsonLineDiagnosticLog(fullPath);
        _detailedDiagnosticsEnabled = detailedDiagnosticsEnabled ? 1 : 0;
        SessionId = Guid.NewGuid().ToString("N");
    }

    public string SessionId { get; }

    public string Path => Volatile.Read(ref _detailedDiagnosticsEnabled) == 1
        ? _fullLog.Path
        : _conciseLog.Path;

    public bool DetailedDiagnosticsEnabled => Volatile.Read(ref _detailedDiagnosticsEnabled) == 1;

    public void SetDetailedDiagnosticsEnabled(bool enabled) =>
        Volatile.Write(ref _detailedDiagnosticsEnabled, enabled ? 1 : 0);

    public void SetRouterRunId(string? routerRunId) =>
        Volatile.Write(ref _routerRunId, routerRunId);

    public Task AppendAsync(
        string level,
        string message,
        CancellationToken cancellationToken = default) =>
        AppendEventAsync("agent_event", level, message, cancellationToken);

    public Task AppendEventAsync(
        string eventName,
        string level,
        string message,
        CancellationToken cancellationToken = default)
    {
        var detailed = Volatile.Read(ref _detailedDiagnosticsEnabled) == 1;
        var log = detailed ? _fullLog : _conciseLog;
        return log.AppendEventAsync(
            eventName,
            level,
            message,
            SessionId,
            Volatile.Read(ref _routerRunId),
            detailed ? "full" : "concise",
            cancellationToken);
    }

    public Task AppendEventAsync(
        DiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        var detailed = Volatile.Read(ref _detailedDiagnosticsEnabled) == 1;
        var log = detailed ? _fullLog : _conciseLog;
        return log.AppendEventAsync(
            diagnosticEvent,
            SessionId,
            Volatile.Read(ref _routerRunId),
            detailed ? "full" : "concise",
            cancellationToken);
    }
}
