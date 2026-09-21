namespace Launcher.Core.Configuration;

public sealed record RuntimeState
{
    public const int CurrentAgentProtocolVersion = 9;

    public int AgentProtocolVersion { get; init; }

    public string? AgentExecutablePath { get; init; }

    public DateTimeOffset? AgentStartedAtUtc { get; init; }

    public ProviderMode SelectedMode { get; init; } = ProviderMode.OpenAI;

    public RuntimePhase Phase { get; init; } = RuntimePhase.Stopped;

    public int? RouterProcessId { get; init; }

    public DateTimeOffset? RouterStartedAtUtc { get; init; }

    public string? RouterExecutablePath { get; init; }

    public string? RouterBaseUri { get; init; }

    public string? ProtectedRouterApiKey { get; init; }

    public string? PublicProxyBaseUri { get; init; }

    public string? SelectedModelId { get; init; }

    public int? AgentProcessId { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? LastError { get; init; }
}
