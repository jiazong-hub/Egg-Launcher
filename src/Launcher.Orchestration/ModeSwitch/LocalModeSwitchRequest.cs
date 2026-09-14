using Launcher.Models.Profiles;

namespace Launcher.Orchestration.ModeSwitch;

public sealed record LocalModeSwitchRequest
{
    public required string CodexHome { get; init; }

    public required string RuntimeRoot { get; init; }

    public required ModelProfile Profile { get; init; }

    public int RouterPort { get; init; } = 8080;
}
