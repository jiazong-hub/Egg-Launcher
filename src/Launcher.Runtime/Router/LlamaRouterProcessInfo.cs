namespace Launcher.Runtime.Router;

public sealed record LlamaRouterProcessInfo(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    Uri BaseUri,
    string StandardOutputLogPath,
    string StandardErrorLogPath,
    RouterHealthSnapshot InitialHealth)
{
    public string DiagnosticRunId { get; init; } = string.Empty;
}
