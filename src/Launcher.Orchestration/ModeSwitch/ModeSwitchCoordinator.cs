using System.Net;
using System.Net.Sockets;
using System.Text;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.State;
using Launcher.Models.Profiles;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Orchestration.ModeSwitch;

public sealed class ModeSwitchCoordinator(
    ISettingsStore settingsStore,
    IChatGptClientDetector clientDetector,
    ChatGptConfigTransactionService configTransactionService,
    LauncherDataPaths dataPaths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ModeSwitchResult> SwitchToLocalAsync(
        LocalModeSwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLocalRequest(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transactionLock = await ModeSwitchFileLock.AcquireAsync(
                dataPaths.ModeSwitchLockFile,
                cancellationToken).ConfigureAwait(false);
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
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

            Directory.CreateDirectory(dataPaths.GeneratedDirectory);
            Directory.CreateDirectory(dataPaths.LogsDirectory);
            var artifactSnapshot = await CaptureGeneratedArtifactsAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteGeneratedArtifactsAsync(request.Profile, runtimeRoot, cancellationToken)
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
                    OriginalLauncherSettings = settings,
                    TargetLauncherSettings = targetSettings,
                };

                if (settings.SelectedMode == ProviderMode.Local)
                {
                    return await UpdateSelectedLocalModelAsync(
                        settings,
                        request,
                        runtimeRoot,
                        configRequest,
                        artifactSnapshot,
                        cancellationToken).ConfigureAwait(false);
                }

                var transaction = await configTransactionService.ApplyLocalAsync(
                    configRequest,
                    dataPaths,
                    cancellationToken).ConfigureAwait(false);

                try
                {
                    var updated = targetSettings;
                    await settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                    transaction = await configTransactionService.CommitLauncherSettingsAsync(
                        dataPaths.RecoveryFile,
                        cancellationToken).ConfigureAwait(false);
                    return new ModeSwitchResult(true, ProviderMode.Local, updated.SelectedModelId, transaction);
                }
                catch
                {
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
                    throw;
                }
            }
            catch
            {
                await RestoreGeneratedArtifactsAsync(artifactSnapshot, CancellationToken.None).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        try
        {
            var transaction = await configTransactionService.UpdateLocalAsync(
                configRequest,
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
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
                rollbackErrors.Insert(0, exception);
                throw new AggregateException(
                    "Local 模型切换失败且未能完整回滚，请停止启动 ChatGPT 并检查恢复记录。",
                    rollbackErrors);
            }

            throw;
        }
    }

    public async Task<ModeSwitchResult> SwitchToOpenAIAsync(CancellationToken cancellationToken = default)
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
            await configTransactionService.PrepareOpenAiRestoreAsync(
                dataPaths.RecoveryFile,
                settings,
                updated,
                cancellationToken).ConfigureAwait(false);
            var transaction = await configTransactionService.RestoreOpenAIAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            officialConfigRestored = true;
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

        if (!string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
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

    private async Task WriteGeneratedArtifactsAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        await LocalModelCatalogBuilder.WriteAtomicallyAsync(
            dataPaths.LocalModelCatalogFile,
            new LocalModelCatalogOptions
            {
                Slug = profile.Alias,
                DisplayName = profile.DisplayName,
                ContextWindow = profile.ContextSize,
            },
            cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicallyAsync(
            dataPaths.RouterPresetFile,
            RouterPresetGenerator.Generate(profile, runtimeRoot, loadOnStartup: false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeneratedArtifactsSnapshot> CaptureGeneratedArtifactsAsync(
        CancellationToken cancellationToken)
    {
        var catalog = await CaptureFileAsync(dataPaths.LocalModelCatalogFile, cancellationToken).ConfigureAwait(false);
        var preset = await CaptureFileAsync(dataPaths.RouterPresetFile, cancellationToken).ConfigureAwait(false);
        return new GeneratedArtifactsSnapshot(catalog, preset);
    }

    private async Task RestoreGeneratedArtifactsAsync(
        GeneratedArtifactsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await RestoreFileAsync(dataPaths.LocalModelCatalogFile, snapshot.Catalog, cancellationToken)
            .ConfigureAwait(false);
        await RestoreFileAsync(dataPaths.RouterPresetFile, snapshot.RouterPreset, cancellationToken)
            .ConfigureAwait(false);
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
        GeneratedFileSnapshot RouterPreset);

    private sealed record GeneratedFileSnapshot(bool Existed, string? Content);
}
