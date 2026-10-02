using System.Text.Json;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.Startup;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;

namespace Launcher.Tests;

public sealed class LegacySandboxRecoveryTests
{
    private static readonly string[] PermissionKeys =
        ["default_permissions", "sandbox_mode", "sandbox_workspace_write", "permissions.egg_launcher_active"];

    [Theory]
    [InlineData("restore")]
    [InlineData("recover")]
    [InlineData("uninstall")]
    [InlineData("local")]
    public async Task RestoredLegacyRecord_RepairsOnlyOwnedPermissions(string entry)
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        if (entry == "restore") await fixture.Service.RestoreOpenAIAsync(fixture.Paths.RecoveryFile);
        if (entry == "recover") await fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile);
        if (entry == "uninstall")
        {
            using var store = new JsonSettingsStore(fixture.Paths.SettingsFile);
            await store.SaveAsync(new LauncherSettings { SelectedMode = ProviderMode.OpenAI });
            await new UninstallPreparationService(store, new Detector(), fixture.Service, fixture.Paths, new RunEntries())
                .PrepareAsync(Path.Combine(fixture.Root, "Launcher.App.exe"));
        }
        if (entry == "local")
        {
            await fixture.Service.ApplyLocalAsync(fixture.Request with
            {
                SandboxSettings = new ModelSandboxSettings { NetworkAccess = SandboxNetworkAccess.Disabled }
            }, fixture.Paths);
        }
        var text = await File.ReadAllTextAsync(fixture.Config);
        Assert.Contains("approval_policy = \"never\"", text);
        Assert.Contains("custom_setting = 42", text);
        if (entry == "local")
        {
            Assert.Contains("network = { enabled = false }", text);
            await fixture.Service.RestoreOpenAIAsync(fixture.Paths.RecoveryFile);
            text = await File.ReadAllTextAsync(fixture.Config);
        }
        Assert.DoesNotContain("egg_launcher_active", text);
        Assert.Contains("default_permissions = \":read-only\"", text);
        Assert.Contains("Local history (offline)", text);
        Assert.Contains("http://127.0.0.1:0/v1/", text);
        Assert.Contains("model = \"official\"", text);
        var once = text;
        await fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile);
        Assert.Equal(once, await File.ReadAllTextAsync(fixture.Config));
    }

    [Fact]
    public async Task LegacyLocalUpdate_UsesNewSettingsAndKeepsOriginalBaseline()
    {
        using var fixture = await Fixture.CreateAsync(restored: false);
        await fixture.Service.UpdateLocalAsync(fixture.Request with
        {
            SandboxSettings = new ModelSandboxSettings { NetworkAccess = SandboxNetworkAccess.Disabled }
        }, fixture.Paths.RecoveryFile);
        await fixture.Service.CommitLocalUpdateAsync(fixture.Paths.RecoveryFile);
        Assert.Contains("network = { enabled = false }", await File.ReadAllTextAsync(fixture.Config));
        await fixture.Service.RestoreOpenAIAsync(fixture.Paths.RecoveryFile);
        var text = await File.ReadAllTextAsync(fixture.Config);
        Assert.Contains("default_permissions = \":read-only\"", text);
        Assert.DoesNotContain("egg_launcher_active", text);
        Assert.Contains("Local history (offline)", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repair_RejectsExternalPermissionChangesWithoutWriting(bool defaultChanged)
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        var text = await File.ReadAllTextAsync(fixture.Config);
        text = defaultChanged
            ? text.Replace("default_permissions = \"egg_launcher_active\"", "default_permissions = \":workspace\"")
            : text.Replace("mode = \"full\"", "mode = \"limited\"");
        await File.WriteAllTextAsync(fixture.Config, text);
        var recovery = await File.ReadAllTextAsync(fixture.Paths.RecoveryFile);
        await Assert.ThrowsAsync<ChatGptConfigConflictException>(() => fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile));
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Config));
        Assert.Equal(recovery, await File.ReadAllTextAsync(fixture.Paths.RecoveryFile));
    }

    [Fact]
    public async Task Repair_MissingBackupDoesNotGuessOriginalPermissions()
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        File.Delete(fixture.Snapshot.BackupPath!);
        var text = await File.ReadAllTextAsync(fixture.Config);
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Service.RestoreOpenAIAsync(fixture.Paths.RecoveryFile));
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Config));
    }

    [Fact]
    public async Task LegacyLocalRestore_RecoversMissingBaselineBeforeRestoring()
    {
        using var fixture = await Fixture.CreateAsync(restored: false);
        await fixture.Service.RestoreOpenAIAsync(fixture.Paths.RecoveryFile);
        var text = await File.ReadAllTextAsync(fixture.Config);
        Assert.DoesNotContain("egg_launcher_active", text);
        Assert.Contains("default_permissions = \":read-only\"", text);
        Assert.Contains("Local history (offline)", text);
    }

    [Fact]
    public async Task Repair_BackupHashMismatchLeavesConfigurationUntouched()
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        var bytes = await ProtectedConfigBackup.ReadAsync(fixture.Snapshot.BackupPath!);
        var replacement = Path.Combine(fixture.Root, "wrong-backup.toml");
        await File.WriteAllTextAsync(replacement, System.Text.Encoding.UTF8.GetString(bytes) + "changed = true\n");
        await File.WriteAllTextAsync(fixture.Paths.RecoveryFile,
            JsonSerializer.Serialize(fixture.Snapshot with { BackupPath = replacement }));
        var text = await File.ReadAllTextAsync(fixture.Config);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile));
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Config));
    }

    [Fact]
    public async Task Repair_FailedConfigCommitCanBeRetriedWithoutLosingBaseline()
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        var text = await File.ReadAllTextAsync(fixture.Config);
        var recovery = await File.ReadAllTextAsync(fixture.Paths.RecoveryFile);
        var failing = new ChatGptConfigTransactionService(new Detector(), (_, _) => throw new IOException("Injected write failure"));
        await Assert.ThrowsAsync<IOException>(() => failing.RecoverInterruptedAsync(fixture.Paths.RecoveryFile));
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Config));
        Assert.Equal(recovery, await File.ReadAllTextAsync(fixture.Paths.RecoveryFile));
        await fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile);
        Assert.DoesNotContain("egg_launcher_active", await File.ReadAllTextAsync(fixture.Config));
    }

    [Fact]
    public async Task Repair_AbsentOriginalPermissionFieldsAreRemoved()
    {
        using var fixture = await Fixture.CreateAsync(restored: true, officialPermissions: false);
        await fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile);
        var text = await File.ReadAllTextAsync(fixture.Config);
        Assert.DoesNotContain("default_permissions", text);
        Assert.DoesNotContain("egg_launcher_active", text);
        Assert.Contains("Local history (offline)", text);
    }

    [Fact]
    public async Task Repair_ClientRunningDoesNotChangeConfigOrRecovery()
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        var text = await File.ReadAllTextAsync(fixture.Config);
        var recovery = await File.ReadAllTextAsync(fixture.Paths.RecoveryFile);
        var service = new ChatGptConfigTransactionService(new RunningDetector());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile));
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Config));
        Assert.Equal(recovery, await File.ReadAllTextAsync(fixture.Paths.RecoveryFile));
    }

    [Fact]
    public async Task Repair_PreservesOriginallyUserOwnedPermissionProfile()
    {
        using var fixture = await Fixture.CreateAsync(restored: true);
        var assignments = new Dictionary<string, string?>(fixture.Snapshot.OriginalAssignments);
        assignments["permissions.egg_launcher_active"] = fixture.Snapshot.AppliedAssignments["permissions.egg_launcher_active"];
        assignments["default_permissions"] = fixture.Snapshot.AppliedAssignments["default_permissions"];
        assignments["sandbox_mode"] = null;
        assignments["sandbox_workspace_write"] = null;
        await File.WriteAllTextAsync(fixture.Paths.RecoveryFile,
            JsonSerializer.Serialize(fixture.Snapshot with { OriginalAssignments = assignments }));
        var text = await File.ReadAllTextAsync(fixture.Config);
        var result = await fixture.Service.RecoverInterruptedAsync(fixture.Paths.RecoveryFile);
        Assert.False(result.Changed);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Config));
    }

    private sealed class Fixture : IDisposable
    {
        public required string Root { get; init; }
        public required string Config { get; init; }
        public required LauncherDataPaths Paths { get; init; }
        public required ChatGptLocalModeRequest Request { get; init; }
        public required ChatGptConfigTransactionService Service { get; init; }
        public required ManagedConfigSnapshot Snapshot { get; init; }

        public static async Task<Fixture> CreateAsync(bool restored, bool officialPermissions = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "EggSandboxTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var config = Path.Combine(root, "config.toml");
            var original = "model = \"official\"\ncustom_setting = 42\n"
                + (officialPermissions ? "default_permissions = \":read-only\"\n" : "");
            await File.WriteAllTextAsync(config, original);
            var catalog = Path.Combine(root, "catalog.json");
            await LocalModelCatalogBuilder.WriteAtomicallyAsync(catalog, new LocalModelCatalogOptions
            {
                Slug = "local",
                DisplayName = "Local",
                ContextWindow = 16384
            });
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var service = new ChatGptConfigTransactionService(new Detector());
            var request = new ChatGptLocalModeRequest
            {
                ConfigPath = config,
                ModelSlug = "local",
                ModelCatalogPath = catalog,
                OpenAIBaseUrl = new Uri("http://127.0.0.1:8080/v1"),
                ContextWindow = 16384,
                AutoCompactTokenLimit = 12288,
                SandboxSettings = new ModelSandboxSettings { NetworkAccess = SandboxNetworkAccess.Full }
            };
            var applied = await service.ApplyLocalAsync(request, paths);
            var assignments = applied.OriginalAssignments.Where(pair => !PermissionKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var legacy = applied with
            {
                OriginalAssignments = assignments,
                Stage = restored ? ConfigTransactionStage.Restored : ConfigTransactionStage.LocalApplied,
                OfficialCompatibilityOwned = restored
            };
            if (restored)
            {
                await service.RestoreOpenAIAsync(paths.RecoveryFile);
                var text = await File.ReadAllTextAsync(config);
                text = officialPermissions
                    ? text.Replace("default_permissions = \":read-only\"", applied.AppliedAssignments["default_permissions"])
                    : applied.AppliedAssignments["default_permissions"] + "\n" + text;
                text = applied.AppliedAssignments["permissions.egg_launcher_active"] + "\n" + text + "\napproval_policy = \"never\"\n";
                await File.WriteAllTextAsync(config, text);
            }
            await File.WriteAllTextAsync(paths.RecoveryFile, JsonSerializer.Serialize(legacy));
            return new Fixture { Root = root, Config = config, Paths = paths, Service = service, Request = request, Snapshot = legacy };
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Detector : IChatGptClientDetector
    {
        public bool IsRunning() => false;
    }

    private sealed class RunningDetector : IChatGptClientDetector
    {
        public bool IsRunning() => true;
    }

    private sealed class RunEntries : IUserRunEntryStore
    {
        public string? Read(string valueName) => null;
        public void Write(string valueName, string command) { }
        public void Delete(string valueName) { }
    }
}
