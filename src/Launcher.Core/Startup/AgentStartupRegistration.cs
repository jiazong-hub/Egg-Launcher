namespace Launcher.Core.Startup;

public sealed class AgentStartupRegistration(IUserRunEntryStore runEntryStore)
{
    public const string ValueName = "EggLauncher";
    public const string LegacyValueName = "ChatGPTLocalLauncher.Agent";

    public AgentStartupRegistrationState Inspect(string agentExecutablePath, string? arguments = null)
    {
        var expectedCommand = BuildCommand(agentExecutablePath, arguments);
        var registered = runEntryStore.Read(ValueName) ?? runEntryStore.Read(LegacyValueName);
        return new AgentStartupRegistrationState(expectedCommand, registered);
    }

    public void SetEnabled(bool enabled, string agentExecutablePath, string? arguments = null)
    {
        var command = BuildCommand(agentExecutablePath, arguments);
        if (enabled)
        {
            var fullPath = Path.GetFullPath(agentExecutablePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到要注册的启动程序。", fullPath);
            }

            StartupExecutableTrustValidator.EnsureSafeForStartup(fullPath);

            runEntryStore.Write(ValueName, command);
            runEntryStore.Delete(LegacyValueName);
        }
        else
        {
            runEntryStore.Delete(ValueName);
            runEntryStore.Delete(LegacyValueName);
        }
    }

    public void Restore(string? registeredCommand)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand))
        {
            runEntryStore.Delete(ValueName);
            runEntryStore.Delete(LegacyValueName);
        }
        else
        {
            runEntryStore.Write(ValueName, registeredCommand);
            runEntryStore.Delete(LegacyValueName);
        }
    }

    public static string BuildCommand(string agentExecutablePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentExecutablePath);
        var fullPath = Path.GetFullPath(agentExecutablePath);
        if (fullPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("启动程序路径不能包含双引号。", nameof(agentExecutablePath));
        }

        if (arguments?.IndexOfAny(['\r', '\n', '"']) >= 0)
        {
            throw new ArgumentException("启动参数包含不支持的字符。", nameof(arguments));
        }

        return string.IsNullOrWhiteSpace(arguments)
            ? $"\"{fullPath}\""
            : $"\"{fullPath}\" {arguments.Trim()}";
    }
}
