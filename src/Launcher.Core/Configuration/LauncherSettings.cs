namespace Launcher.Core.Configuration;

public sealed record LauncherSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ProviderMode SelectedMode { get; init; } = ProviderMode.OpenAI;

    public string? SelectedModelId { get; init; }

    public string? LlamaRoot { get; init; }

    public int RouterPort { get; init; } = 8080;

    public bool PublicProxyPortInitialized { get; init; }

    public bool StartAgentAtLogin { get; init; }

    public bool LlamaMetricsSupported { get; init; }
}
