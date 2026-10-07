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
    private async Task<ModelProfile> DetectEditorReasoningAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (_busy || HasActiveOrQueuedDownloads || !CanEditProfile(profile, forceClientRefresh: true))
            throw new InvalidOperationException(AppLanguageManager.Choose("当前模型或 Runtime 正在使用，暂时不能检测。", "The model or runtime is in use; detection is unavailable."));
        var conflict = GetModelDownloadBlockReason();
        if (!string.IsNullOrWhiteSpace(conflict)) throw new InvalidOperationException(conflict);
        var root = Path.GetFullPath(_settings.LlamaRoot!);
        if (!profile.Jinja) throw new InvalidOperationException(AppLanguageManager.Choose("请先在基础参数中开启 Jinja。", "Enable Jinja in Basic Parameters first."));
        var template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? Launcher.Scripts.Templates.CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(root, profile.ModelRelativePath))
            : await File.ReadAllTextAsync(Path.Combine(root, profile.ChatTemplateRelativePath), cancellationToken);
        if (string.IsNullOrWhiteSpace(template)) throw new InvalidOperationException("No active chat template was found.");
        if (string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            && Launcher.Scripts.Templates.ChatTemplateRules.Analyze(template).Kind == Launcher.Scripts.Templates.ChatTemplateMatchKind.Repairable)
            throw new InvalidOperationException(AppLanguageManager.Choose("请先在基础参数中完成模板检测与生成，再检测思考能力。", "Check and generate the compatibility template in Basic Parameters first."));
        if (profile.ExtraArguments.Keys.Any(key => key is "reasoning" or "reasoning-effort" or "reasoning-budget" or "chat-template-kwargs"))
            throw new InvalidOperationException(AppLanguageManager.Choose("请先移除高级参数中的思考覆盖，再执行检测。", "Remove reasoning overrides from Advanced Parameters before detection."));
        var options = await Launcher.Runtime.Detection.LlamaRuntimeOptionDetector.DetectAsync(root, cancellationToken);
        var canConfigureThinking = options?.Contains("reasoning") == true && options.Contains("reasoning-budget");
        var signature = ComputeReasoningCapabilitySignature(profile, root);
        var capability = await DetectReasoningCapabilityWithNativeRouterAsync(profile, root, template,
            options?.Contains("log-prompts-dir") == true, cancellationToken);
        var compatibility = await Launcher.ChatGPT.Catalog.CodexReasoningCompatibility.CheckAsync(capability.SupportedLevels, cancellationToken);
        if (!CanEditProfile(profile, forceClientRefresh: true) || signature != ComputeReasoningCapabilitySignature(profile, root))
            throw new InvalidOperationException(AppLanguageManager.Choose("检测期间模型、模板或运行时发生变化，请重新检测。", "The model, template or runtime changed during detection; retry."));
        var verified = capability.Status == LlamaReasoningCapabilityStatus.Verified && capability.SupportedLevels.Count > 0;
        var result = profile with
        {
            ReasoningCapabilityStatus = verified ? ReasoningCapabilityStatus.Verified : ReasoningCapabilityStatus.Unknown,
            SupportedReasoningLevels = verified ? capability.SupportedLevels.ToArray() : [],
            DefaultReasoningLevel = verified ? capability.DefaultLevel : null,
            ReasoningResponsesVerified = capability.ResponsesVerified,
            ReasoningClientCompatible = compatibility.Compatible,
            ReasoningClientExecutablePath = compatibility.ExecutablePath,
            SupportsThinkingSwitch = canConfigureThinking ? capability.SupportsThinkingSwitch : null,
            DefaultThinkingEnabled = capability.DefaultThinkingEnabled,
            ThinkingEnabled = canConfigureThinking && capability.SupportsThinkingSwitch == true ? profile.ThinkingEnabled : null,
            ReasoningLevelAliases = capability.Aliases,
            ReasoningValidationDetails = capability.Details + "\n" + compatibility.Detail + (canConfigureThinking ? "" : "\n当前运行时缺少思考启动参数，不能开放思考开关。"),
            ExposeReasoningEffortInChatGpt = verified && capability.ResponsesVerified && compatibility.Compatible == true && profile.ExposeReasoningEffortInChatGpt,
            ReasoningCapabilitySignature = signature,
            ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow,
        };
        return result with
        {
            ReasoningCapabilitySignature = ComputeReasoningCapabilitySignature(result, root),
            ReasoningValidationBasis = Launcher.Scripts.Templates.ReasoningValidationFingerprint.CaptureBasis(result, root),
        };
    }

    private static async Task<LlamaReasoningCapability> DetectReasoningCapabilityWithNativeRouterAsync(
        ModelProfile profile,
        string runtimeRoot,
        string template,
        bool canRecordPrompts,
        CancellationToken cancellationToken)
    {
        var isolatedProfile = profile with
        {
            ExposeReasoningEffortInChatGpt = false,
            MtpEnabled = false,
            VisionEnabled = false,
            ThinkingEnabled = null,
            ContextSize = 4096,
            CompactionSafetyReserve = 2048,
            ToolOutputTokenLimit = 1024,
            Parallel = 1,
            ContextShiftEnabled = false,
        };
        var scriptsDirectory = Path.Combine(runtimeRoot, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        var presetPath = Path.Combine(scriptsDirectory, $".reasoning-validation-{Guid.NewGuid():N}.ini");
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var port = ReserveAvailableLoopbackPort();
        var promptDirectory = Path.Combine(Path.GetTempPath(), "EggLauncher.ReasoningValidation", Guid.NewGuid().ToString("N"));
        if (canRecordPrompts)
        {
            Directory.CreateDirectory(promptDirectory);
            var arguments = isolatedProfile.ExtraArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            arguments["log-prompts-dir"] = promptDirectory;
            isolatedProfile = isolatedProfile with { ExtraArguments = arguments };
        }
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
            return await new LlamaReasoningNativeProbe(capabilityHttp, canRecordPrompts ? new NativePromptEvidence(promptDirectory) : null).ProbeAsync(
                router.BaseUri,
                isolatedProfile.Alias,
                template,
                cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(presetPath);
                if (Directory.Exists(promptDirectory)) Directory.Delete(promptDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
