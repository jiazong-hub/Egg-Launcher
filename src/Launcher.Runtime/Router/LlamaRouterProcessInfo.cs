namespace Launcher.Runtime.Router;

public sealed record LlamaRouterProcessInfo(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    Uri BaseUri,
    string StandardOutputLogPath,
    string StandardErrorLogPath,
    RouterHealthSnapshot InitialHealth);

