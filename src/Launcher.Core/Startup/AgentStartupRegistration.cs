namespace Launcher.Core.Startup;

public sealed class AgentStartupRegistration(IUserRunEntryStore runEntryStore)
{
    public const string ValueName = "ChatGPTLocalLauncher.Agent";

    public AgentStartupRegistrationState Inspect(string agentExecutablePath)
    {
        var expectedCommand = BuildCommand(agentExecutablePath);
        return new AgentStartupRegistrationState(
            expectedCommand,
            runEntryStore.Read(ValueName));
    }

    public void SetEnabled(bool enabled, string agentExecutablePath)
    {
        var command = BuildCommand(agentExecutablePath);
        if (enabled)
        {
            var fullPath = Path.GetFullPath(agentExecutablePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到 Launcher.Agent 可执行文件。", fullPath);
            }

            StartupExecutableTrustValidator.EnsureSafeForStartup(fullPath);

            runEntryStore.Write(ValueName, command);
        }
        else
        {
            runEntryStore.Delete(ValueName);
        }
    }

    public void Restore(string? registeredCommand)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand))
        {
            runEntryStore.Delete(ValueName);
        }
        else
        {
            runEntryStore.Write(ValueName, registeredCommand);
        }
    }

    public static string BuildCommand(string agentExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        var fullPath = Path.GetFullPath(agentExecutablePath);
        if (fullPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent 路径不能包含双引号。", nameof(agentExecutablePath));
        }

        return $"\"{fullPath}\"";
    }
}
