namespace Launcher.ChatGPT.Catalog;

public sealed record LocalModelCatalogOptions
{
    public required string Slug { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = "本地 llama.cpp 模型";

    public required int ContextWindow { get; init; }

    public IReadOnlyList<string> SupportedReasoningLevels { get; init; } = Array.Empty<string>();

    public string? DefaultReasoningLevel { get; init; }

    public bool SupportsImageInput { get; init; }
}
