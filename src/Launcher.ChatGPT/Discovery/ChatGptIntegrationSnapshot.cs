namespace Launcher.ChatGPT.Discovery;

public sealed record ChatGptIntegrationSnapshot(
    string CodexHomePath,
    string ConfigPath,
    bool CodexHomeExists,
    bool ConfigExists,
    bool AuthenticationStateExists,
    bool HistoryStateExists,
    IReadOnlyList<string> TopLevelConfigKeys,
    IReadOnlyList<string> ManagedKeysPresent);

