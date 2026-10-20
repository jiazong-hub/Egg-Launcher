using System.Net;
using System.Net.Sockets;
using System.Text;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Core.Persistence;
using Launcher.Core.State;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;

namespace Launcher.Orchestration.ModeSwitch;

public sealed class ModeSwitchCoordinator(
    ISettingsStore settingsStore,
    IChatGptClientDetector clientDetector,
    ChatGptConfigTransactionService configTransactionService,
    LauncherDataPaths dataPaths,
    DiagnosticJournal? diagnostics = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DiagnosticJournal _diagnostics = diagnostics ?? DiagnosticJournal.None;

    public Task<ModeSwitchResult> SwitchToLocalAsync(LocalModeSwitchRequest request, CancellationToken cancellationToken = default) =>
        _diagnostics.RunAsync("local_mode_apply", new Dictionary<string, object?>
        {
            ["modelId"] = request?.Profile?.Id,
            ["configuration"] = Path.GetFileName(request?.Profile?.PreferredConfigurationPath),
        }, operation => SwitchToLocalCoreAsync(request!, operation, cancellationToken));

    private async Task<ModeSwitchResult> SwitchToLocalCoreAsync(
        LocalModeSwitchRequest request,
        DiagnosticOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLocalRequest(request);
        operation.Set("llamaCustomNames", request.Profile.CustomParameters.Llama.Select(r => r.Name).ToArray());
        operation.Set("codexCustomNames", request.Profile.CustomParameters.Codex.Select(r => r.Name).ToArray());
        operation.SetDetail("runtimeParameters", request.Profile.RuntimeParameters);
        operation.SetDetail("experimentalEffect", "unverified");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ModeSwitchFileLock.AcquireAsync(
                dataPaths.ModeSwitchLockFile,
                cancellationToken).ConfigureAwait(false);
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            operation.Set("requestedRequiresOpenAiAuth", settings.LocalRequiresOpenAiAuth);
            operation.Set("preferenceSource", settingsStore is JsonSettingsStore jsonStore
                ? jsonStore.LastLoadUsedAuthenticationDefault ? "missing_field_default" : "stored_setting" : "unknown");
            await operation.StageAsync("check_client_and_model").ConfigureAwait(false);
            if (settings.SelectedMode == ProviderMode.Local)
            {
                if (clientDetector.IsRunning())
                {
                    throw new InvalidOperationException("请先完全关闭 ChatGPT Desktop 后再切换本地模型。");
                }
            }
            else
            {
                ModeSwitchGuard.EnsureCanSwitch(
                    settings.SelectedMode,
                    ProviderMode.Local,
                    clientDetector.IsRunning());
            }

            var runtimeRoot = Path.GetFullPath(request.RuntimeRoot);
            ValidateRuntimeAndProfile(request.Profile, runtimeRoot);
            await operation.StageAsync("generate_runtime_files").ConfigureAwait(false);

            Directory.CreateDirectory(dataPaths.GeneratedDirectory);
            Directory.CreateDirectory(dataPaths.LogsDirectory);
            Launcher.Scripts.Batch.BatchScriptGenerator.EnsureCanWrite(runtimeRoot, request.Profile.Id);
            var artifactSnapshot = await CaptureGeneratedArtifactsAsync(runtimeRoot, request.Profile.Id, cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteGeneratedArtifactsAsync(request.Profile, runtimeRoot, operation, cancellationToken)
                    .ConfigureAwait(false);

                var publicPort = SelectPublicProxyPort(settings, request.RouterPort);
                var targetSettings = LocalSettings(settings, request, runtimeRoot, publicPort);

                var configRequest = new ChatGptLocalModeRequest
                {
                    ConfigPath = Path.Combine(Path.GetFullPath(request.CodexHome), "config.toml"),
                    ModelSlug = request.Profile.Alias,
                    ModelCatalogPath = dataPaths.LocalModelCatalogFile,
                    OpenAIBaseUrl = new Uri($"http://{IPAddress.Loopback}:{publicPort}/v1"),
                    ContextWindow = request.Profile.ContextSize,
                    AutoCompactTokenLimit = request.Profile.AutoCompactTokenLimit,
                    ToolOutputTokenLimit = request.Profile.ToolOutputTokenLimit,
                    CodexStreamIdleTimeoutMinutes = request.Profile.CodexStreamIdleTimeoutMinutes,
                    RequiresOpenAiAuth = settings.LocalRequiresOpenAiAuth,
                    ShowThinkingProcess = request.Profile.ShowThinkingProcess && request.Profile.SupportsThinkingSwitch == true
                        && (request.Profile.ThinkingEnabled ?? request.Profile.DefaultThinkingEnabled) == true,
                    DefaultReasoningEffort = ReasoningDefaultSelection.ForCodex(request.Profile),
                    SandboxSettings = request.Profile.SandboxSettings,
                    CustomParameters = request.Profile.CustomParameters.Codex,
                    OriginalLauncherSettings = settings,
                    TargetLauncherSettings = targetSettings,
                };

                if (settings.SelectedMode == ProviderMode.Local)
                {
                    await operation.StageAsync("update_local_configuration").ConfigureAwait(false);
                    var result = await UpdateSelectedLocalModelAsync(
                        settings,
                        request,
                        runtimeRoot,
                        configRequest,
                        artifactSnapshot,
                        operation,
                        cancellationToken).ConfigureAwait(false);
                    operation.Set("appliedRequiresOpenAiAuth", settings.LocalRequiresOpenAiAuth);
                    return result;
                }

                await operation.StageAsync("write_codex_configuration").ConfigureAwait(false);
                var transaction = await configTransactionService.ApplyLocalAsync(
                    configRequest,
                    dataPaths,
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    var updated = targetSettings;
                    await operation.StageAsync("commit_launcher_settings").ConfigureAwait(false);
                    await settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                    transaction = await configTransactionService.CommitLauncherSettingsAsync(
                        dataPaths.RecoveryFile,
                        cancellationToken).ConfigureAwait(false);
                    operation.Set("appliedRequiresOpenAiAuth", settings.LocalRequiresOpenAiAuth);
                    return new ModeSwitchResult(true, ProviderMode.Local, updated.SelectedModelId, transaction);
                }
                catch
                {
                    operation.Set("failureStage", operation.Stage);
                    await operation.StageAsync("rollback_codex_and_settings").ConfigureAwait(false);
                    await configTransactionService.PrepareOpenAiRestoreAsync(
                        dataPaths.RecoveryFile,
                        targetSettings,
                        settings,
                        CancellationToken.None).ConfigureAwait(false);
                    await configTransactionService.RestoreOpenAIAsync(
                        dataPaths.RecoveryFile,
                        CancellationToken.None).ConfigureAwait(false);
                    await settingsStore.SaveAsync(settings, CancellationToken.None).ConfigureAwait(false);
                    await configTransactionService.CommitLauncherSettingsAsync(
                        dataPaths.RecoveryFile,
                        CancellationToken.None).ConfigureAwait(false);
                    operation.Set("configurationRollback", "completed");
                    throw;
                }
            }
            catch
            {
                operation.Set("generatedFilesRollbackStage", operation.Stage);
                await RestoreGeneratedArtifactsAsync(artifactSnapshot, CancellationToken.None).ConfigureAwait(false);
                operation.Set("generatedFilesRollback", "completed");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModeSwitchResult> UpdateSelectedLocalModelAsync(
        LauncherSettings settings,
        LocalModeSwitchRequest request,
        string runtimeRoot,
        ChatGptLocalModeRequest configRequest,
        GeneratedArtifactsSnapshot artifactSnapshot,
        DiagnosticOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var transaction = await configTransactionService.UpdateLocalAsync(
                configRequest,
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            await operation.StageAsync("commit_launcher_settings").ConfigureAwait(false);
            var updated = configRequest.TargetLauncherSettings
                ?? LocalSettings(settings, request, runtimeRoot, request.RouterPort);
            await settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            transaction = await configTransactionService.CommitLocalUpdateAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            return new ModeSwitchResult(true, ProviderMode.Local, updated.SelectedModelId, transaction);
        }
        catch (Exception exception)
        {
            operation.Set("failureStage", operation.Stage);
            await operation.StageAsync("rollback_local_update").ConfigureAwait(false);
            var rollbackErrors = new List<Exception>();
            try
            {
                await configTransactionService.RollbackLocalUpdateAsync(
                    dataPaths.RecoveryFile,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                rollbackErrors.Add(rollbackException);
            }

            try
            {
                await RestoreGeneratedArtifactsAsync(artifactSnapshot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception artifactException)
            {
                rollbackErrors.Add(artifactException);
            }

            try
            {
                await settingsStore.SaveAsync(settings, CancellationToken.None).ConfigureAwait(false);
                await configTransactionService.CommitLauncherSettingsAsync(
                    dataPaths.RecoveryFile,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception settingsException)
            {
                rollbackErrors.Add(settingsException);
            }

            if (rollbackErrors.Count > 0)
            {
                operation.Set("rollback", "incomplete");
                operation.Set("rollbackErrorCount", rollbackErrors.Count);
                rollbackErrors.Insert(0, exception);
                throw new AggregateException(
                    "Local 模型切换失败且未能完整回滚，请停止启动 ChatGPT 并检查恢复记录。",
                    rollbackErrors);
            }

            operation.Set("rollback", "completed");
            throw;
        }
    }

    public Task<ModeSwitchResult> SwitchToOpenAIAsync(CancellationToken cancellationToken = default) =>
        _diagnostics.RunAsync("online_mode_restore", null, operation => SwitchToOpenAiCoreAsync(operation, cancellationToken));

    private async Task<ModeSwitchResult> SwitchToOpenAiCoreAsync(DiagnosticOperation operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var officialConfigRestored = false;
        try
        {
            await using var transactionLock = await ModeSwitchFileLock.AcquireAsync(
                dataPaths.ModeSwitchLockFile,
                cancellationToken).ConfigureAwait(false);
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings.SelectedMode == ProviderMode.OpenAI)
            {
                return new ModeSwitchResult(false, ProviderMode.OpenAI, settings.SelectedModelId, null);
            }

            ModeSwitchGuard.EnsureCanSwitch(
                settings.SelectedMode,
                ProviderMode.OpenAI,
                clientDetector.IsRunning());
            if (!File.Exists(dataPaths.RecoveryFile))
            {
                throw new FileNotFoundException("找不到 Local 模式的恢复记录，已停止自动恢复。", dataPaths.RecoveryFile);
            }

            var updated = settings with { SelectedMode = ProviderMode.OpenAI };
            await operation.StageAsync("prepare_official_restore").ConfigureAwait(false);
            await configTransactionService.PrepareOpenAiRestoreAsync(
                dataPaths.RecoveryFile,
                settings,
                updated,
                cancellationToken).ConfigureAwait(false);
            await operation.StageAsync("restore_official_configuration").ConfigureAwait(false);
            var transaction = await configTransactionService.RestoreOpenAIAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            officialConfigRestored = true;
            operation.Set("officialConfigRestored", true);
            operation.Set("restoredCustomKeys", transaction.OriginalAssignments.Keys
                .Where(k => k.StartsWith("custom:", StringComparison.Ordinal)).Select(k => k[7..]).ToArray());
            await operation.StageAsync("commit_launcher_settings").ConfigureAwait(false);
            await settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            transaction = await configTransactionService.CommitLauncherSettingsAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            return new ModeSwitchResult(true, ProviderMode.OpenAI, updated.SelectedModelId, transaction);
        }
        catch (Exception exception) when (officialConfigRestored)
        {
            throw new InvalidOperationException(
                "OpenAI 官方配置已经恢复，但 Launcher 状态尚未完成保存。"
                + "请关闭并重新打开 Launcher，让现有恢复记录完成收尾；在此之前不要再次切换模式。",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateLocalRequest(LocalModeSwitchRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodexHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeRoot);
        ArgumentNullException.ThrowIfNull(request.Profile);
        if (request.RouterPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Router 端口必须在 1 到 65535 之间。");
        }
    }

    private static void ValidateRuntimeAndProfile(ModelProfile profile, string runtimeRoot)
    {
        if (Launcher.Scripts.Templates.ReasoningValidationState.RequiresCurrentProof(profile))
        {
            var check = Launcher.Scripts.Templates.ReasoningValidationState.Check(profile, runtimeRoot);
            if (check.State != Launcher.Scripts.Templates.ReasoningValidationStateKind.Current)
                throw new InvalidDataException(check.Reason);
        }
        var profileErrors = ModelProfileValidator.Validate(profile, runtimeRoot);
        if (profileErrors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, profileErrors));
        }

        if (profile.ContextSize < 1_024)
        {
            throw new InvalidDataException(
                "ChatGPT Desktop 的本地模型 Catalog 需要明确的 Context，且不能小于 1,024 tokens。"
                + "请根据模型和硬件编辑该模型的 Profile；Launcher 不会代替用户选择参数。");
        }

        var modelPath = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Profile 指向的 GGUF 主模型不存在。", modelPath);
        }

        if (profile.Jinja && !string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
        {
            var templatePath = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ChatTemplateRelativePath));
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException("Profile 指向的 Chat Template 不存在。", templatePath);
            }
        }

        var executablePath = Path.Combine(runtimeRoot, "llama-server.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Runtime Root 中不存在 llama-server.exe。", executablePath);
        }
    }

    private static LauncherSettings LocalSettings(
        LauncherSettings settings,
        LocalModeSwitchRequest request,
        string runtimeRoot,
        int publicPort) =>
        settings with
        {
            SelectedMode = ProviderMode.Local,
            SelectedModelId = request.Profile.Id,
            LlamaRoot = runtimeRoot,
            RouterPort = publicPort,
            PublicProxyPortInitialized = true,
        };

    private static int SelectPublicProxyPort(LauncherSettings settings, int requestedPort)
    {
        if (settings.PublicProxyPortInitialized || requestedPort != 8080)
        {
            return requestedPort;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task WriteGeneratedArtifactsAsync(ModelProfile profile, string runtimeRoot, DiagnosticOperation operation, CancellationToken cancellationToken)
    {
        await operation.StageAsync("generate_batch").ConfigureAwait(false);
        await Launcher.Scripts.Batch.BatchScriptGenerator.WriteOwnedAsync(runtimeRoot, profile, cancellationToken).ConfigureAwait(false);
        await operation.StageAsync("generate_router_preset_and_catalog").ConfigureAwait(false);
        await Launcher.Orchestration.Models.LocalModelConfigurationWriter.WriteAsync(
            profile, runtimeRoot, dataPaths.RouterPresetFile, dataPaths.LocalModelCatalogFile, cancellationToken).ConfigureAwait(false);
        await operation.StageAsync("write_applied_run_snapshot").ConfigureAwait(false);
        await Launcher.Orchestration.Models.AppliedLocalRun.WriteAsync(profile, runtimeRoot, dataPaths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeneratedArtifactsSnapshot> CaptureGeneratedArtifactsAsync(string root, string profileId,
        CancellationToken cancellationToken)
    {
        var catalog = await CaptureFileAsync(dataPaths.LocalModelCatalogFile, cancellationToken).ConfigureAwait(false);
        var preset = await CaptureFileAsync(dataPaths.RouterPresetFile, cancellationToken).ConfigureAwait(false);
        var applied = await CaptureFileAsync(dataPaths.AppliedLocalRunFile, cancellationToken).ConfigureAwait(false);
        var batchPath = Launcher.Scripts.Batch.BatchScriptGenerator.GetOutputPath(root, profileId);
        var batch = await CaptureFileAsync(batchPath, cancellationToken).ConfigureAwait(false);
        return new GeneratedArtifactsSnapshot(catalog, preset, applied, batchPath, batch);
    }

    private async Task RestoreGeneratedArtifactsAsync(
        GeneratedArtifactsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await RestoreFileAsync(dataPaths.LocalModelCatalogFile, snapshot.Catalog, cancellationToken)
            .ConfigureAwait(false);
        await RestoreFileAsync(dataPaths.RouterPresetFile, snapshot.RouterPreset, cancellationToken)
            .ConfigureAwait(false);
        await RestoreFileAsync(dataPaths.AppliedLocalRunFile, snapshot.AppliedRun, cancellationToken).ConfigureAwait(false);
        await RestoreFileAsync(snapshot.BatchPath, snapshot.Batch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GeneratedFileSnapshot> CaptureFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var exists = File.Exists(path);
        return new GeneratedFileSnapshot(
            exists,
            exists ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null);
    }

    private static async Task RestoreFileAsync(
        string path,
        GeneratedFileSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Existed)
        {
            await WriteTextAtomicallyAsync(
                path,
                snapshot.Content ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("生成文件必须位于一个目录中。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original transaction result; generated temp files are never consumed.
            }
        }
    }

    private sealed record GeneratedArtifactsSnapshot(
        GeneratedFileSnapshot Catalog,
        GeneratedFileSnapshot RouterPreset,
        GeneratedFileSnapshot AppliedRun,
        string BatchPath,
        GeneratedFileSnapshot Batch);

    private sealed record GeneratedFileSnapshot(bool Existed, string? Content);
}
