namespace Launcher.ChatGPT.Processes;

public sealed record CodexCliSmokeRequest
{
    public required string ExecutablePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string IsolatedCodexHome { get; init; }

    public required string ModelCatalogPath { get; init; }

    public required string ModelSlug { get; init; }

    public required Uri OpenAIBaseUrl { get; init; }

    public required string Prompt { get; init; }

    public string? FollowUpPrompt { get; init; }

    public int? AutoCompactTokenLimit { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
