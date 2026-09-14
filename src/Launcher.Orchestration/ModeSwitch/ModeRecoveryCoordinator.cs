using Launcher.ChatGPT.Configuration;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;

namespace Launcher.Orchestration.ModeSwitch;

public sealed class ModeRecoveryCoordinator(
    ISettingsStore settingsStore,
    ChatGptConfigTransactionService configTransactionService,
    LauncherDataPaths dataPaths)
{
    public async Task<ModeRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dataPaths.RecoveryFile))
        {
            return new ModeRecoveryResult(false, false, null);
        }

        await using var transactionLock = await ModeSwitchFileLock.AcquireAsync(
            dataPaths.ModeSwitchLockFile,
            cancellationToken).ConfigureAwait(false);
        var recovery = await configTransactionService.RecoverInterruptedAsync(
            dataPaths.RecoveryFile,
            cancellationToken).ConfigureAwait(false);
        var changed = recovery.Changed;
        var snapshot = recovery.Snapshot;
        if (recovery.PendingSettings is not null)
        {
            await settingsStore.SaveAsync(recovery.PendingSettings, cancellationToken).ConfigureAwait(false);
            snapshot = await configTransactionService.CommitLauncherSettingsAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        return new ModeRecoveryResult(true, changed, snapshot);
    }
}
