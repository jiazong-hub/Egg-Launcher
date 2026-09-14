namespace Launcher.Core.Configuration;

public sealed record LauncherDataPaths(
    string Root,
    string SettingsFile,
    string RuntimeStateFile,
    string RecoveryFile,
    string ManagedConfigSnapshotFile,
    string BackupsDirectory,
    string ProfilesDirectory,
    string GeneratedDirectory,
    string LocalModelCatalogFile,
    string RouterPresetFile,
    string LogsDirectory)
{
    public string AgentLogFile => Path.Combine(LogsDirectory, "agent.log");

    public string ProxyLogFile => Path.Combine(LogsDirectory, "proxy.jsonl");

    public string ModeSwitchLockFile => Path.Combine(Root, "mode-switch.lock");

    public string ConfigTransactionLockFile => Path.Combine(Root, "config-transaction.lock");

    public static LauncherDataPaths ForCurrentUser(string? rootOverride = null)
    {
        var root = rootOverride;
        if (string.IsNullOrWhiteSpace(root))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(localAppData, "ChatGPTLocalLauncher");
        }

        root = Path.GetFullPath(root);

        return new LauncherDataPaths(
            root,
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "runtime-state.json"),
            Path.Combine(root, "recovery.json"),
            Path.Combine(root, "managed-config-snapshot.json"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "profiles"),
            Path.Combine(root, "generated"),
            Path.Combine(root, "generated", "local-models.json"),
            Path.Combine(root, "generated", "models.ini"),
            Path.Combine(root, "logs"));
    }
}
