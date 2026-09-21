namespace Launcher.Core.Configuration;

public sealed record LauncherSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ProviderMode SelectedMode { get; init; } = ProviderMode.OpenAI;

    public string? SelectedModelId { get; init; }

    /// <summary>
    /// User selection shown in Egg Launcher. This is intentionally separate from
    /// SelectedMode: ChatGPT configuration is not changed until the launch button
    /// commits this pending selection.
    /// </summary>
    public ProviderMode? PendingMode { get; init; }

    public string? PendingModelId { get; init; }

    /// <summary>
    /// Minimal recovery metadata for the pending local selection. Model parameters
    /// remain owned by the profile JSON; these fields only let the launcher safely
    /// rebuild defaults when that JSON and its backup were removed externally.
    /// </summary>
    public string? PendingModelRelativePath { get; init; }

    public string? PendingModelDisplayName { get; init; }

    /// <summary>
    /// Distinguishes an intentional empty preselection from settings created before
    /// Egg Launcher introduced the preselection workflow.
    /// </summary>
    public bool PendingSelectionInitialized { get; init; }

    public string? LlamaRoot { get; init; }

    public int RouterPort { get; init; } = 8080;

    public bool PublicProxyPortInitialized { get; init; }

    public bool StartAgentAtLogin { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    public AppLanguage Language { get; init; } = AppLanguage.System;

    public bool LlamaMetricsSupported { get; init; }
}
