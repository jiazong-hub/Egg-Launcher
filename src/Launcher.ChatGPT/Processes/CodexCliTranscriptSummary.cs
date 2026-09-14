namespace Launcher.ChatGPT.Processes;

public sealed record CodexCliTranscriptSummary(
    int CommandExecutionAttempts,
    int SuccessfulCommandExecutions,
    int PolicyBlockedCommandExecutions);
