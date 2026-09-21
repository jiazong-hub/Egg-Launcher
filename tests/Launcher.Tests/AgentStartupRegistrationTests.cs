using Launcher.Core.Startup;
using Launcher.Core.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Launcher.Tests;

public sealed class AgentStartupRegistrationTests
{
    [Fact]
    public void Inspect_OnlyTreatsExactAgentCommandAsEnabled()
    {
        var executablePath = Path.GetFullPath(Path.Combine("package", "Launcher.Agent.exe"));
        var store = new MemoryRunEntryStore
        {
            Command = $"\"{executablePath}\" --unexpected",
        };
        var registration = new AgentStartupRegistration(store);

        var state = registration.Inspect(executablePath);

        Assert.True(state.HasRegistration);
        Assert.False(state.IsEnabled);
        Assert.Equal($"\"{executablePath}\"", state.ExpectedCommand);
    }

    [Fact]
    public void Inspect_WithStartupArgument_RequiresExactSilentLauncherCommand()
    {
        var executablePath = Path.GetFullPath(Path.Combine("package", "Launcher.App.exe"));
        var store = new MemoryRunEntryStore
        {
            Command = $"\"{executablePath}\" --startup",
        };
        var registration = new AgentStartupRegistration(store);

        var state = registration.Inspect(executablePath, "--startup");

        Assert.True(state.IsEnabled);
        Assert.Equal($"\"{executablePath}\" --startup", state.ExpectedCommand);
    }

    [Fact]
    public void SetEnabled_MigratesLegacyAgentRunEntryToEggLauncherEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PrivateFilePermissions.HardenDirectory(root);
            var executablePath = Path.Combine(root, "Launcher.App.exe");
            File.WriteAllText(executablePath, string.Empty);
            var store = new MemoryRunEntryStore
            {
                LegacyCommand = "\"old-agent.exe\"",
            };
            var registration = new AgentStartupRegistration(store);

            registration.SetEnabled(true, executablePath, "--startup");

            Assert.Equal($"\"{Path.GetFullPath(executablePath)}\" --startup", store.Command);
            Assert.Null(store.LegacyCommand);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetEnabled_AndRestore_OnlyMutateOwnedValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PrivateFilePermissions.HardenDirectory(root);
            var executablePath = Path.Combine(root, "Launcher.Agent.exe");
            File.WriteAllText(executablePath, string.Empty);
            var store = new MemoryRunEntryStore { Command = "previous-command" };
            var registration = new AgentStartupRegistration(store);

            registration.SetEnabled(true, executablePath);
            Assert.Equal($"\"{Path.GetFullPath(executablePath)}\"", store.Command);

            registration.SetEnabled(false, executablePath);
            Assert.Null(store.Command);

            registration.Restore("previous-command");
            Assert.Equal("previous-command", store.Command);
            Assert.Contains(AgentStartupRegistration.ValueName, store.ValueNames);
            Assert.Contains(AgentStartupRegistration.LegacyValueName, store.ValueNames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetEnabled_WhenInstallDirectoryIsBroadlyWritable_RejectsRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = new DirectoryInfo(root);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.CreateFiles | FileSystemRights.WriteData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
            var executablePath = Path.Combine(root, "Launcher.Agent.exe");
            File.WriteAllText(executablePath, string.Empty);
            var store = new MemoryRunEntryStore();
            var registration = new AgentStartupRegistration(store);

            Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true, executablePath));
            Assert.Null(store.Command);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemoryRunEntryStore : IUserRunEntryStore
    {
        private readonly Dictionary<string, string> _commands = new(StringComparer.Ordinal);

        public string? Command
        {
            get => _commands.GetValueOrDefault(AgentStartupRegistration.ValueName)
                ?? _commands.GetValueOrDefault(AgentStartupRegistration.LegacyValueName);
            set
            {
                _commands.Clear();
                if (value is not null)
                {
                    _commands[AgentStartupRegistration.ValueName] = value;
                }
            }
        }

        public string? LegacyCommand
        {
            get => _commands.GetValueOrDefault(AgentStartupRegistration.LegacyValueName);
            set
            {
                if (value is null)
                {
                    _commands.Remove(AgentStartupRegistration.LegacyValueName);
                }
                else
                {
                    _commands[AgentStartupRegistration.LegacyValueName] = value;
                }
            }
        }

        public List<string> ValueNames { get; } = [];

        public string? Read(string valueName)
        {
            ValueNames.Add(valueName);
            return _commands.GetValueOrDefault(valueName);
        }

        public void Write(string valueName, string command)
        {
            ValueNames.Add(valueName);
            _commands[valueName] = command;
        }

        public void Delete(string valueName)
        {
            ValueNames.Add(valueName);
            _commands.Remove(valueName);
        }
    }
}
