namespace Launcher.ChatGPT.Processes;

public sealed record ChatGptClientLaunchResult(
    bool Succeeded,
    bool AlreadyRunning,
    string? Diagnostic);
