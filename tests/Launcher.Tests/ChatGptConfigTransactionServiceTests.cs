using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using System.Text.Json;
using Tomlyn.Parsing;

namespace Launcher.Tests;

public sealed class ChatGptConfigTransactionServiceTests
{
    [Fact]
    public async Task ApplyAndRestore_PreservesUnknownConfigAndAuthenticationFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var codexHome = Path.Combine(root, ".codex");
            Directory.CreateDirectory(codexHome);
            var configPath = Path.Combine(codexHome, "config.toml");
            var authPath = Path.Combine(codexHome, "auth.json");
            var catalogPath = Path.Combine(root, "local-models.json");
            const string authContent = "{\"token\":\"must-remain-untouched\"}";
            await File.WriteAllTextAsync(
                configPath,
                 "model = \"official-model\"\r\nmodel_provider = \"official-provider\"\r\n"
                 + "openai_base_url = \"https://official.example/v1\"\r\n"
                 + "model_providers.user_proxy = { name = \"User proxy\", base_url = \"https://user.example/v1\", wire_api = \"responses\" }\r\n"
                + "custom_setting = 42\r\n\r\n[projects.sample]\r\ntrust_level = \"trusted\"\r\n");
            await File.WriteAllTextAsync(authPath, authContent);
            await WriteCatalogAsync(catalogPath, "local-coder");

            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(IsRunningValue: false));
            var applied = await service.ApplyLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-coder",
                    ModelCatalogPath = catalogPath,
                    OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                },
                paths);

            var localText = await File.ReadAllTextAsync(configPath);
            Assert.Equal(ConfigTransactionStage.LocalApplied, applied.Stage);
            Assert.Contains("model = \"local-coder\"", localText);
            Assert.Contains("model_provider = \"chatgpt_local_launcher\"", localText);
            Assert.Contains("model_providers.chatgpt_local_launcher =", localText);
            Assert.Contains("name = \"Local llama.cpp\"", localText);
            Assert.Contains("base_url = \"http://127.0.0.1:8080/v1/\"", localText);
            Assert.Contains("wire_api = \"responses\"", localText);
            Assert.Contains("requires_openai_auth = true", localText);
            Assert.Contains("supports_websockets = false", localText);
            Assert.DoesNotContain("supports_websockets = false }}", localText, StringComparison.Ordinal);
            Assert.False(SyntaxParser.ParseStrict(localText, "generated-local.toml", validate: true).HasErrors);
            Assert.DoesNotContain("openai_base_url =", localText);
            Assert.Contains("model_context_window = 16384", localText);
            Assert.Contains("model_auto_compact_token_limit = 12288", localText);
            Assert.DoesNotContain("model_auto_compact_token_limit_scope", localText);
            Assert.DoesNotContain("model_reasoning_effort", localText);
            Assert.DoesNotContain("approval_policy =", localText);
            Assert.DoesNotContain("approvals_reviewer =", localText);
            Assert.Contains("custom_setting = 42", localText);
            Assert.Contains("[projects.sample]", localText);
            Assert.Contains("model_providers.user_proxy =", localText);
            Assert.Equal(authContent, await File.ReadAllTextAsync(authPath));
            Assert.True(File.Exists(paths.RecoveryFile));
            Assert.NotNull(applied.BackupPath);
            Assert.True(File.Exists(applied.BackupPath));
            Assert.EndsWith(".dpapi", applied.BackupPath, StringComparison.OrdinalIgnoreCase);
            var encryptedBackup = await File.ReadAllBytesAsync(applied.BackupPath);
            Assert.DoesNotContain(
                "official-model",
                System.Text.Encoding.UTF8.GetString(encryptedBackup),
                StringComparison.Ordinal);
            var decryptedBackup = await ProtectedConfigBackup.ReadAsync(applied.BackupPath);
            Assert.Contains(
                "model = \"official-model\"",
                System.Text.Encoding.UTF8.GetString(decryptedBackup),
                StringComparison.Ordinal);

            localText = localText.Replace("[projects.sample]", "theme = \"dark\"\r\n\r\n[projects.sample]", StringComparison.Ordinal);
            await File.WriteAllTextAsync(configPath, localText);

            var restored = await service.RestoreOpenAIAsync(paths.RecoveryFile);
            var restoredText = await File.ReadAllTextAsync(configPath);

            Assert.Equal(ConfigTransactionStage.Restored, restored.Stage);
            Assert.Contains("model = \"official-model\"", restoredText);
            Assert.Contains("model_provider = \"official-provider\"", restoredText);
            Assert.Contains("openai_base_url = \"https://official.example/v1\"", restoredText);
            Assert.DoesNotContain("model_catalog_json", restoredText);
            Assert.DoesNotContain("model_context_window", restoredText);
            Assert.DoesNotContain("model_auto_compact_token_limit", restoredText);
            Assert.DoesNotContain("model_reasoning_effort", restoredText);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"", restoredText);
            Assert.Contains("base_url = \"http://127.0.0.1:0/v1/\"", restoredText);
            Assert.DoesNotContain("approval_policy =", restoredText);
            Assert.DoesNotContain("approvals_reviewer =", restoredText);
            Assert.Contains("model_providers.user_proxy =", restoredText);
            Assert.Contains("custom_setting = 42", restoredText);
            Assert.Contains("theme = \"dark\"", restoredText);
            Assert.Contains("[projects.sample]", restoredText);
            Assert.Equal(authContent, await File.ReadAllTextAsync(authPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("model_providers.chatgpt_local_launcher = { name = \"user owned\" }\n")]
    [InlineData("model_providers.\"chatgpt_local_launcher\" = { name = \"user owned\" }\n")]
    [InlineData("[model_providers.chatgpt_local_launcher]\nname = \"user owned\"\n")]
    [InlineData("[model_providers]\nchatgpt_local_launcher = { name = \"user owned\" }\n")]
    public async Task ApplyLocal_WhenProviderIdAlreadyExists_RefusesWithoutChangingFiles(string original)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            await File.WriteAllTextAsync(configPath, original);
            await WriteCatalogAsync(catalogPath, "local-coder");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));

            var exception = await Assert.ThrowsAsync<ChatGptConfigConflictException>(() =>
                service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-coder"), paths));

            Assert.Contains("chatgpt_local_launcher", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
            Assert.False(File.Exists(paths.RecoveryFile));
            Assert.False(Directory.Exists(paths.BackupsDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyLocal_WhenOriginalTomlIsInvalid_RefusesBeforeCreatingRecoveryArtifacts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            const string invalid = "model = \"official\" }\n";
            await File.WriteAllTextAsync(configPath, invalid);
            await WriteCatalogAsync(catalogPath, "local-coder");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-coder"), paths));

            Assert.DoesNotContain("official", exception.Message, StringComparison.Ordinal);
            Assert.Equal(invalid, await File.ReadAllTextAsync(configPath));
            Assert.False(File.Exists(paths.RecoveryFile));
            Assert.False(Directory.Exists(paths.BackupsDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAndRestore_MultilineStringContainingTableLikeText_RemainsLosslessAndValid()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            const string original =
                "description = \"\"\"\r\n"
                + "[this-is-text-not-a-table]\r\n"
                + "kept exactly\r\n"
                + "\"\"\"\r\n"
                + "model = \"official\" # keep this comment\r\n"
                + "\r\n"
                + "[projects.sample]\r\n"
                + "trust_level = \"trusted\"\r\n";
            await File.WriteAllTextAsync(configPath, original);
            await WriteCatalogAsync(catalogPath, "local-coder");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));

            await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-coder"), paths);
            var localText = await File.ReadAllTextAsync(configPath);

            Assert.Contains("[this-is-text-not-a-table]", localText, StringComparison.Ordinal);
            Assert.Contains("description = \"\"\"\r\n", localText, StringComparison.Ordinal);
            Assert.Contains("[projects.sample]", localText, StringComparison.Ordinal);
            Assert.False(SyntaxParser.ParseStrict(localText, "local.toml", validate: true).HasErrors);

            await service.RestoreOpenAIAsync(paths.RecoveryFile);

            var restoredText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("description = \"\"\"\r\n[this-is-text-not-a-table]\r\nkept exactly\r\n\"\"\"", restoredText);
            Assert.Contains("model = \"official\" # keep this comment", restoredText);
            Assert.Contains("[projects.sample]\r\ntrust_level = \"trusted\"", restoredText);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"", restoredText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAndRestore_PreservesExplicitOfficialPermissionDefaults()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            await File.WriteAllTextAsync(
                configPath,
                "model = \"official-model\"\napproval_policy = \"never\"\napprovals_reviewer = \"user\"\n");
            await WriteCatalogAsync(catalogPath, "local-coder");

            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(IsRunningValue: false));
            await service.ApplyLocalAsync(
                LocalRequest(configPath, catalogPath, "local-coder"),
                paths);

            var localText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("approval_policy = \"never\"", localText);
            Assert.Contains("approvals_reviewer = \"user\"", localText);

            await service.RestoreOpenAIAsync(paths.RecoveryFile);
            var restoredText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("approval_policy = \"never\"", restoredText);
            Assert.Contains("approvals_reviewer = \"user\"", restoredText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyLocal_AllowsDesktopPermissionChangesAndRestoresOfficialDefaults()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            await File.WriteAllTextAsync(
                configPath,
                "model = \"official-model\"\napproval_policy = \"never\"\napprovals_reviewer = \"user\"\n");
            await WriteCatalogAsync(catalogPath, "local-coder");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));

            await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-coder"), paths);

            var localText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("approval_policy = \"never\"", localText, StringComparison.Ordinal);
            Assert.Contains("approvals_reviewer = \"user\"", localText, StringComparison.Ordinal);
            localText = localText
                .Replace("approval_policy = \"never\"", "approval_policy = \"on-request\"", StringComparison.Ordinal)
                .Replace("approvals_reviewer = \"user\"", "approvals_reviewer = \"auto_review\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(configPath, localText);

            await service.RestoreOpenAIAsync(paths.RecoveryFile);
            var restoredText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("approval_policy = \"never\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("approvals_reviewer = \"user\"", restoredText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAndRestore_RoundTripsCompleteOfficialModelPreset()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            const string original =
                "model = \"gpt-5.6-sol\"\n"
                + "model_reasoning_effort = \"xhigh\"\n"
                + "model_reasoning_summary = \"detailed\"\n"
                + "model_supports_reasoning_summaries = true\n"
                + "model_verbosity = \"high\"\n"
                + "service_tier = \"default\"\n"
                + "model_context_window = 1050000\n"
                + "model_auto_compact_token_limit = 900000\n"
                + "model_auto_compact_token_limit_scope = \"body_after_prefix\"\n";
            await File.WriteAllTextAsync(configPath, original);
            await WriteCatalogAsync(catalogPath, "local-coder");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));

            await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-coder"), paths);

            var localText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model = \"local-coder\"", localText, StringComparison.Ordinal);
            Assert.DoesNotContain("model_reasoning_effort", localText, StringComparison.Ordinal);
            Assert.Contains("model_context_window = 16384", localText, StringComparison.Ordinal);
            Assert.Contains("model_auto_compact_token_limit = 12288", localText, StringComparison.Ordinal);
            Assert.DoesNotContain("model_auto_compact_token_limit_scope", localText, StringComparison.Ordinal);
            Assert.DoesNotContain("model_reasoning_summary", localText, StringComparison.Ordinal);
            Assert.DoesNotContain("model_supports_reasoning_summaries", localText, StringComparison.Ordinal);
            Assert.DoesNotContain("model_verbosity", localText, StringComparison.Ordinal);
            Assert.DoesNotContain("service_tier", localText, StringComparison.Ordinal);

            // Desktop can normalize its local model-preset fields without blocking
            // restoration of the exact official model preset.
            localText += "model_reasoning_effort = \"minimal\"\n";
            await File.WriteAllTextAsync(configPath, localText);

            await service.RestoreOpenAIAsync(paths.RecoveryFile);

            var restoredText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model = \"gpt-5.6-sol\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("model_reasoning_effort = \"xhigh\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("model_reasoning_summary = \"detailed\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("model_supports_reasoning_summaries = true", restoredText, StringComparison.Ordinal);
            Assert.Contains("model_verbosity = \"high\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("service_tier = \"default\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("model_context_window = 1050000", restoredText, StringComparison.Ordinal);
            Assert.Contains("model_auto_compact_token_limit = 900000", restoredText, StringComparison.Ordinal);
            Assert.Contains(
                "model_auto_compact_token_limit_scope = \"body_after_prefix\"",
                restoredText,
                StringComparison.Ordinal);
            Assert.DoesNotContain("model = \"local-coder\"", restoredText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_WhenManagedFieldChanged_StopsWithConflict()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            await File.WriteAllTextAsync(configPath, "model = \"official-model\"\n");
            await WriteCatalogAsync(catalogPath, "local-coder");

            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(IsRunningValue: false));
            await service.ApplyLocalAsync(
                new ChatGptLocalModeRequest
                {
                    ConfigPath = configPath,
                    ModelSlug = "local-coder",
                    ModelCatalogPath = catalogPath,
                    OpenAIBaseUrl = new Uri("http://localhost:8080/v1"),
                    ContextWindow = 16_384,
                    AutoCompactTokenLimit = 12_288,
                },
                paths);

            var changed = (await File.ReadAllTextAsync(configPath))
                .Replace("model = \"local-coder\"", "model = \"changed-elsewhere\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(configPath, changed);

            await Assert.ThrowsAsync<ChatGptConfigConflictException>(() =>
                service.RestoreOpenAIAsync(paths.RecoveryFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_WhenEncryptedBackupWasTampered_StopsBeforeChangingConfig()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "local-models.json");
            await File.WriteAllTextAsync(configPath, "model = \"official-model\"\n");
            await WriteCatalogAsync(catalogPath, "local-coder");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(IsRunningValue: false));
            var applied = await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-coder"), paths);
            var localText = await File.ReadAllTextAsync(configPath);
            Assert.NotNull(applied.BackupPath);
            await File.AppendAllTextAsync(applied.BackupPath, "tampered");

            await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreOpenAIAsync(paths.RecoveryFile));

            Assert.Equal(localText, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyLocal_WhenClientIsRunning_DoesNotWriteAnything()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(IsRunningValue: true));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApplyLocalAsync(
                    new ChatGptLocalModeRequest
                    {
                        ConfigPath = configPath,
                        ModelSlug = "local-coder",
                        ModelCatalogPath = Path.Combine(root, "local-models.json"),
                        OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                        ContextWindow = 16_384,
                        AutoCompactTokenLimit = 12_288,
                    },
                    paths));

            Assert.False(File.Exists(configPath));
            Assert.False(File.Exists(paths.RecoveryFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateLegacyBackups_EncryptsThenUpdatesRecoveryReference()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            await File.WriteAllTextAsync(configPath, "model = \"official\"\n");
            await WriteCatalogAsync(catalogPath, "local-a");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));
            var applied = await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-a"), paths);
            Assert.NotNull(applied.BackupPath);
            var plaintext = await ProtectedConfigBackup.ReadAsync(applied.BackupPath);
            var legacyPath = Path.ChangeExtension(applied.BackupPath, ".bak");
            await File.WriteAllBytesAsync(legacyPath, plaintext);
            File.Delete(applied.BackupPath);
            await File.WriteAllTextAsync(
                paths.RecoveryFile,
                JsonSerializer.Serialize(applied with { BackupPath = legacyPath }));

            var result = await service.MigrateLegacyBackupsAsync(paths);

            Assert.Equal(1, result.MigratedCount);
            Assert.Equal(0, result.RemainingPlaintextCount);
            Assert.False(File.Exists(legacyPath));
            var recovery = JsonSerializer.Deserialize<ManagedConfigSnapshot>(
                await File.ReadAllTextAsync(paths.RecoveryFile));
            Assert.NotNull(recovery);
            Assert.EndsWith(".dpapi", recovery.BackupPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(plaintext, await ProtectedConfigBackup.ReadAsync(recovery.BackupPath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateLocalAndRestore_PreservesOriginalOpenAiRecoveryPoint()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var firstCatalog = Path.Combine(root, "first.json");
            var secondCatalog = Path.Combine(root, "second.json");
            await File.WriteAllTextAsync(
                configPath,
                "model = \"official-model\"\ncustom_setting = true\n");
            await WriteCatalogAsync(firstCatalog, "local-a");
            await WriteCatalogAsync(secondCatalog, "local-b");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));
            var first = await service.ApplyLocalAsync(
                LocalRequest(configPath, firstCatalog, "local-a"),
                paths);

            var withUnknownSetting = (await File.ReadAllTextAsync(configPath))
                .Replace("custom_setting = true", "custom_setting = true\ntheme = \"dark\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(configPath, withUnknownSetting);
            var updated = await service.UpdateLocalAsync(
                LocalRequest(configPath, secondCatalog, "local-b"),
                paths.RecoveryFile);

            Assert.Equal(first.TransactionId, updated.TransactionId);
            Assert.Equal(first.OriginalAssignments.Count, updated.OriginalAssignments.Count);
            foreach (var assignment in first.OriginalAssignments)
            {
                Assert.True(updated.OriginalAssignments.TryGetValue(assignment.Key, out var value));
                Assert.Equal(assignment.Value, value);
            }
            Assert.Equal(ConfigTransactionStage.LocalUpdateApplied, updated.Stage);
            Assert.NotNull(updated.PreviousAppliedAssignments);
            var localBText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model = \"local-b\"", localBText, StringComparison.Ordinal);
            Assert.Contains("theme = \"dark\"", localBText, StringComparison.Ordinal);

            var committed = await service.CommitLocalUpdateAsync(paths.RecoveryFile);
            Assert.Equal(ConfigTransactionStage.LocalApplied, committed.Stage);
            Assert.Null(committed.PreviousAppliedAssignments);

            await service.RestoreOpenAIAsync(paths.RecoveryFile);
            var restoredText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model = \"official-model\"", restoredText, StringComparison.Ordinal);
            Assert.Contains("custom_setting = true", restoredText, StringComparison.Ordinal);
            Assert.Contains("theme = \"dark\"", restoredText, StringComparison.Ordinal);
            Assert.DoesNotContain("local-a", restoredText, StringComparison.Ordinal);
            Assert.DoesNotContain("local-b", restoredText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackLocalUpdate_RestoresPreviousLocalFields()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var firstCatalog = Path.Combine(root, "first.json");
            var secondCatalog = Path.Combine(root, "second.json");
            await File.WriteAllTextAsync(configPath, "model = \"official\"\n");
            await WriteCatalogAsync(firstCatalog, "local-a");
            await WriteCatalogAsync(secondCatalog, "local-b");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));
            await service.ApplyLocalAsync(LocalRequest(configPath, firstCatalog, "local-a"), paths);
            await service.UpdateLocalAsync(
                LocalRequest(configPath, secondCatalog, "local-b"),
                paths.RecoveryFile);

            var rolledBack = await service.RollbackLocalUpdateAsync(paths.RecoveryFile);

            Assert.Equal(ConfigTransactionStage.LocalApplied, rolledBack.Stage);
            Assert.Null(rolledBack.PreviousAppliedAssignments);
            var text = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model = \"local-a\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("model = \"local-b\"", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreOpenAi_WhenInitialTransactionStoppedBeforeConfigWrite_AcceptsOriginalFields()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            const string original = "model = \"official\"\n";
            await File.WriteAllTextAsync(configPath, original);
            await WriteCatalogAsync(catalogPath, "local-a");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));
            var applied = await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-a"), paths);
            await File.WriteAllTextAsync(configPath, original);
            await File.WriteAllTextAsync(
                paths.RecoveryFile,
                JsonSerializer.Serialize(applied with { Stage = ConfigTransactionStage.Prepared }));

            var restored = await service.RestoreOpenAIAsync(paths.RecoveryFile);

            Assert.Equal(ConfigTransactionStage.Restored, restored.Stage);
            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreOpenAi_WhenOriginalConfigDidNotExist_KeepsHistoryProviderOnly()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "codex", "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            await WriteCatalogAsync(catalogPath, "local-a");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));
            await service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-a"), paths);

            await service.RestoreOpenAIAsync(paths.RecoveryFile);

            var restoredText = await File.ReadAllTextAsync(configPath);
            Assert.Contains("model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\"", restoredText);
            Assert.DoesNotContain("model_provider =", restoredText);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyLocal_WhenConfigChangesAfterRead_DoesNotOverwriteExternalChange()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            const string original = "model = \"official\"\ncustom_setting = true\n";
            await File.WriteAllTextAsync(configPath, original);
            await WriteCatalogAsync(catalogPath, "local-a");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            var hookCalls = 0;
            var service = new ChatGptConfigTransactionService(
                new FakeClientDetector(false),
                async (path, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref hookCalls) == 1)
                    {
                        await File.AppendAllTextAsync(path, "external_setting = \"preserved\"\n", cancellationToken);
                    }
                });

            var exception = await Assert.ThrowsAsync<ChatGptConfigConflictException>(() =>
                service.ApplyLocalAsync(LocalRequest(configPath, catalogPath, "local-a"), paths));

            Assert.Contains("未覆盖外部更改", exception.Message, StringComparison.Ordinal);
            var actual = await File.ReadAllTextAsync(configPath);
            Assert.StartsWith(original, actual, StringComparison.Ordinal);
            Assert.Contains("external_setting = \"preserved\"", actual, StringComparison.Ordinal);
            Assert.DoesNotContain("chatgpt_local_launcher", actual, StringComparison.Ordinal);
            var recovery = JsonSerializer.Deserialize<ManagedConfigSnapshot>(
                await File.ReadAllTextAsync(paths.RecoveryFile));
            Assert.NotNull(recovery);
            Assert.Equal(ConfigTransactionStage.Restored, recovery.Stage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyLocal_WhenAnotherProcessOwnsConfigLock_WaitsWithoutWriting()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(root, "config.toml");
            var catalogPath = Path.Combine(root, "catalog.json");
            const string original = "model = \"official\"\n";
            await File.WriteAllTextAsync(configPath, original);
            await WriteCatalogAsync(catalogPath, "local-a");
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "launcher-data"));
            Directory.CreateDirectory(paths.Root);
            await using var externalLock = new FileStream(
                paths.ConfigTransactionLockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var service = new ChatGptConfigTransactionService(new FakeClientDetector(false));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ApplyLocalAsync(
                    LocalRequest(configPath, catalogPath, "local-a"),
                    paths,
                    cancellation.Token));

            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
            Assert.False(File.Exists(paths.RecoveryFile));
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

    private static Task WriteCatalogAsync(string path, string slug) =>
        LocalModelCatalogBuilder.WriteAtomicallyAsync(
            path,
            new LocalModelCatalogOptions
            {
                Slug = slug,
                DisplayName = slug,
                ContextWindow = 8192,
            });

    private static ChatGptLocalModeRequest LocalRequest(
        string configPath,
        string catalogPath,
        string slug) => new()
        {
            ConfigPath = configPath,
            ModelSlug = slug,
            ModelCatalogPath = catalogPath,
            OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
            ContextWindow = 16_384,
            AutoCompactTokenLimit = 12_288,
        };

    private sealed record FakeClientDetector(bool IsRunningValue) : IChatGptClientDetector
    {
        public bool IsRunning() => IsRunningValue;
    }
}
