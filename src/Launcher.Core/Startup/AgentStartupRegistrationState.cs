namespace Launcher.Core.Startup;

public sealed record AgentStartupRegistrationState(
    string ExpectedCommand,
    string? RegisteredCommand)
{
    public bool IsEnabled => string.Equals(
        ExpectedCommand,
        RegisteredCommand,
        StringComparison.OrdinalIgnoreCase);

    public bool HasRegistration => !string.IsNullOrWhiteSpace(RegisteredCommand);
}
