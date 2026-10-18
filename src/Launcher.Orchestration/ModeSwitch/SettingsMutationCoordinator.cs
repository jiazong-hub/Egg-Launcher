using Launcher.Core.Configuration;
using Launcher.Core.Persistence;

namespace Launcher.Orchestration.ModeSwitch;

/// <summary>Preference edits share the same read/write boundary as mode and endpoint changes.</summary>
public static class SettingsMutationCoordinator
{
    public static async Task<LauncherSettings> UpdateAsync(
        ISettingsStore store,
        LauncherDataPaths paths,
        Func<LauncherSettings, LauncherSettings> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mutation);
        await using var transactionLock = await ModeSwitchFileLock.AcquireAsync(
            paths.ModeSwitchLockFile, cancellationToken).ConfigureAwait(false);
        var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var updated = mutation(current);
        if (updated != current)
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
