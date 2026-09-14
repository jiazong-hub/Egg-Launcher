using Launcher.Core.Configuration;

namespace Launcher.ChatGPT.Configuration;

public sealed record ManagedConfigRecoveryResult(
    ManagedConfigSnapshot Snapshot,
    LauncherSettings? PendingSettings,
    bool Changed);
