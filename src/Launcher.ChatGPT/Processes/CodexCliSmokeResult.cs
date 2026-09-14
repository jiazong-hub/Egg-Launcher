namespace Launcher.ChatGPT.Processes;

public sealed record CodexCliSmokeResult(
    bool Succeeded,
    int? ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    int CommandExecutionAttempts,
    int SuccessfulCommandExecutions,
    int PolicyBlockedCommandExecutions,
    TimeSpan Elapsed);
