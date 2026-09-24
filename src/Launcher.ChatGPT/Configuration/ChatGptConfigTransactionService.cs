using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Security;
using Launcher.Models.Profiles;

namespace Launcher.ChatGPT.Configuration;

public sealed class ChatGptConfigTransactionService
{
    private const string LocalProviderId = "chatgpt_local_launcher";
    private const string LocalProviderKey = "model_providers.chatgpt_local_launcher";
    private const string OfflineCompatibilityProvider =
        "model_providers.chatgpt_local_launcher = { name = \"Local history (offline)\", "
        + "base_url = \"http://127.0.0.1:0/v1/\", wire_api = \"responses\", "
        + "requires_openai_auth = false, supports_websockets = false }";
    private const string LauncherPermissionProfileName = "egg_launcher_active";
    private const string LauncherPermissionProfileKey = $"permissions.{LauncherPermissionProfileName}";

    private static readonly string[] SandboxManagedKeys =
    [
        "default_permissions",
        "sandbox_mode",
        "sandbox_workspace_write",
        LauncherPermissionProfileKey,
    ];

    private static readonly string[] ManagedKeys =
    [
        "model",
        "model_provider",
        "model_catalog_json",
        "openai_base_url",
        "profile",
        "model_context_window",
        "model_auto_compact_token_limit",
        "model_auto_compact_token_limit_scope",
        "model_reasoning_effort",
        "model_reasoning_summary",
        "model_supports_reasoning_summaries",
        "model_verbosity",
        "service_tier",
        "approval_policy",
        "approvals_reviewer",
        LocalProviderKey,
        .. SandboxManagedKeys,
    ];

    // Approval fields remain snapshot-only: they are controlled by ChatGPT Desktop
    // and are not sandbox permissions. SandboxManagedKeys are owned while Local mode
    // is active so changing or restoring model-specific sandbox settings is atomic.
    private static readonly string[] OwnershipKeys =
    [
        "model",
        "model_provider",
        "model_catalog_json",
        "openai_base_url",
        "profile",
        "model_context_window",
        LocalProviderKey,
        .. SandboxManagedKeys,
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IChatGptClientDetector clientDetector;
    private readonly Func<string, CancellationToken, Task>? _beforeConfigCommit;

    public ChatGptConfigTransactionService(IChatGptClientDetector clientDetector)
        : this(clientDetector, null)
    {
    }

    internal ChatGptConfigTransactionService(
        IChatGptClientDetector clientDetector,
        Func<string, CancellationToken, Task>? beforeConfigCommit)
    {
        this.clientDetector = clientDetector ?? throw new ArgumentNullException(nameof(clientDetector));
        _beforeConfigCommit = beforeConfigCommit;
    }

    public async Task<ManagedConfigSnapshot> ApplyLocalAsync(
        ChatGptLocalModeRequest request,
        LauncherDataPaths dataPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataPaths);
        ValidateRequest(request);
        EnsureClientClosed();
        await LocalModelCatalogValidator.ValidateSingleModelAsync(
            request.ModelCatalogPath,
            request.ModelSlug,
            cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                dataPaths.ConfigTransactionLockFile,
                cancellationToken).ConfigureAwait(false);
            var configPath = Path.GetFullPath(request.ConfigPath);
            var originalState = await ReadConfigStateAsync(configPath, cancellationToken).ConfigureAwait(false);
            var originalExists = originalState.Exists;
            var originalBytes = originalState.Bytes;
            var originalText = originalState.Text;
            var originalDocument = new TomlRootDocument(originalText);
            var previousSnapshot = File.Exists(dataPaths.RecoveryFile)
                ? await LoadSnapshotAsync(dataPaths.RecoveryFile, cancellationToken).ConfigureAwait(false)
                : null;
            var ownsCompatibilityProvider = previousSnapshot is not null
                && IsOwnedOfficialCompatibility(previousSnapshot, configPath, originalDocument);
            if (previousSnapshot?.Stage == ConfigTransactionStage.Restored
                && previousSnapshot.OfficialCompatibilityOwned
                && !ownsCompatibilityProvider)
            {
                throw new ChatGptConfigConflictException(
                    "启动器保留的历史 Provider 已被修改或删除；已停止切换，避免覆盖外部配置。");
            }

            if (originalDocument.ContainsDefinition(LocalProviderKey)
                && !ownsCompatibilityProvider)
            {
                throw new ChatGptConfigConflictException(
                    $"官方配置已包含 Provider '{LocalProviderId}'，为避免覆盖用户配置，已停止 Local 模式切换。");
            }

            if (request.SandboxSettings is not null
                && originalDocument.ContainsDefinition(LauncherPermissionProfileKey))
            {
                throw new ChatGptConfigConflictException(
                    $"官方配置已包含权限配置 '{LauncherPermissionProfileName}'，为避免覆盖用户设置，已停止沙箱配置切换。");
            }

            if (request.SandboxSettings is not null
                && originalDocument.ContainsDescendantDefinition("sandbox_workspace_write"))
            {
                throw new ChatGptConfigConflictException(
                    "Codex 配置中的 sandbox_workspace_write 含有嵌套字段，无法完整移入模型权限配置；为避免丢失现有规则，已停止切换。");
            }

            var originalAssignments = CaptureAssignments(originalDocument);

            var localDocument = BuildLocalDocument(originalDocument, request, originalAssignments);
            var localText = localDocument.ToString();
            var transactionId = Guid.NewGuid();

            Directory.CreateDirectory(dataPaths.BackupsDirectory);
            string? backupPath = null;
            if (originalExists)
            {
                backupPath = Path.Combine(
                    dataPaths.BackupsDirectory,
                    $"config.toml.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{transactionId:N}.dpapi");
                await ProtectedConfigBackup.WriteAsync(backupPath, originalBytes, cancellationToken)
                    .ConfigureAwait(false);
                var verifiedBackup = await ProtectedConfigBackup.ReadAsync(backupPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(originalBytes),
                        SHA256.HashData(verifiedBackup)))
                {
                    throw new InvalidDataException("加密配置备份校验失败，已停止 Local 模式切换。");
                }

                RotateEncryptedBackups(dataPaths.BackupsDirectory, backupPath);
            }

            var prepared = new ManagedConfigSnapshot
            {
                TransactionId = transactionId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Stage = ConfigTransactionStage.Prepared,
                ConfigPath = configPath,
                OriginalConfigExisted = originalExists,
                OriginalConfigSha256 = ComputeSha256(originalBytes),
                BackupPath = backupPath,
                OriginalAssignments = originalAssignments,
                AppliedAssignments = CaptureAssignments(localDocument),
                OriginalLauncherSettings = request.OriginalLauncherSettings,
                TargetLauncherSettings = request.TargetLauncherSettings,
                SettingsCommitted = request.TargetLauncherSettings is null,
                OfficialCompatibilityOwned = ownsCompatibilityProvider,
                CompatibilityBackupPath = previousSnapshot?.CompatibilityBackupPath,
            };

