using Launcher.ChatGPT.Configuration;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Core.Persistence;

namespace Launcher.Orchestration.ModeSwitch;

public sealed class ModeRecoveryCoordinator(
    ISettingsStore settingsStore,
    ChatGptConfigTransactionService configTransactionService,
    LauncherDataPaths dataPaths,
    DiagnosticJournal? diagnostics = null)
{
    public async Task<ModeRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dataPaths.RecoveryFile)) return new ModeRecoveryResult(false, false, null);
        if (diagnostics is not null)
        {
            try { diagnostics.SetDetailed((await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false)).DetailedDiagnosticsEnabled); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return await (diagnostics ?? DiagnosticJournal.None).RunAsync("configuration_recovery", null,
            operation => RecoverCoreAsync(operation, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<ModeRecoveryResult> RecoverCoreAsync(DiagnosticOperation operation,
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
        operation.Set("changed", changed);
        var snapshot = recovery.Snapshot;
        if (recovery.PendingSettings is not null)
        {
            await operation.StageAsync("reconcile_launcher_settings").ConfigureAwait(false);
            // A transaction restores its mode metadata, not a later user preference.
            var current = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var pending = recovery.PendingSettings with { LocalRequiresOpenAiAuth = current.LocalRequiresOpenAiAuth };
            await settingsStore.SaveAsync(pending, cancellationToken).ConfigureAwait(false);
            snapshot = await configTransactionService.CommitLauncherSettingsAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        operation.Set("changed", changed);
        operation.Set("transactionStage", snapshot?.Stage.ToString());
        if (!changed) await operation.CompleteAsync("unchanged").ConfigureAwait(false);
        return new ModeRecoveryResult(true, changed, snapshot);
    }
}
