using Launcher.Models.Profiles;
using Launcher.Scripts.Batch;

namespace Launcher.Orchestration.Models;

public sealed class ModelArtifactWriter(
    JsonModelProfileStore profileStore,
    ModelArtifactTransaction transaction)
{
    // Only writes files. The caller guards active sessions; startup owns service activation.
    public Task<bool> SaveAsync(
        ModelProfile profile,
        string runtimeRoot,
        string presetPath,
        string catalogPath,
        bool updateActiveConfiguration,
        CancellationToken cancellationToken = default) =>
        transaction.ExecuteAsync(
            runtimeRoot,
            profile.Id,
            presetPath,
            updateActiveConfiguration,
            async token =>
            {
                BatchScriptGenerator.EnsureCanWrite(runtimeRoot, profile.Id);
                _ = BatchScriptGenerator.Generate(profile, runtimeRoot);
                await profileStore.SaveAsync(runtimeRoot, profile, token).ConfigureAwait(false);
                await BatchScriptGenerator.WriteOwnedAsync(runtimeRoot, profile, token).ConfigureAwait(false);
                if (updateActiveConfiguration)
                {
                    await LocalModelConfigurationWriter.WriteAsync(
                        profile, runtimeRoot, presetPath, catalogPath, token).ConfigureAwait(false);
                }
                return true;
            },
            cancellationToken,
            localModelCatalogPath: catalogPath);
}