            var recoveryPrepared = false;
            try
            {
                await WriteJsonAtomicallyAsync(dataPaths.RecoveryFile, prepared, cancellationToken).ConfigureAwait(false);
                recoveryPrepared = true;
                EnsureClientClosed();
                await BeforeConfigCommitAsync(configPath, cancellationToken).ConfigureAwait(false);
                await WriteConfigAtomicallyAsync(
                    configPath,
                    localText,
                    originalState.Revision,
                    cancellationToken).ConfigureAwait(false);

                var applied = prepared with { Stage = ConfigTransactionStage.LocalApplied };
                await WriteJsonAtomicallyAsync(dataPaths.RecoveryFile, applied, cancellationToken).ConfigureAwait(false);
                return applied;
            }
            catch (Exception exception) when (recoveryPrepared)
            {
                try
                {
                    await RollbackPreparedInitialApplicationAsync(
                        dataPaths.RecoveryFile,
                        prepared).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Local 配置应用失败且未能完整回滚，请勿启动 ChatGPT 并检查恢复记录。",
                        exception,
                        rollbackException);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> EnsureOfficialCompatibilityAsync(
        string recoveryPath,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        if (!File.Exists(recoveryPath))
        {
            return false;
        }

        EnsureClientClosed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Stage != ConfigTransactionStage.Restored
                || !snapshot.SettingsCommitted
                || !string.Equals(
                    Path.GetFullPath(snapshot.ConfigPath),
                    Path.GetFullPath(configPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatGptConfigConflictException(
                    "官方配置与启动器恢复记录不一致，已停止历史 Provider 兼容配置。");
            }

            if (!snapshot.AppliedAssignments.TryGetValue(LocalProviderKey, out var appliedProvider)
                || appliedProvider is null
                || !snapshot.OriginalAssignments.TryGetValue(LocalProviderKey, out var originalProvider)
                || originalProvider is not null
                    && (!snapshot.OfficialCompatibilityOwned
                        || !string.Equals(
                            originalProvider,
                            OfflineCompatibilityProvider,
                            StringComparison.Ordinal)))
            {
                throw new ChatGptConfigConflictException(
                    "无法证明历史 Provider 由启动器创建，已停止兼容配置。");
            }

            var state = await ReadConfigStateAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            var document = new TomlRootDocument(state.Text);
            if (snapshot.OfficialCompatibilityOwned)
            {
                if (!IsOwnedOfficialCompatibility(snapshot, configPath, document))
                {
                    throw new ChatGptConfigConflictException(
                        "启动器保留的历史 Provider 已被修改或删除，已停止自动操作。");
                }

                return false;
            }

            if (document.ContainsDefinition(LocalProviderKey)
                || string.Equals(document.GetStringValue("model_provider"), LocalProviderId, StringComparison.Ordinal))
            {
                throw new ChatGptConfigConflictException(
                    "官方配置中的历史 Provider 标识已被其他配置占用，已停止自动操作。");
            }

            var compatibleDocument = document.SetRawAssignment(
                LocalProviderKey,
                OfflineCompatibilityProvider);
            string? backupPath = null;
            if (state.Exists)
            {
                var backupsDirectory = Path.Combine(Path.GetDirectoryName(recoveryPath)!, "backups");
                Directory.CreateDirectory(backupsDirectory);
                backupPath = Path.Combine(
                    backupsDirectory,
                    $"config-compat.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}.dpapi");
                await ProtectedConfigBackup.WriteAsync(backupPath, state.Bytes, cancellationToken)
                    .ConfigureAwait(false);
                var verified = await ProtectedConfigBackup.ReadAsync(backupPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(state.Bytes),
                        SHA256.HashData(verified)))
                {
                    throw new InvalidDataException("官方配置兼容备份校验失败，已停止写入。");
                }
            }

            var prepared = snapshot with
            {
                Stage = ConfigTransactionStage.OfficialCompatibilityPrepared,
                CompatibilityBackupPath = backupPath,
                CompatibilityOriginalConfigExisted = state.Exists,
                CompatibilityOriginalConfigSha256 = ComputeSha256(state.Bytes),
                CompatibilityAppliedConfigSha256 = ComputeSha256(
                    Encoding.UTF8.GetBytes(compatibleDocument.ToString())),
            };
            await WriteJsonAtomicallyAsync(recoveryPath, prepared, cancellationToken).ConfigureAwait(false);
            EnsureClientClosed();
            await BeforeConfigCommitAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            await WriteConfigAtomicallyAsync(
                snapshot.ConfigPath,
                compatibleDocument.ToString(),
                state.Revision,
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAtomicallyAsync(
                recoveryPath,
                prepared with
                {
                    Stage = ConfigTransactionStage.Restored,
                    OfficialCompatibilityOwned = true,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> UpdateLocalAsync(
        ChatGptLocalModeRequest request,
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        ValidateRequest(request);
        EnsureClientClosed();
        await LocalModelCatalogValidator.ValidateSingleModelAsync(
            request.ModelCatalogPath,
            request.ModelSlug,
            cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Stage != ConfigTransactionStage.LocalApplied)
            {
                throw new InvalidOperationException(
                    $"当前恢复记录阶段 {snapshot.Stage} 不允许开始 Local 模型更新。");
            }

            if (!string.Equals(
                    Path.GetFullPath(request.ConfigPath),
                    Path.GetFullPath(snapshot.ConfigPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Local 模型更新指向了不同的 ChatGPT 配置文件。");
            }

            var currentState = await ReadConfigStateAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            var currentDocument = new TomlRootDocument(currentState.Text);
            EnsureAssignmentsStillOwned(currentDocument, snapshot.AppliedAssignments);
            var updatedDocument = BuildLocalDocument(currentDocument, request, snapshot.OriginalAssignments);
            var prepared = snapshot with
            {
                Stage = ConfigTransactionStage.LocalUpdatePrepared,
                PreviousAppliedAssignments = snapshot.AppliedAssignments,
                AppliedAssignments = CaptureAssignments(updatedDocument),
                OriginalLauncherSettings = request.OriginalLauncherSettings,
                TargetLauncherSettings = request.TargetLauncherSettings,
                SettingsCommitted = request.TargetLauncherSettings is null,
            };

            await WriteJsonAtomicallyAsync(recoveryPath, prepared, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureClientClosed();
                await BeforeConfigCommitAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
                await WriteConfigAtomicallyAsync(
                    snapshot.ConfigPath,
                    updatedDocument.ToString(),
                    currentState.Revision,
                    cancellationToken).ConfigureAwait(false);
                var applied = prepared with { Stage = ConfigTransactionStage.LocalUpdateApplied };
                await WriteJsonAtomicallyAsync(recoveryPath, applied, cancellationToken).ConfigureAwait(false);
                return applied;
            }
            catch
            {
                await TryResetPreparedUpdateAsync(recoveryPath, snapshot).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> UpdateLocalEndpointAsync(
        string recoveryPath,
        Uri openAIBaseUrl,
        LauncherSettings originalLauncherSettings,
        LauncherSettings targetLauncherSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        ArgumentNullException.ThrowIfNull(openAIBaseUrl);
        ArgumentNullException.ThrowIfNull(originalLauncherSettings);
        ArgumentNullException.ThrowIfNull(targetLauncherSettings);
        _ = LoopbackEndpoint.NormalizeBaseUrl(openAIBaseUrl);
        EnsureClientClosed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Stage != ConfigTransactionStage.LocalApplied)
            {
                throw new InvalidOperationException(
                    $"当前恢复记录阶段 {snapshot.Stage} 不允许迁移 Local 连接端口。");
            }

            var currentState = await ReadConfigStateAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            var currentDocument = new TomlRootDocument(currentState.Text);
            EnsureAssignmentsStillOwned(currentDocument, snapshot.AppliedAssignments);
            var updatedDocument = currentDocument.SetRawAssignment(
                LocalProviderKey,
                BuildLocalProviderAssignment(openAIBaseUrl));
            var prepared = snapshot with
            {
                Stage = ConfigTransactionStage.LocalUpdatePrepared,
                PreviousAppliedAssignments = snapshot.AppliedAssignments,
                AppliedAssignments = CaptureAssignments(updatedDocument),
                OriginalLauncherSettings = originalLauncherSettings,
                TargetLauncherSettings = targetLauncherSettings,
                SettingsCommitted = false,
            };

            await WriteJsonAtomicallyAsync(recoveryPath, prepared, cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureClientClosed();
                await BeforeConfigCommitAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
                await WriteConfigAtomicallyAsync(
                    snapshot.ConfigPath,
                    updatedDocument.ToString(),
                    currentState.Revision,
                    cancellationToken).ConfigureAwait(false);
                var applied = prepared with { Stage = ConfigTransactionStage.LocalUpdateApplied };
                await WriteJsonAtomicallyAsync(recoveryPath, applied, cancellationToken).ConfigureAwait(false);
                return applied;
            }
            catch
            {
                await TryResetPreparedUpdateAsync(recoveryPath, snapshot).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> CommitLocalUpdateAsync(
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Stage != ConfigTransactionStage.LocalUpdateApplied
                || snapshot.PreviousAppliedAssignments is null)
            {
                throw new InvalidOperationException("没有可提交的 Local 模型更新。");
            }

            var currentText = File.Exists(snapshot.ConfigPath)
                ? await File.ReadAllTextAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            EnsureAssignmentsStillOwned(new TomlRootDocument(currentText), snapshot.AppliedAssignments);
            var committed = snapshot with
            {
                Stage = ConfigTransactionStage.LocalApplied,
                PreviousAppliedAssignments = null,
                SettingsCommitted = true,
            };
            await WriteJsonAtomicallyAsync(recoveryPath, committed, cancellationToken).ConfigureAwait(false);
            return committed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> RollbackLocalUpdateAsync(
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        EnsureClientClosed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Stage == ConfigTransactionStage.LocalApplied
                && snapshot.PreviousAppliedAssignments is null)
            {
                return snapshot;
            }

            if (snapshot.Stage is not (ConfigTransactionStage.LocalUpdatePrepared
                or ConfigTransactionStage.LocalUpdateApplied)
                || snapshot.PreviousAppliedAssignments is null)
            {
                throw new InvalidOperationException("没有可回滚的 Local 模型更新。");
            }

            var currentState = await ReadConfigStateAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            var currentDocument = new TomlRootDocument(currentState.Text);
            if (AssignmentsMatch(currentDocument, snapshot.AppliedAssignments))
            {
                var previousDocument = ApplyAssignments(currentDocument, snapshot.PreviousAppliedAssignments);
                EnsureClientClosed();
                await BeforeConfigCommitAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
                await WriteConfigAtomicallyAsync(
                    snapshot.ConfigPath,
                    previousDocument.ToString(),
                    currentState.Revision,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!AssignmentsMatch(currentDocument, snapshot.PreviousAppliedAssignments))
            {
                throw new ChatGptConfigConflictException(
                    "ChatGPT Local 配置在模型更新期间被其他程序修改，已停止自动回滚。");
            }

            var rolledBack = snapshot with
            {
                Stage = ConfigTransactionStage.LocalApplied,
                AppliedAssignments = snapshot.PreviousAppliedAssignments,
                PreviousAppliedAssignments = null,
                TargetLauncherSettings = snapshot.OriginalLauncherSettings,
            };
            await WriteJsonAtomicallyAsync(recoveryPath, rolledBack, cancellationToken).ConfigureAwait(false);
            return rolledBack;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> RestoreOpenAIAsync(
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        EnsureClientClosed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);

            if (snapshot.Stage == ConfigTransactionStage.Restored)
            {
                return snapshot;
            }

            await VerifyOriginalBackupAsync(snapshot, cancellationToken).ConfigureAwait(false);

            var currentState = await ReadConfigStateAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            var currentDocument = new TomlRootDocument(currentState.Text);
            var officialAssignments = GetOfficialCompatibleAssignments(snapshot);
            var acceptableAssignments = snapshot.Stage switch
            {
                ConfigTransactionStage.Prepared =>
                    new[] { snapshot.AppliedAssignments, snapshot.OriginalAssignments, officialAssignments },
                ConfigTransactionStage.LocalUpdatePrepared or ConfigTransactionStage.LocalUpdateApplied
                    when snapshot.PreviousAppliedAssignments is not null =>
                    new[] { snapshot.AppliedAssignments, snapshot.PreviousAppliedAssignments },
                ConfigTransactionStage.OpenAiRestorePrepared =>
                    new[] { snapshot.AppliedAssignments, snapshot.OriginalAssignments, officialAssignments },
                _ => new[] { snapshot.AppliedAssignments },
            };
            if (!acceptableAssignments.Any(assignments => AssignmentsMatch(currentDocument, assignments)))
            {
                throw new ChatGptConfigConflictException(
                    "ChatGPT 受管理配置与恢复记录不一致，已停止自动恢复。");
            }

            // A prepared initial switch may never have written Local configuration.
            // In that case there is no new Local history to preserve from this attempt.
            var initialSwitchNeverApplied = snapshot.Stage == ConfigTransactionStage.Prepared
                && AssignmentsMatch(currentDocument, snapshot.OriginalAssignments);
            var restoredDocument = initialSwitchNeverApplied
                ? ApplyAssignments(currentDocument, snapshot.OriginalAssignments)
                : BuildOpenAiDocument(currentDocument, snapshot);
            EnsureClientClosed();
            await BeforeConfigCommitAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            await WriteRestoredConfigAsync(
                snapshot,
                restoredDocument,
                currentState.Revision,
                cancellationToken).ConfigureAwait(false);
            var restored = snapshot with
            {
                Stage = ConfigTransactionStage.Restored,
                PreviousAppliedAssignments = null,
                OfficialCompatibilityOwned = initialSwitchNeverApplied
                    ? snapshot.OfficialCompatibilityOwned
                    : IsOfficialCompatibilityOwned(snapshot),
            };
            await WriteJsonAtomicallyAsync(recoveryPath, restored, cancellationToken).ConfigureAwait(false);
            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> PrepareOpenAiRestoreAsync(
        string recoveryPath,
        LauncherSettings originalSettings,
        LauncherSettings targetSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        ArgumentNullException.ThrowIfNull(originalSettings);
        ArgumentNullException.ThrowIfNull(targetSettings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.Stage != ConfigTransactionStage.LocalApplied)
            {
                throw new InvalidOperationException(
                    $"当前恢复记录阶段 {snapshot.Stage} 不允许准备 OpenAI 恢复。");
            }

            var prepared = snapshot with
            {
                Stage = ConfigTransactionStage.OpenAiRestorePrepared,
                OriginalLauncherSettings = originalSettings,
                TargetLauncherSettings = targetSettings,
                SettingsCommitted = false,
            };
            await WriteJsonAtomicallyAsync(recoveryPath, prepared, cancellationToken).ConfigureAwait(false);
            return prepared;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigSnapshot> CommitLauncherSettingsAsync(
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            if (snapshot.SettingsCommitted)
            {
                return snapshot;
            }

            if (snapshot.Stage is not (ConfigTransactionStage.LocalApplied or ConfigTransactionStage.Restored))
            {
                throw new InvalidOperationException(
                    $"配置事务阶段 {snapshot.Stage} 尚不能提交 Launcher 设置。");
            }

            var committed = snapshot with { SettingsCommitted = true };
            await WriteJsonAtomicallyAsync(recoveryPath, committed, cancellationToken).ConfigureAwait(false);
            return committed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ManagedConfigRecoveryResult> RecoverInterruptedAsync(
        string recoveryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                ConfigTransactionFileLock.ForRecoveryPath(recoveryPath),
                cancellationToken).ConfigureAwait(false);
            var snapshot = await LoadSnapshotAsync(recoveryPath, cancellationToken).ConfigureAwait(false);
            var currentState = await ReadConfigStateAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
            var currentDocument = new TomlRootDocument(currentState.Text);
            var changed = false;
            LauncherSettings? pendingSettings = null;

            switch (snapshot.Stage)
            {
                case ConfigTransactionStage.Prepared:
                    if (AssignmentsMatch(currentDocument, snapshot.AppliedAssignments))
                    {
                        snapshot = snapshot with { Stage = ConfigTransactionStage.LocalApplied };
                        pendingSettings = snapshot.TargetLauncherSettings;
                    }
                    else if (AssignmentsMatch(currentDocument, snapshot.OriginalAssignments))
                    {
                        snapshot = snapshot with
                        {
                            Stage = ConfigTransactionStage.Restored,
                            TargetLauncherSettings = snapshot.OriginalLauncherSettings,
                        };
                        pendingSettings = snapshot.OriginalLauncherSettings;
                    }
                    else
                    {
                        throw RecoveryConflict();
                    }

                    changed = true;
                    break;

                case ConfigTransactionStage.LocalUpdatePrepared:
                case ConfigTransactionStage.LocalUpdateApplied:
                    if (snapshot.PreviousAppliedAssignments is null)
                    {
                        throw new InvalidDataException("Local 更新恢复记录缺少上一组受管理配置。");
                    }

                    if (AssignmentsMatch(currentDocument, snapshot.AppliedAssignments))
                    {
                        snapshot = snapshot with
                        {
                            Stage = ConfigTransactionStage.LocalApplied,
                            PreviousAppliedAssignments = null,
                        };
                        pendingSettings = snapshot.TargetLauncherSettings;
                    }
                    else if (AssignmentsMatch(currentDocument, snapshot.PreviousAppliedAssignments))
                    {
                        snapshot = snapshot with
                        {
                            Stage = ConfigTransactionStage.LocalApplied,
                            AppliedAssignments = snapshot.PreviousAppliedAssignments,
                            PreviousAppliedAssignments = null,
                            TargetLauncherSettings = snapshot.OriginalLauncherSettings,
                        };
                        pendingSettings = snapshot.OriginalLauncherSettings;
                    }
                    else
                    {
                        throw RecoveryConflict();
                    }

                    changed = true;
                    break;

                case ConfigTransactionStage.OpenAiRestorePrepared:
                    await VerifyOriginalBackupAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    var officialAssignments = GetOfficialCompatibleAssignments(snapshot);
                    if (AssignmentsMatch(currentDocument, snapshot.AppliedAssignments))
                    {
                        EnsureClientClosed();
                        var restoredDocument = BuildOpenAiDocument(currentDocument, snapshot);
                        await BeforeConfigCommitAsync(snapshot.ConfigPath, cancellationToken).ConfigureAwait(false);
                        await WriteRestoredConfigAsync(
                            snapshot,
                            restoredDocument,
                            currentState.Revision,
                            cancellationToken).ConfigureAwait(false);
                        snapshot = snapshot with
                        {
                            OfficialCompatibilityOwned = IsOfficialCompatibilityOwned(snapshot),
                        };
                    }
                    else if (AssignmentsMatch(currentDocument, officialAssignments))
                    {
                        snapshot = snapshot with
                        {
                            OfficialCompatibilityOwned = IsOfficialCompatibilityOwned(snapshot),
                        };
                    }
                    else if (AssignmentsMatch(currentDocument, snapshot.OriginalAssignments))
                    {
                        snapshot = snapshot with { OfficialCompatibilityOwned = false };
                    }
                    else
                    {
                        throw RecoveryConflict();
                    }

                    snapshot = snapshot with { Stage = ConfigTransactionStage.Restored };
                    pendingSettings = snapshot.TargetLauncherSettings;
                    changed = true;
                    break;

                case ConfigTransactionStage.OfficialCompatibilityPrepared:
                    if (snapshot.CompatibilityOriginalConfigSha256 is null
                        || snapshot.CompatibilityAppliedConfigSha256 is null)
                    {
                        throw new InvalidDataException("历史 Provider 兼容恢复记录不完整。");
                    }

                    if (snapshot.CompatibilityOriginalConfigExisted)
                    {
                        if (string.IsNullOrWhiteSpace(snapshot.CompatibilityBackupPath)
                            || !File.Exists(snapshot.CompatibilityBackupPath))
                        {
                            throw new FileNotFoundException(
                                "找不到官方配置兼容备份，已停止恢复。",
                                snapshot.CompatibilityBackupPath);
                        }

                        var compatibilityBackup = await ProtectedConfigBackup.ReadAsync(
                            snapshot.CompatibilityBackupPath,
                            cancellationToken).ConfigureAwait(false);
                        if (!string.Equals(
                                ComputeSha256(compatibilityBackup),
                                snapshot.CompatibilityOriginalConfigSha256,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("官方配置兼容备份校验失败，已停止恢复。");
                        }
                    }

                    var currentHash = ComputeSha256(currentState.Bytes);
                    if (currentState.Exists
                        && string.Equals(
                            currentHash,
                            snapshot.CompatibilityAppliedConfigSha256,
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            currentDocument.GetDefinition(LocalProviderKey),
                            OfflineCompatibilityProvider,
                            StringComparison.Ordinal))
                    {
                        snapshot = snapshot with
                        {
                            Stage = ConfigTransactionStage.Restored,
                            OfficialCompatibilityOwned = true,
                        };
                    }
                    else if (currentState.Exists == snapshot.CompatibilityOriginalConfigExisted
                        && string.Equals(
                            currentHash,
                            snapshot.CompatibilityOriginalConfigSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        snapshot = snapshot with
                        {
                            Stage = ConfigTransactionStage.Restored,
                            OfficialCompatibilityOwned = false,
                        };
                    }
                    else
                    {
                        throw RecoveryConflict();
                    }

                    changed = true;
                    break;

                case ConfigTransactionStage.LocalApplied:
                    if (!AssignmentsMatch(currentDocument, snapshot.AppliedAssignments))
                    {
                        throw RecoveryConflict();
                    }

                    if (!snapshot.SettingsCommitted)
                    {
                        pendingSettings = snapshot.TargetLauncherSettings;
                    }

                    break;

                case ConfigTransactionStage.Restored:
                    if (!snapshot.SettingsCommitted)
                    {
                        pendingSettings = snapshot.TargetLauncherSettings;
                    }

                    break;

                default:
                    throw new InvalidDataException($"不支持的配置恢复阶段：{snapshot.Stage}。");
            }

            if (changed)
            {
                await WriteJsonAtomicallyAsync(recoveryPath, snapshot, cancellationToken).ConfigureAwait(false);
            }

            return new ManagedConfigRecoveryResult(snapshot, pendingSettings, changed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LegacyBackupMigrationResult> MigrateLegacyBackupsAsync(
        LauncherDataPaths dataPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ConfigTransactionFileLock.AcquireAsync(
                dataPaths.ConfigTransactionLockFile,
                cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(dataPaths.BackupsDirectory))
            {
                return new LegacyBackupMigrationResult(0, 0);
            }

            PrivateFilePermissions.HardenDirectory(dataPaths.BackupsDirectory);
            var legacyPaths = Directory.EnumerateFiles(
                    dataPaths.BackupsDirectory,
                    "config.toml.*.bak",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (legacyPaths.Length == 0)
            {
                return new LegacyBackupMigrationResult(0, 0);
            }

            ManagedConfigSnapshot? snapshot = File.Exists(dataPaths.RecoveryFile)
                ? await LoadSnapshotAsync(dataPaths.RecoveryFile, cancellationToken).ConfigureAwait(false)
                : null;
            var migrated = 0;
            foreach (var legacyPath in legacyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDirectChildOfDirectory(legacyPath, dataPaths.BackupsDirectory);
                PrivateFilePermissions.HardenFile(legacyPath);
                var plaintext = await File.ReadAllBytesAsync(legacyPath, cancellationToken).ConfigureAwait(false);
                var encryptedPath = Path.ChangeExtension(legacyPath, ".dpapi");
                if (File.Exists(encryptedPath))
                {
                    encryptedPath = Path.Combine(
                        dataPaths.BackupsDirectory,
                        $"{Path.GetFileNameWithoutExtension(legacyPath)}.{Guid.NewGuid():N}.dpapi");
                }

                await ProtectedConfigBackup.WriteAsync(encryptedPath, plaintext, cancellationToken)
                    .ConfigureAwait(false);
                var verified = await ProtectedConfigBackup.ReadAsync(encryptedPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(plaintext),
                        SHA256.HashData(verified)))
                {
                    throw new InvalidDataException("旧版配置备份迁移后的密文校验失败。");
                }

                if (snapshot is not null
                    && !string.IsNullOrWhiteSpace(snapshot.BackupPath)
                    && string.Equals(
                        Path.GetFullPath(snapshot.BackupPath),
                        legacyPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    snapshot = snapshot with { BackupPath = encryptedPath };
                    await WriteJsonAtomicallyAsync(dataPaths.RecoveryFile, snapshot, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Delete(legacyPath);
                migrated++;
            }

            if (snapshot?.BackupPath is { } activeBackupPath
                && activeBackupPath.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
            {
                RotateEncryptedBackups(dataPaths.BackupsDirectory, activeBackupPath);
            }

            var remaining = Directory.EnumerateFiles(
                    dataPaths.BackupsDirectory,
                    "config.toml.*.bak",
                    SearchOption.TopDirectoryOnly)
                .Count();
            return new LegacyBackupMigrationResult(migrated, remaining);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ChatGptConfigConflictException RecoveryConflict() =>
        new("ChatGPT 受管理配置既不匹配事务前状态，也不匹配事务目标状态；已停止自动恢复。");

    private static void EnsureDirectChildOfDirectory(string path, string directory)
    {
        var actualParent = Path.GetDirectoryName(Path.GetFullPath(path));
        var expectedParent = Path.GetFullPath(directory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("拒绝迁移备份目录之外的文件。");
        }
    }

    private static IReadOnlyDictionary<string, string?> CaptureAssignments(TomlRootDocument document) =>
        ManagedKeys.ToDictionary(key => key, document.GetDefinition, StringComparer.Ordinal);

    // The provider deliberately keeps the existing OpenAI authentication manager so
    // Desktop retains its signed-in account shell and shared project history. Its
    // non-OpenAI identity is equally deliberate: Codex then performs its native local
    // compaction by asking the selected model for a summary through the ordinary
    // Responses route. llama.cpp remains responsible only for that model inference;
    // the launcher never creates summaries or rewrites conversation history.
    private static TomlRootDocument BuildLocalDocument(
        TomlRootDocument source,
        ChatGptLocalModeRequest request,
        IReadOnlyDictionary<string, string?> originalAssignments)
    {
        var contextWindow = request.ContextWindow.ToString(CultureInfo.InvariantCulture);
        var autoCompactTokenLimit = request.AutoCompactTokenLimit.ToString(CultureInfo.InvariantCulture);

        // This threshold tells Codex when to run its own native semantic compaction.
        // It is deliberately distinct from max_output_tokens: the launcher neither
        // limits one response nor summarizes/rewrites conversation history.
        var localDocument = source
            .SetRawAssignment("profile", null)
            .SetString("model", request.ModelSlug)
            .SetString("model_provider", LocalProviderId)
            .SetString("model_catalog_json", Path.GetFullPath(request.ModelCatalogPath))
            .SetRawAssignment("openai_base_url", null)
            .SetRawAssignment("model_context_window", $"model_context_window = {contextWindow}")
            .SetRawAssignment(
                "model_auto_compact_token_limit",
                $"model_auto_compact_token_limit = {autoCompactTokenLimit}")
            .SetRawAssignment("model_auto_compact_token_limit_scope", null)
            .SetRawAssignment("model_reasoning_effort", null)
            .SetRawAssignment("model_reasoning_summary", null)
            .SetRawAssignment("model_supports_reasoning_summaries", null)
            .SetRawAssignment("model_verbosity", null)
            .SetRawAssignment("service_tier", null)
            .SetRawAssignment(LocalProviderKey, BuildLocalProviderAssignment(request.OpenAIBaseUrl));

        return ApplyModelSandboxSettings(localDocument, request.SandboxSettings, originalAssignments);
    }

    private static TomlRootDocument ApplyModelSandboxSettings(
        TomlRootDocument source,
        ModelSandboxSettings? settings,
        IReadOnlyDictionary<string, string?> originalAssignments)
    {
        if (settings is null)
        {
            var originalLauncherProfile = originalAssignments.TryGetValue(
                    LauncherPermissionProfileKey,
                    out var originalProfileDefinition)
                && originalProfileDefinition is null;
            var currentLauncherProfileIsActive = originalLauncherProfile
                && string.Equals(
                    source.GetStringValue("default_permissions"),
                    LauncherPermissionProfileName,
                    StringComparison.Ordinal)
                && source.GetDefinition(LauncherPermissionProfileKey) is not null;
            return currentLauncherProfileIsActive
                ? RestoreSandboxAssignments(source, originalAssignments)
                : source;
        }

        var baseline = RestoreSandboxAssignments(source, originalAssignments);

        if (baseline.ContainsDefinition(LauncherPermissionProfileKey))
        {
            throw new ChatGptConfigConflictException(
                $"配置已包含权限配置 '{LauncherPermissionProfileName}'，无法安全应用模型沙箱设置。");
        }

        var validationErrors = ModelSandboxSettingsValidator.Validate(settings);
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, validationErrors), nameof(settings));
        }

        var configuredDefault = baseline.GetStringValue("default_permissions");
        var legacySandboxMode = baseline.GetStringValue("sandbox_mode");
        if (configuredDefault is not null && legacySandboxMode is not null)
        {
            throw new ChatGptConfigConflictException(
                "Codex 配置同时设置了 default_permissions 和 sandbox_mode；请先在 Codex 配置中保留其中一种沙箱格式。");
        }

        var hasLegacyWorkspaceSettings = baseline.GetDefinition("sandbox_workspace_write") is not null;
        if (configuredDefault is not null && hasLegacyWorkspaceSettings)
        {
            throw new ChatGptConfigConflictException(
                "Codex 配置同时设置了 default_permissions 和 sandbox_workspace_write；请先在 Codex 配置中保留其中一种沙箱格式。");
        }

        var parentProfile = configuredDefault ?? (legacySandboxMode switch
        {
            "read-only" => ":read-only",
            "workspace-write" => ":workspace",
            "danger-full-access" => ":danger-full-access",
            null => ":workspace",
            _ => throw new ChatGptConfigConflictException("Codex 配置中的 sandbox_mode 值无法识别。"),
        });
        if (string.Equals(parentProfile, ":danger-full-access", StringComparison.Ordinal))
        {
            throw new ChatGptConfigConflictException(
                "Codex 当前使用完整磁盘访问；Codex 不允许命名权限配置继承该模式。请先切换到工作区权限后再配置此模型。");
        }

        var legacyWorkspaceSettingsAreActive = legacySandboxMode is null or "workspace-write";
        var legacyNetworkAccess = legacyWorkspaceSettingsAreActive
            ? baseline.GetTableBooleanValue("sandbox_workspace_write", "network_access")
            : null;
        if (baseline.GetAssignment("sandbox_workspace_write") is not null)
        {
            throw new ChatGptConfigConflictException(
                "Codex 配置将 sandbox_workspace_write 写成了内联赋值，无法完整移入模型权限配置；为避免丢失现有规则，已停止切换。");
        }

        var excludeTempDirectory = legacyWorkspaceSettingsAreActive
            ? baseline.GetTableBooleanValue("sandbox_workspace_write", "exclude_tmpdir_env_var")
            : null;
        var excludeSlashTemp = legacyWorkspaceSettingsAreActive
            ? baseline.GetTableBooleanValue("sandbox_workspace_write", "exclude_slash_tmp")
            : null;
        if (excludeTempDirectory == true || excludeSlashTemp == true)
        {
            throw new ChatGptConfigConflictException(
                "当前 Codex 配置启用了临时目录排除选项，命名权限配置无法等价保留该选项；为避免扩大沙箱范围，已停止切换。");
        }

        var legacyWritableRoots = legacyWorkspaceSettingsAreActive
            ? baseline.GetTableStringArrayValue("sandbox_workspace_write", "writable_roots") ?? Array.Empty<string>()
            : Array.Empty<string>();
        bool? networkEnabled = settings.NetworkAccess switch
        {
            SandboxNetworkAccess.Full => true,
            SandboxNetworkAccess.Disabled => false,
            SandboxNetworkAccess.InheritCodexSettings => legacyNetworkAccess,
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
        var profileAssignment = BuildPermissionProfileAssignment(
            settings,
            parentProfile,
            networkEnabled,
            legacyWritableRoots);

        return baseline
            .SetRawDefinition("sandbox_mode", null)
            .SetRawDefinition("sandbox_workspace_write", null)
            .SetString("default_permissions", LauncherPermissionProfileName)
            .SetRawAssignment(LauncherPermissionProfileKey, profileAssignment);
    }

    private static TomlRootDocument RestoreSandboxAssignments(
        TomlRootDocument document,
        IReadOnlyDictionary<string, string?> originalAssignments)
    {
        foreach (var key in SandboxManagedKeys)
        {
            if (originalAssignments.TryGetValue(key, out var definition))
            {
                document = document.SetRawDefinition(key, definition);
            }
        }

        return document;
    }

    private static string BuildPermissionProfileAssignment(
        ModelSandboxSettings settings,
        string parentProfile,
        bool? networkEnabled,
        IReadOnlyList<string> legacyWritableRoots)
    {
        var fields = new List<string>
        {
            $"extends = {JsonSerializer.Serialize(parentProfile)}",
        };

        var filesystem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in legacyWritableRoots)
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ChatGptConfigConflictException(
                    "Codex 配置中的 sandbox_workspace_write.writable_roots 包含非绝对路径；为避免改变原有权限，已停止切换。");
            }

            filesystem[Path.GetFullPath(path)] = "write";
        }

        foreach (var permission in settings.AdditionalPaths)
        {
            filesystem[Path.GetFullPath(permission.Path)] = permission.AllowWrite ? "write" : "read";
        }

        if (filesystem.Count > 0)
        {
            var entries = filesystem
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{JsonSerializer.Serialize(pair.Key)} = {JsonSerializer.Serialize(pair.Value)}");
            fields.Add($"filesystem = {{ {string.Join(", ", entries)} }}");
        }

        if (networkEnabled is bool enabled)
        {
            fields.Add(enabled
                ? "network = { enabled = true, mode = \"full\" }"
                : "network = { enabled = false }");
        }

        return $"{LauncherPermissionProfileKey} = {{ {string.Join(", ", fields)} }}";
    }

    private static string BuildLocalProviderAssignment(Uri baseUri)
    {
        var normalizedBaseUrl = LoopbackEndpoint.NormalizeBaseUrl(baseUri);
        var encodedName = JsonSerializer.Serialize("Local llama.cpp");
        var encodedBaseUrl = JsonSerializer.Serialize(normalizedBaseUrl);
        return $"{LocalProviderKey} = {{ name = {encodedName}, base_url = {encodedBaseUrl}, "
            + "wire_api = \"responses\", requires_openai_auth = true, supports_websockets = false }";
    }

    private static TomlRootDocument BuildOpenAiDocument(
        TomlRootDocument currentDocument,
        ManagedConfigSnapshot snapshot)
    {
        return ApplyAssignments(currentDocument, GetOfficialCompatibleAssignments(snapshot));
    }

    private static IReadOnlyDictionary<string, string?> GetOfficialCompatibleAssignments(
        ManagedConfigSnapshot snapshot)
    {
        if (!IsOfficialCompatibilityOwned(snapshot))
        {
            return snapshot.OriginalAssignments;
        }

        var assignments = new Dictionary<string, string?>(snapshot.OriginalAssignments, StringComparer.Ordinal)
        {
            [LocalProviderKey] = OfflineCompatibilityProvider,
        };
        return assignments;
    }

    private static bool IsOfficialCompatibilityOwned(ManagedConfigSnapshot snapshot)
    {
        if (!snapshot.OriginalAssignments.TryGetValue(LocalProviderKey, out var originalProvider))
        {
            return false;
        }

        return originalProvider is null
            || snapshot.OfficialCompatibilityOwned
                && string.Equals(originalProvider, OfflineCompatibilityProvider, StringComparison.Ordinal);
    }

    private static bool IsOwnedOfficialCompatibility(
        ManagedConfigSnapshot snapshot,
        string configPath,
        TomlRootDocument document)
    {
        return snapshot.Stage == ConfigTransactionStage.Restored
            && snapshot.OfficialCompatibilityOwned
            && string.Equals(
                Path.GetFullPath(snapshot.ConfigPath),
                Path.GetFullPath(configPath),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                document.GetDefinition(LocalProviderKey),
                OfflineCompatibilityProvider,
                StringComparison.Ordinal)
            && !string.Equals(document.GetStringValue("model_provider"), LocalProviderId, StringComparison.Ordinal);
    }

    private static TomlRootDocument ApplyAssignments(
        TomlRootDocument document,
        IReadOnlyDictionary<string, string?> assignments)
    {
        foreach (var key in ManagedKeys)
        {
            if (!assignments.TryGetValue(key, out var assignment))
            {
                continue;
            }

            document = document.SetRawDefinition(key, assignment);
        }

        return document;
    }

    private static void EnsureAssignmentsStillOwned(
        TomlRootDocument currentDocument,
        IReadOnlyDictionary<string, string?> appliedAssignments)
    {
        if (!AssignmentsMatch(currentDocument, appliedAssignments))
        {
            throw new ChatGptConfigConflictException(
                "ChatGPT 受管理配置在 Local 模式期间被其他程序修改，已停止自动操作。");
        }
    }

    private static bool AssignmentsMatch(
        TomlRootDocument currentDocument,
        IReadOnlyDictionary<string, string?> expectedAssignments) =>
        OwnershipKeys
            .Where(expectedAssignments.ContainsKey)
            .All(key =>
            {
                expectedAssignments.TryGetValue(key, out var expected);
                return string.Equals(currentDocument.GetDefinition(key), expected, StringComparison.Ordinal);
            });

    private static async Task<ManagedConfigSnapshot> LoadSnapshotAsync(
        string recoveryPath,
        CancellationToken cancellationToken)
    {
        await using var recoveryStream = new FileStream(
            recoveryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<ManagedConfigSnapshot>(
            recoveryStream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("恢复记录为空。");
    }

    private static async Task VerifyOriginalBackupAsync(
        ManagedConfigSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.OriginalConfigExisted)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.BackupPath) || !File.Exists(snapshot.BackupPath))
        {
            throw new FileNotFoundException("找不到切换前的配置备份，已停止自动恢复。", snapshot.BackupPath);
        }

        var backupBytes = snapshot.BackupPath.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase)
            ? await ProtectedConfigBackup.ReadAsync(snapshot.BackupPath, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllBytesAsync(snapshot.BackupPath, cancellationToken).ConfigureAwait(false);
        var exactHash = ComputeSha256(backupBytes);
        var legacyTextHash = ComputeSha256(Encoding.UTF8.GetBytes(DecodeUtf8Config(backupBytes)));
        if (!string.Equals(exactHash, snapshot.OriginalConfigSha256, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(legacyTextHash, snapshot.OriginalConfigSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("切换前的配置备份哈希不匹配，已停止自动恢复。");
        }
    }

    private static async Task TryResetPreparedUpdateAsync(
        string recoveryPath,
        ManagedConfigSnapshot previousSnapshot)
    {
        try
        {
            var currentText = File.Exists(previousSnapshot.ConfigPath)
                ? await File.ReadAllTextAsync(previousSnapshot.ConfigPath, CancellationToken.None).ConfigureAwait(false)
                : string.Empty;
            if (AssignmentsMatch(new TomlRootDocument(currentText), previousSnapshot.AppliedAssignments))
            {
                await WriteJsonAtomicallyAsync(
                    recoveryPath,
                    previousSnapshot,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Preserve the prepared recovery record when the current state cannot be proven.
        }
    }

    private static async Task RollbackPreparedInitialApplicationAsync(
        string recoveryPath,
        ManagedConfigSnapshot prepared)
    {
        var currentState = await ReadConfigStateAsync(prepared.ConfigPath, CancellationToken.None)
            .ConfigureAwait(false);
        var currentDocument = new TomlRootDocument(currentState.Text);
        if (AssignmentsMatch(currentDocument, prepared.AppliedAssignments))
        {
            var restoredDocument = ApplyAssignments(currentDocument, prepared.OriginalAssignments);
            await WriteRestoredConfigAsync(
                prepared,
                restoredDocument,
                currentState.Revision,
                CancellationToken.None).ConfigureAwait(false);
        }
        else if (!AssignmentsMatch(currentDocument, prepared.OriginalAssignments))
        {
            throw new ChatGptConfigConflictException(
                "ChatGPT 配置在首次 Local 应用期间被其他程序修改，已停止自动回滚。");
        }

        await WriteJsonAtomicallyAsync(
            recoveryPath,
            prepared with
            {
                Stage = ConfigTransactionStage.Restored,
                TargetLauncherSettings = prepared.OriginalLauncherSettings,
                SettingsCommitted = true,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WriteRestoredConfigAsync(
        ManagedConfigSnapshot snapshot,
        TomlRootDocument restoredDocument,
        ConfigFileRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        var restoredText = restoredDocument.ToString();
        if (!snapshot.OriginalConfigExisted && string.IsNullOrWhiteSpace(restoredText))
        {
            await DeleteConfigIfUnchangedAsync(
                snapshot.ConfigPath,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteConfigAtomicallyAsync(
            snapshot.ConfigPath,
            restoredText,
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureClientClosed()
    {
        if (clientDetector.IsRunning())
        {
            throw new InvalidOperationException("请先完全关闭 ChatGPT Desktop 后再修改 Provider 配置。");
        }
    }

    private static void ValidateRequest(ChatGptLocalModeRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelCatalogPath);
        var sandboxErrors = ModelSandboxSettingsValidator.Validate(request.SandboxSettings);
        if (sandboxErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, sandboxErrors), nameof(request));
        }

        if (request.ContextWindow < 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Local 模型上下文不能小于 1,024 tokens。");
        }

        if (request.AutoCompactTokenLimit < 1_024
            || request.AutoCompactTokenLimit >= request.ContextWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Codex 自动压缩线必须至少为 1,024 tokens，且小于模型上下文容量。");
        }

        if (!request.OpenAIBaseUrl.IsAbsoluteUri
            || request.OpenAIBaseUrl.Scheme != Uri.UriSchemeHttp
            || !IsLoopbackHost(request.OpenAIBaseUrl.Host))
        {
            throw new ArgumentException("Local Provider 必须使用 HTTP 回环地址。", nameof(request));
        }

    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string DecodeUtf8Config(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("ChatGPT config.toml 不是有效的 UTF-8 文本。", exception);
        }
    }

    private static void RotateEncryptedBackups(string backupsDirectory, string currentBackupPath)
    {
        const int maximumBackups = 3;
        var current = Path.GetFullPath(currentBackupPath);
        foreach (var file in Directory.EnumerateFiles(backupsDirectory, "config.toml.*.dpapi", SearchOption.TopDirectoryOnly)
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(file => file.CreationTimeUtc)
                     .Skip(maximumBackups))
        {
            file.Delete();
        }
    }

    private static Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        WriteSensitiveTextAtomicallyAsync(
            path,
            JsonSerializer.Serialize(value, SerializerOptions) + Environment.NewLine,
            cancellationToken);

    private Task BeforeConfigCommitAsync(string path, CancellationToken cancellationToken) =>
        _beforeConfigCommit?.Invoke(path, cancellationToken) ?? Task.CompletedTask;

    private static async Task<ConfigFileState> ReadConfigStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new ConfigFileState(false, [], string.Empty, new ConfigFileRevision(false, string.Empty));
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return new ConfigFileState(
            true,
            bytes,
            DecodeUtf8Config(bytes),
            new ConfigFileRevision(true, ComputeSha256(bytes)));
    }

    private static async Task WriteConfigAtomicallyAsync(
        string path,
        string content,
        ConfigFileRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        _ = new TomlRootDocument(content);
        await WriteTextAtomicallyAsync(
            path,
            content,
            cancellationToken,
            expectedRevision: expectedRevision).ConfigureAwait(false);

        var persisted = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(persisted, content, StringComparison.Ordinal))
        {
            throw new IOException("ChatGPT config.toml 写入后校验不一致，已停止配置事务。");
        }

        _ = new TomlRootDocument(persisted);
    }

    private static async Task DeleteConfigIfUnchangedAsync(
        string path,
        ConfigFileRevision expectedRevision,
        CancellationToken cancellationToken)
    {
        var actualRevision = await ReadConfigRevisionAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureRevisionUnchanged(expectedRevision, actualRevision);
        if (actualRevision.Exists)
        {
            File.Delete(Path.GetFullPath(path));
        }
    }

    private static async Task WriteSensitiveTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await WriteTextAtomicallyAsync(path, content, cancellationToken, hardenTemporaryFile: true)
            .ConfigureAwait(false);
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken,
        bool hardenTemporaryFile = false,
        ConfigFileRevision? expectedRevision = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("目标文件必须位于一个目录中。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (hardenTemporaryFile)
            {
                PrivateFilePermissions.HardenFile(temporaryPath);
            }

            if (expectedRevision is not null)
            {
                var actualRevision = await ReadConfigRevisionAsync(fullPath, cancellationToken).ConfigureAwait(false);
                EnsureRevisionUnchanged(expectedRevision, actualRevision);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<ConfigFileRevision> ReadConfigRevisionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new ConfigFileRevision(false, string.Empty);
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return new ConfigFileRevision(true, ComputeSha256(bytes));
    }

    private static void EnsureRevisionUnchanged(
        ConfigFileRevision expected,
        ConfigFileRevision actual)
    {
        if (expected.Exists != actual.Exists
            || expected.Exists
            && !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChatGptConfigConflictException(
                "ChatGPT config.toml 在事务读取后又被其他程序修改；已停止本次写入，未覆盖外部更改。");
        }
    }

    private sealed record ConfigFileState(
        bool Exists,
        byte[] Bytes,
        string Text,
        ConfigFileRevision Revision);

    private sealed record ConfigFileRevision(bool Exists, string Sha256);
}
