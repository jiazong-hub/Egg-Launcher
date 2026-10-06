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
            var catalogPath = Path.Combine(root, "state", "catalog.json");
            var originals = new Dictionary<string, string>
            {
                [profilePath] = "old-profile",
                [backupPath] = "old-backup",
                [batchPath] = "old-batch",
                [presetPath] = "old-preset",
                [catalogPath] = "old-catalog",
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
                }, localModelCatalogPath: catalogPath));

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

    [Fact]
    public async Task ExecuteAsync_RollsBackTemplateBackupValidationAndStagedFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var directory = Path.Combine(root, "scripts", "templates");
            Directory.CreateDirectory(directory);
            var backup = Path.Combine(directory, "model.embedded.jinja");
            var cache = Path.Combine(directory, "model.validation.json");
            var staged = Path.Combine(directory, "model.staged.codex-compatible.jinja");
            await File.WriteAllTextAsync(backup, "original backup");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ModelArtifactTransaction().ExecuteAsync<bool>(
                root, "model", Path.Combine(root, "router.ini"), false, async token =>
                {
                    await File.WriteAllTextAsync(backup, "changed", token);
                    await File.WriteAllTextAsync(cache, "cache", token);
                    await File.WriteAllTextAsync(staged, "candidate", token);
                    throw new InvalidOperationException("injected failure");
                }, additionalTargets: [staged]));
            Assert.Equal("original backup", await File.ReadAllTextAsync(backup));
            Assert.False(File.Exists(cache));
            Assert.False(File.Exists(staged));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsAdditionalPathsOutsideTemplates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var called = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ModelArtifactTransaction().ExecuteAsync(
                root, "model", Path.Combine(root, "router.ini"), false, _ => { called = true; return Task.FromResult(true); },
                additionalTargets: [Path.Combine(root, "outside.txt")]));
            Assert.False(called);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
