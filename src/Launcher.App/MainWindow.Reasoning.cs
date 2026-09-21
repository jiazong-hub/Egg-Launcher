using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Runtime.Router;
using Launcher.Scripts.RouterPreset;

namespace Launcher.App;

public partial class MainWindow
{
    private async void DetectReasoningCapabilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        if (HasActiveOrQueuedDownloads)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    "请等待当前单文件下载任务结束后再检测思考档位。",
                    "Wait for the current single-file download task to finish before detecting reasoning levels."),
                AppLanguageManager.Choose("Runtime 正在使用", "Runtime Is Busy"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var conflict = GetModelDownloadBlockReason();
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"当前不能启动隔离检测：{conflict}\n\n请先关闭正在运行的本地模型，然后重新检测。",
                    $"Isolated detection cannot start now: {conflict}\n\nStop the running local model and try again."),
                AppLanguageManager.Choose("暂时不能检测思考档位", "Reasoning Detection Is Unavailable"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"将使用 llama.cpp 临时加载 {selected.Profile.DisplayName}，并读取模型明确报告的思考强度档位。\n\n检测可能需要几分钟，并会占用正常加载模型所需的内存和显存；不会切换当前模式，也不会自动开启思考强度。继续吗？",
                    $"llama.cpp will temporarily load {selected.Profile.DisplayName} and read the reasoning-effort levels explicitly reported by the model.\n\nDetection may take several minutes and uses the memory and VRAM normally required to load the model. It will not switch the current mode or enable reasoning effort automatically. Continue?"),
                AppLanguageManager.Choose("检测思考档位", "Detect Reasoning Levels"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var runtimeRoot = Path.GetFullPath(_settings.LlamaRoot);
            var profile = await InvalidateStaleReasoningCapabilityAsync(
                selected.Profile,
                runtimeRoot,
                _lifetime.Token);
            StatusText.Text = AppLanguageManager.Choose(
                $"正在由 llama.cpp 隔离加载 {profile.DisplayName} 并读取思考档位；这可能需要几分钟…",
                $"llama.cpp is loading {profile.DisplayName} in isolation and reading reasoning levels. This may take several minutes…");

            var capability = await DetectReasoningCapabilityWithNativeRouterAsync(
                profile,
                runtimeRoot,
                _lifetime.Token);

            if (!CanEditProfile(selected.Profile, forceClientRefresh: true))
            {
                throw new InvalidOperationException(AppLanguageManager.Choose(
                    "检测期间本地模型被启动，因此没有保存思考档位结果。",
                    "A local model was started during detection, so the reasoning-level result was not saved."));
            }

            var verified = capability.Status == LlamaReasoningCapabilityStatus.Verified
                && capability.SupportedLevels.Count > 0;
            var updated = profile with
            {
                ReasoningCapabilityStatus = capability.Status switch
                {
                    LlamaReasoningCapabilityStatus.Unsupported => ReasoningCapabilityStatus.Unsupported,
                    LlamaReasoningCapabilityStatus.SupportedLevelsUnknown => ReasoningCapabilityStatus.SupportedLevelsUnknown,
                    LlamaReasoningCapabilityStatus.Verified when verified => ReasoningCapabilityStatus.Verified,
                    _ => ReasoningCapabilityStatus.Unknown,
                },
                SupportedReasoningLevels = verified
                    ? capability.SupportedLevels.ToArray()
                    : Array.Empty<string>(),
                DefaultReasoningLevel = verified ? capability.DefaultLevel : null,
                ExposeReasoningEffortInChatGpt = verified && profile.ExposeReasoningEffortInChatGpt,
                ReasoningCapabilitySignature = ComputeReasoningCapabilitySignature(profile, runtimeRoot),
                ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow,
            };

            await SaveProfileArtifactsAsync(updated, _lifetime.Token);
            await ReloadProfilesAsync(runtimeRoot, _lifetime.Token);
            StatusText.Text = FormatReasoningDetectionResult(updated);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"思考档位检测失败：{exception.Message}",
                $"Reasoning-level detection failed: {exception.Message}");
            MessageBox.Show(
                this,
                StatusText.Text,
                AppLanguageManager.Choose("思考档位检测失败", "Reasoning Detection Failed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string FormatReasoningDetectionResult(ModelProfile profile) =>
        profile.ReasoningCapabilityStatus switch
        {
            ReasoningCapabilityStatus.Verified => AppLanguageManager.Choose(
                $"已确认思考档位：{string.Join("、", profile.SupportedReasoningLevels)}。检测未改变现有开关状态，可在模型参数中开启或关闭。",
                $"Verified reasoning levels: {string.Join(", ", profile.SupportedReasoningLevels)}. Detection did not change the current setting; enable or disable it in model parameters."),
            ReasoningCapabilityStatus.Unsupported => AppLanguageManager.Choose(
                "llama.cpp 已明确报告该模型不支持思考强度调节；参数开关保持禁用。",
                "llama.cpp explicitly reported that this model does not support reasoning-effort selection. The setting remains disabled."),
            ReasoningCapabilityStatus.SupportedLevelsUnknown => AppLanguageManager.Choose(
                "llama.cpp 报告模型支持思考强度，但没有提供可验证的完整档位；参数开关保持禁用。",
                "llama.cpp reported reasoning-effort support but did not provide a verifiable complete level list. The setting remains disabled."),
            _ => AppLanguageManager.Choose(
                "llama.cpp 没有返回可验证的思考强度档位；参数开关保持禁用。",
                "llama.cpp did not return verifiable reasoning-effort levels. The setting remains disabled."),
        };

    private static async Task<LlamaReasoningCapability> DetectReasoningCapabilityWithNativeRouterAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var isolatedProfile = profile with
        {
            ExposeReasoningEffortInChatGpt = false,
            MtpEnabled = false,
            VisionEnabled = false,
        };
        var scriptsDirectory = Path.Combine(runtimeRoot, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        var presetPath = Path.Combine(scriptsDirectory, $".reasoning-validation-{Guid.NewGuid():N}.ini");
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var port = ReserveAvailableLoopbackPort();
        try
        {
            await File.WriteAllTextAsync(
                presetPath,
                RouterPresetGenerator.Generate(isolatedProfile, runtimeRoot, loadOnStartup: true),
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

            using var capabilityHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            capabilityHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await new LlamaReasoningCapabilityClient(capabilityHttp).ProbeAsync(
                router.BaseUri,
                isolatedProfile.Alias,
                cancellationToken);
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
