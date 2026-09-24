namespace Launcher.ChatGPT.Configuration;

using Launcher.Core.Configuration;
using Launcher.Models.Profiles;

public sealed record ChatGptLocalModeRequest
{
    public required string ConfigPath { get; init; }

    public required string ModelSlug { get; init; }

    public required string ModelCatalogPath { get; init; }

    public required Uri OpenAIBaseUrl { get; init; }

    public required int ContextWindow { get; init; }

    public required int AutoCompactTokenLimit { get; init; }

    public ModelSandboxSettings? SandboxSettings { get; init; }

    public LauncherSettings? OriginalLauncherSettings { get; init; }

    public LauncherSettings? TargetLauncherSettings { get; init; }
}
