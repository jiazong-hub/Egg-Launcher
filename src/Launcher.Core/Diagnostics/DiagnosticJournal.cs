using System.Diagnostics;

namespace Launcher.Core.Diagnostics;

/// <summary>Low-frequency operation records using the existing mode-aware JSONL writer.</summary>
public sealed class DiagnosticJournal
{
    private readonly ModeAwareJsonLineDiagnosticLog? _log;
    private readonly Func<DiagnosticEvent, Task>? _writer;
    public static DiagnosticJournal None { get; } = new();
    private DiagnosticJournal() { }
    public DiagnosticJournal(ModeAwareJsonLineDiagnosticLog log) => _log = log;
    public DiagnosticJournal(Func<DiagnosticEvent, Task> writer) => _writer = writer;
    private bool _detailed;
    public bool Detailed => _log?.DetailedDiagnosticsEnabled ?? _detailed;

    public void SetDetailed(bool detailed)
    {
        if (_log is null && _writer is null) return;
        _detailed = detailed;
        _log?.SetDetailedDiagnosticsEnabled(detailed);
    }

    public DiagnosticOperation Begin(string name, IReadOnlyDictionary<string, object?>? properties = null) => new(this, name, properties);

    public async Task<T> RunAsync<T>(string name, IReadOnlyDictionary<string, object?>? properties,
        Func<DiagnosticOperation, Task<T>> action)
    {
        await using var operation = Begin(name, properties);
        try
        {
            // The supplied action may edit WPF controls. Keep the caller's context when
            // invoking it; only the log writer itself uses background continuations.
            await operation.StageAsync("started");
            var result = await action(operation);
            await operation.CompleteAsync().ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await operation.CompleteAsync("cancelled", "operation_cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task WriteAsync(string name, string outcome, IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null, bool detailedOnly = false)
    {
        try
        {
            var detailed = _log?.DetailedDiagnosticsEnabled ?? Detailed;
            if ((_log is null && _writer is null) || (detailedOnly && !detailed)) return;
            var fields = properties is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(properties);
            fields["outcome"] = outcome;
            if (exception is not null)
            {
                // Exceptions can contain native response bodies or user JSON. Record classification,
                // not arbitrary exception messages; the static reason/stage supplies the context.
                fields["exceptionType"] = exception.GetType().FullName;
                fields["hResult"] = $"0x{exception.HResult:X8}";
                if (detailed) fields["stackTrace"] = exception.StackTrace;
            }
            var diagnosticEvent = new DiagnosticEvent(name,
                exception is not null || outcome == "failed" ? "error" : outcome is "blocked" or "unavailable" ? "warning" : "info",
                null, fields);
            await AppendEventAsync(diagnosticEvent).ConfigureAwait(false);
        }
        catch
        {
            // Recording a diagnostic must never change the result of a user operation.
        }
    }

    public async Task AppendEventAsync(DiagnosticEvent diagnosticEvent)
    {
        try
        {
            if (_log is not null) await _log.AppendEventAsync(diagnosticEvent).ConfigureAwait(false);
            else if (_writer is not null) await _writer(diagnosticEvent).ConfigureAwait(false);
        }
        catch
        {
            // The business operation retains its own outcome even when the log sink fails.
        }
    }
}

public sealed class DiagnosticOperation : IAsyncDisposable
{
    private readonly DiagnosticJournal _journal;
    private readonly string _name;
    private readonly Dictionary<string, object?> _properties;
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _completed;
    public string Stage { get; private set; } = "requested";

    internal DiagnosticOperation(DiagnosticJournal journal, string name, IReadOnlyDictionary<string, object?>? properties)
    {
        _journal = journal;
        _name = name;
        _properties = properties is null ? new() : new(properties);
        _properties["operationId"] = Guid.NewGuid().ToString("N");
    }

    public void Set(string name, object? value) => _properties[name] = value;
    public async Task StageAsync(string stage)
    {
        Stage = stage;
        await _journal.WriteAsync(_name + "_stage", "in_progress", Fields(), detailedOnly: true).ConfigureAwait(false);
    }

    public Task CompleteAsync(string outcome = "success", string? reason = null) => FinishAsync(outcome, reason, null);
    public Task FailAsync(Exception exception, string? reason = null) => FinishAsync("failed", reason ?? exception switch
    {
        FileNotFoundException => "file_missing",
        UnauthorizedAccessException => "access_denied",
        InvalidDataException => "validation_failed",
        NotSupportedException => "unsupported",
        IOException => "file_io_or_conflict",
        InvalidOperationException => "state_or_precondition_failed",
        _ => "operation_failed",
    }, exception);
    private async Task FinishAsync(string outcome, string? reason, Exception? exception)
    {
        if (_completed) return;
        _completed = true;
        if (reason is not null) _properties["reasonCode"] = reason;
        await _journal.WriteAsync(_name, outcome, Fields(), exception).ConfigureAwait(false);
    }
    private Dictionary<string, object?> Fields()
    {
        var fields = new Dictionary<string, object?>(_properties) { ["stage"] = Stage };
        if (_journal.Detailed) fields["durationMs"] = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        return fields;
    }
    public async ValueTask DisposeAsync()
    {
        if (!_completed) await CompleteAsync("cancelled", "not_completed").ConfigureAwait(false);
    }
}
