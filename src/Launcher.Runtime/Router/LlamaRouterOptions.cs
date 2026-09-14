namespace Launcher.Runtime.Router;

public sealed record LlamaRouterOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 8080;

    public string? ModelsDirectory { get; init; }

    public string? ModelsPresetPath { get; init; }

    public int MaximumLoadedModels { get; init; } = 1;

    public bool AutoloadModels { get; init; } = true;

    public string? ApiKey { get; init; }

    public bool DisableMultimodalProjectorAutoDownload { get; init; }

    public bool EnableMetrics { get; init; }
}
