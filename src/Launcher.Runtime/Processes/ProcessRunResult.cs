namespace Launcher.Runtime.Processes;

public sealed record ProcessRunResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

