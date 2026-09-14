using Launcher.ChatGPT.Configuration;

namespace Launcher.Orchestration.ModeSwitch;

public sealed record ModeRecoveryResult(
    bool RecoveryRecordFound,
    bool Changed,
    ManagedConfigSnapshot? Snapshot);
