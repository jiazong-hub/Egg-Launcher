using Launcher.Core.Configuration;
using Launcher.Models.Profiles;
using Launcher.Scripts.Batch;

namespace Launcher.Orchestration.Models;

/// <summary>Unregister a model while retaining its parameter files and validated resources.</summary>
public static class ModelListRemoval
{
    public static bool IsApplied(LauncherSettings settings, string modelId) =>
        settings.SelectedMode == ProviderMode.Local
        && string.Equals(settings.SelectedModelId, modelId, StringComparison.Ordinal);

    private static void EnsureCanRemove(LauncherSettings settings, string modelId)
    {
        if (IsApplied(settings, modelId))
            throw new InvalidOperationException("请先实际切换到在线模式或另一个本地模型，再移除此模型 / "
                + "Switch to online mode or another local model before removing this model.");
    }

    public static async Task RemoveAsync(ModelProfile profile, string runtimeRoot, string routerPresetPath,
        Func<LauncherSettings> getSettings,
        Func<Func<LauncherSettings, LauncherSettings>, CancellationToken, Task<LauncherSettings>> mutateSettings,
        CancellationToken token = default)
    {
        EnsureCanRemove(getSettings(), profile.Id);
        BatchScriptGenerator.EnsureCanWrite(runtimeRoot, profile.Id);
        await new ModelArtifactTransaction().ExecuteAsync(runtimeRoot, profile.Id, routerPresetPath, false,
            async ct =>
            {
                EnsureCanRemove(getSettings(), profile.Id);
                BatchScriptGenerator.DeleteOwned(runtimeRoot, profile.Id);
                new JsonModelProfileStore().DeleteOwnedProfileFiles(runtimeRoot, profile.Id);
                // Commit settings last. A failed settings write rolls back only registration/BAT;
                // preserved user JSON and shared templates never enter the removal transaction.
                await mutateSettings(settings =>
                {
                    EnsureCanRemove(settings, profile.Id);
                    return settings.PendingMode == ProviderMode.Local && settings.PendingModelId == profile.Id
                        ? settings with
                        {
                            PendingMode = null,
                            PendingModelId = null,
                            PendingModelRelativePath = null,
                            PendingModelDisplayName = null,
                            PendingSelectionInitialized = true,
                        }
                        : settings;
                }, ct).ConfigureAwait(false);
                return true;
            }, token, registrationRemoval: true).ConfigureAwait(false);
    }
}
