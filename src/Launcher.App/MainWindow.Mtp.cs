using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Runtime.Detection;
using Launcher.Runtime.Router;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class MainWindow
{
    private async Task LoadExternalMtpAsync(ModelProfile model)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || !CanEditProfile(model, forceClientRefresh: true))
        {
            return;
        }

        if (!CanRunModelResourceDetection()) return;
        await using var operation = _diagnostics.Begin("mtp_resource_add", ModelDiagnosticFields(model));
        await operation.StageAsync("select_and_validate_file");
        var runtimeRoot = Path.GetFullPath(_settings.LlamaRoot);
        var modelsRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "models"));
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguageManager.Choose(
                "选择要与当前主模型联合验证的外置 MTP GGUF",
                "Select an external MTP GGUF to validate with the current main model"),
            Filter = "GGUF (*.gguf)|*.gguf",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(modelsRoot) ? modelsRoot : runtimeRoot,
        };
        if (dialog.ShowDialog(ResourceDialogOwner) != true)
        {
            return;
        }

        string relativePath;
        try
        {
            operation.Set("resource", Path.GetFileName(dialog.FileName));
            relativePath = ValidateExternalMtpSelection(
                model,
                runtimeRoot,
                dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException
                                          or OverflowException)
        {
            await operation.FailAsync(exception, "resource_file_rejected");
            MessageBox.Show(
                ResourceDialogOwner,
                exception.Message,
                AppLanguageManager.Choose("不能加载该 MTP 文件", "Unable to Load This MTP File"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        operation.Set("resource", Path.GetFileName(relativePath));
        if (await TryReuseResourceAsync(model with { MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = relativePath },
                runtimeRoot, Launcher.Scripts.Templates.ModelResourceKind.Mtp))
        { await operation.CompleteAsync("blocked", "already_registered"); return; }

        if (HasActiveOrQueuedDownloads)
        {
            await operation.CompleteAsync("blocked", "downloads_active");
            MessageBox.Show(
                ResourceDialogOwner,
                AppLanguageManager.Choose(
                    "请等待当前单文件下载任务结束后再验证 MTP。",
                    "Wait for the current single-file download task to finish before validating MTP."),
                AppLanguageManager.Choose("Runtime 正在使用", "Runtime Is Busy"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var conflict = GetModelDownloadBlockReason();
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            await operation.CompleteAsync("blocked", "runtime_in_use");
            MessageBox.Show(
                ResourceDialogOwner,
                AppLanguageManager.Choose(
                    $"当前不能启动隔离验证：{conflict}\n\n请先关闭正在运行的本地模型，再重新加载该文件。",
                    $"Isolated validation cannot start now: {conflict}\n\nStop the running local model and load this file again."),
                AppLanguageManager.Choose("暂时不能验证 MTP", "MTP Validation Is Unavailable"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                ResourceDialogOwner,
                AppLanguageManager.Choose(
                    $"将使用 llama.cpp 临时加载主模型与以下外置 MTP 文件，并执行一次极短推理：\n\n{relativePath}\n\n验证期间会占用正常加载模型所需的内存和显存。继续吗？",
                    $"llama.cpp will temporarily load the main model with this external MTP file and run a very short inference:\n\n{relativePath}\n\nValidation uses the memory and VRAM normally required to load the model. Continue?"),
                AppLanguageManager.Choose("验证并关联外置 MTP", "Validate and Associate External MTP"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (!CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
                throw new InvalidOperationException(AppLanguageManager.Choose("本地服务状态已改变，请停止服务后再检测。", "Local service state changed. Stop services before checking."));
            await Launcher.Scripts.Templates.ModelResourceCatalog.EnsureMigratedAsync(model, runtimeRoot, _lifetime.Token);
            StatusText.Text = AppLanguageManager.Choose(
                "正在由 llama.cpp 联合加载主模型和外置 MTP；这可能需要几分钟…",
                "llama.cpp is loading the main model and external MTP together. This may take several minutes…");
            await operation.StageAsync("native_joint_load_and_short_inference");
            var verified = await ValidateExternalMtpWithNativeRouterAsync(
                model,
                runtimeRoot,
                relativePath,
                _lifetime.Token);

            if (!CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
            {
                throw new InvalidOperationException(AppLanguageManager.Choose(
                    "验证期间本地模型被启动，因此没有保存 MTP 关联。",
                    "The local model was started during validation, so the MTP association was not saved."));
            }

            await operation.StageAsync("register_validated_mtp");
            await Launcher.Scripts.Templates.ModelResourceCatalog.RegisterAsync(verified, runtimeRoot, Launcher.Scripts.Templates.ModelResourceKind.Mtp, _lifetime.Token);
            operation.Set("resourceRegistered", true);
            await ReloadProfilesAsync(runtimeRoot, _lifetime.Token);
            await operation.CompleteAsync();
            StatusText.Text = AppLanguageManager.Choose(
                $"llama.cpp 已验证 {Path.GetFileName(relativePath)} 可与 {verified.DisplayName} 联合加载；已登记到可选资源，已有配置未改变。",
                $"llama.cpp verified that {Path.GetFileName(relativePath)} loads with {verified.DisplayName}. Registered for selection; existing configurations are unchanged.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await operation.CompleteAsync("cancelled");
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception, "resource_validation_failed");
            StatusText.Text = AppLanguageManager.Choose(
                $"外置 MTP 未通过当前配置下的联合加载验证：{exception.Message}",
                $"The external MTP did not pass joint-load validation with the current configuration: {exception.Message}");
            MessageBox.Show(
                ResourceDialogOwner,
                StatusText.Text,
                AppLanguageManager.Choose("MTP 验证失败", "MTP Validation Failed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string ValidateExternalMtpSelection(
        ModelProfile profile,
        string runtimeRoot,
        string selectedFile)
    {
        var root = Path.GetFullPath(runtimeRoot);
        var modelsRoot = Path.GetFullPath(Path.Combine(root, "models")).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var selectedPath = Path.GetFullPath(selectedFile);
        var mainPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
        if (!selectedPath.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLanguageManager.Choose(
                "为保证配置可迁移和路径安全，外置 MTP 文件必须位于当前 Runtime 的 models 目录内。",
                "For portable configuration and path safety, the external MTP file must be inside the current Runtime models directory."));
        }

        if (string.Equals(selectedPath, mainPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLanguageManager.Choose(
                "主模型文件本身不能作为外置 MTP 文件加载。",
                "The main model file cannot also be loaded as an external MTP file."));
        }

        _ = GgufContextMetadataReader.ReadMetadata(selectedPath);
        return Path.GetRelativePath(root, selectedPath);
    }

    private static bool DetectEmbeddedMtp(ModelProfile profile, string runtimeRoot)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
            return File.Exists(path) && GgufContextMetadataReader.ReadMetadata(path).HasEmbeddedMtp;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException
                                          or OverflowException)
        {
            return false;
        }
    }

    private async Task<ModelProfile> ValidateExternalMtpWithNativeRouterAsync(
        ModelProfile profile,
        string runtimeRoot,
        string companionRelativePath,
        CancellationToken cancellationToken)
    {
        var capabilities = await LlamaRuntimeOptionDetector.DetectAsync(runtimeRoot, cancellationToken);
        if (capabilities is not null
            && (!capabilities.Contains("spec-type") || !capabilities.Contains("spec-draft-model")))
        {
            throw new NotSupportedException(AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 未报告 --spec-type 与 --spec-draft-model，无法验证外置 MTP。",
                "The current llama.cpp Runtime does not report --spec-type and --spec-draft-model, so external MTP cannot be validated."));
        }

        var basisBefore = MtpValidationFingerprint.Compute(profile with
        { MtpSource = MtpSourceKind.External, MtpDraftModelRelativePath = companionRelativePath }, runtimeRoot);
        var validating = ExternalMtpValidationProfile.Create(profile, companionRelativePath);

        var scriptsDirectory = Path.Combine(runtimeRoot, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        var presetPath = Path.Combine(scriptsDirectory, $".mtp-validation-{Guid.NewGuid():N}.ini");
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var port = ReserveAvailableLoopbackPort();
        try
        {
            await File.WriteAllTextAsync(
                presetPath,
                RouterPresetGenerator.Generate(validating, runtimeRoot, loadOnStartup: true),
                new UTF8Encoding(false),
                cancellationToken);

            using var healthHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            healthHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var healthClient = new RouterHealthClient(healthHttp);
            await using var manager = new LlamaRouterProcessManager(healthClient);
            var router = await manager.StartAsync(
                new LlamaRouterStartRequest
                {
                    ExecutablePath = Path.Combine(runtimeRoot, "llama-server.exe"),
                    WorkingDirectory = runtimeRoot,
                    LogDirectory = Path.Combine(runtimeRoot, "logs"),
                    ModelCacheDirectory = Path.Combine(runtimeRoot, "models"),
                    Options = new LlamaRouterOptions
                    {
                        Host = IPAddress.Loopback.ToString(),
                        Port = port,
                        ModelsDirectory = Path.Combine(runtimeRoot, "models"),
                        ModelsPresetPath = presetPath,
                        MaximumLoadedModels = 1,
                        AutoloadModels = true,
                        ApiKey = apiKey,
                        DisableMultimodalProjectorAutoDownload = true,
                    },
                    StartupTimeout = TimeSpan.FromMinutes(5),
                },
                cancellationToken);

            var modelReadyDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            var modelReady = false;
            while (DateTimeOffset.UtcNow < modelReadyDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await healthClient.ProbeAsync(router.BaseUri, cancellationToken);
                if (snapshot.IsHealthy
                    && snapshot.ModelIds.Contains(validating.Alias, StringComparer.Ordinal))
                {
                    modelReady = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            if (!modelReady)
            {
                throw new TimeoutException(AppLanguageManager.Choose(
                    "llama.cpp 已启动，但未在五分钟内确认主模型与外置 MTP 完成联合加载。",
                    "llama.cpp started but did not confirm joint loading of the main model and external MTP within five minutes."));
            }

            using var inferenceHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            inferenceHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var payload = JsonSerializer.Serialize(new
            {
                model = validating.Alias,
                prompt = "a",
                n_predict = 1,
                temperature = 0,
            });
            using var response = await inferenceHttp.PostAsync(
                new Uri(router.BaseUri, "completion"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var diagnostic = await response.Content.ReadAsStringAsync(cancellationToken);
                if (diagnostic.Length > 500)
                {
                    diagnostic = diagnostic[..500];
                }

                var message = AppLanguageManager.Choose(
                    $"llama.cpp 已启动但极短推理失败，HTTP {(int)response.StatusCode}：{diagnostic}",
                    $"llama.cpp started, but the short inference failed with HTTP {(int)response.StatusCode}: {diagnostic}");
                if ((int)response.StatusCode is 400 or 422) throw new ModelResourceValidationException(message);
                throw new InvalidOperationException(message);
            }

            var verified = validating with
            {
                MtpEnabled = false,
                MtpCapabilityStatus = MtpCapabilityStatus.Verified,
                MtpValidationSignature = null,
                MtpValidatedAtUtc = DateTimeOffset.UtcNow,
            };
            if (basisBefore != MtpValidationFingerprint.Compute(verified, runtimeRoot))
                throw new InvalidOperationException("MTP 检测期间模型、外挂或运行库已改变，请重新检测 / MTP validation source changed; check again.");
            return verified with
            {
                MtpValidationSignature = MtpValidationFingerprint.Compute(verified, runtimeRoot),
            };
        }
        finally
        {
            try
            {
                File.Delete(presetPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
