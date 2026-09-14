using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Scripts.Batch;

namespace Launcher.Tests;

public sealed class ModelArtifactTransactionTests
{
    [Fact]
    public async Task ExecuteAsync_WhenOperationFails_RestoresEveryArtifact()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string profileId = "local-model";
            var profilePath = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(root), profileId + ".json");
            var backupPath = profilePath + ".bak";
            var batchPath = BatchScriptGenerator.GetOutputPath(root, profileId);
            var templatePath = Path.Combine(root, "scripts", "templates", profileId + ".codex-compatible.jinja");
            var presetPath = Path.Combine(root, "state", "models.ini");
            var originals = new Dictionary<string, string>
            {
                [profilePath] = "old-profile",
                [backupPath] = "old-backup",
                [batchPath] = "old-batch",
                [presetPath] = "old-preset",
            };
            foreach (var pair in originals)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                await File.WriteAllTextAsync(pair.Key, pair.Value);
            }

            var transaction = new ModelArtifactTransaction();
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ExecuteAsync<bool>(
                root,
                profileId,
                presetPath,
                includeRouterPreset: true,
                async cancellationToken =>
                {
                    foreach (var path in originals.Keys)
                    {
                        await File.WriteAllTextAsync(path, "new", cancellationToken);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
                    await File.WriteAllTextAsync(templatePath, "generated", cancellationToken);
                    throw new InvalidOperationException("injected failure");
                }));

            foreach (var pair in originals)
            {
                Assert.Equal(pair.Value, await File.ReadAllTextAsync(pair.Key));
            }

            Assert.False(File.Exists(templatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
