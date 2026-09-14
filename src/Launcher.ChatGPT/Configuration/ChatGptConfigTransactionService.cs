using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Security;

namespace Launcher.ChatGPT.Configuration;

public sealed class ChatGptConfigTransactionService
{
    private const string LocalProviderId = "chatgpt_local_launcher";
    private const string LocalProviderKey = "model_providers.chatgpt_local_launcher";

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
    ];

    // Permission and model-preset fields are snapshotted so the official defaults
    // can be restored, but Local mode does not own them. Desktop remains free to
    // normalize these UI-owned values without causing a Provider transaction conflict.
    private static readonly string[] OwnershipKeys =
    [
        "model",
        "model_provider",
        "model_catalog_json",
        "openai_base_url",
        "profile",
        "model_context_window",
        LocalProviderKey,
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
            if (originalDocument.ContainsDefinition(LocalProviderKey))
            {
                throw new ChatGptConfigConflictException(
                    $"官方配置已包含 Provider '{LocalProviderId}'，为避免覆盖用户配置，已停止 Local 模式切换。");
            }

            var originalAssignments = CaptureAssignments(originalDocument);

            var localDocument = BuildLocalDocument(originalDocument, request);
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
            var updatedDocument = BuildLocalDocument(currentDocument, request);
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
            var acceptableAssignments = snapshot.Stage switch
            {
                ConfigTransactionStage.Prepared =>
                    new[] { snapshot.AppliedAssignments, snapshot.OriginalAssignments },
                ConfigTransactionStage.LocalUpdatePrepared or ConfigTransactionStage.LocalUpdateApplied
                    when snapshot.PreviousAppliedAssignments is not null =>
                    new[] { snapshot.AppliedAssignments, snapshot.PreviousAppliedAssignments },
                ConfigTransactionStage.OpenAiRestorePrepared =>
                    new[] { snapshot.AppliedAssignments, snapshot.OriginalAssignments },
                _ => new[] { snapshot.AppliedAssignments },
            };
            if (!acceptableAssignments.Any(assignments => AssignmentsMatch(currentDocument, assignments)))
            {
                throw new ChatGptConfigConflictException(
                    "ChatGPT 受管理配置与恢复记录不一致，已停止自动恢复。");
            }

            var restoredDocument = BuildOpenAiDocument(currentDocument, snapshot);
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
                    }
                    else if (!AssignmentsMatch(currentDocument, snapshot.OriginalAssignments))
                    {
                        throw RecoveryConflict();
                    }

                    snapshot = snapshot with { Stage = ConfigTransactionStage.Restored };
                    pendingSettings = snapshot.TargetLauncherSettings;
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
        ManagedKeys.ToDictionary(key => key, document.GetAssignment, StringComparer.Ordinal);

    // The provider deliberately keeps the existing OpenAI authentication manager so
    // Desktop retains its signed-in account shell and shared project history. Its
    // non-OpenAI identity is equally deliberate: Codex then performs its native local
    // compaction by asking the selected model for a summary through the ordinary
    // Responses route. llama.cpp remains responsible only for that model inference;
    // the launcher never creates summaries or rewrites conversation history.
    private static TomlRootDocument BuildLocalDocument(
        TomlRootDocument source,
        ChatGptLocalModeRequest request)
    {
        var contextWindow = request.ContextWindow.ToString(CultureInfo.InvariantCulture);
        var autoCompactTokenLimit = request.AutoCompactTokenLimit.ToString(CultureInfo.InvariantCulture);

        // This threshold tells Codex when to run its own native semantic compaction.
        // It is deliberately distinct from max_output_tokens: the launcher neither
        // limits one response nor summarizes/rewrites conversation history.
        return source
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
        // The initial Local transaction refuses a pre-existing provider with our
        // fixed ID. Applying the captured value therefore removes only the provider
        // created by this transaction, while remaining compatible with old recovery
        // records that may contain an explicit original value.
        return ApplyAssignments(currentDocument, snapshot.OriginalAssignments);
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

            document = document.SetRawAssignment(key, assignment);
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
                return string.Equals(currentDocument.GetAssignment(key), expected, StringComparison.Ordinal);
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
