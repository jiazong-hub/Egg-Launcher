using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Core.Configuration;
using Launcher.Runtime.Router;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class MainWindow
{
    private async Task<CodexChatTemplateCompatibilityResult> PrepareChatTemplateAsync(
        ModelProfile profile, string root, CancellationToken cancellationToken, bool explicitCheck = false,
        string? stagedFileName = null)
    {
        // Artifact transactions resume on a worker thread after taking file snapshots.
        // This flow reads UI state and owns dialogs, so enter through the UI dispatcher.
        if (!Dispatcher.CheckAccess())
        {
            return await Dispatcher.InvokeAsync(
                () => PrepareChatTemplateAsync(profile, root, cancellationToken, explicitCheck, stagedFileName),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken).Task.Unwrap();
        }

        if (!profile.Jinja || (!explicitCheck && !string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)))
            return new(profile, false);
        if (ChatTemplateValidationCache.IsCurrent(profile, root) && !explicitCheck) return new(profile, false);
        var source = await Task.Run(() => CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(
            Path.Combine(root, profile.ModelRelativePath), cancellationToken), cancellationToken);
        var analysis = ChatTemplateRules.Analyze(source);
        var generate = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            && analysis.Kind == ChatTemplateMatchKind.Repairable;
        if (!generate && !explicitCheck) return new(profile, false);

        if (GetClientRunningSnapshot(force: true) || HasActiveOrQueuedDownloads
            || !string.IsNullOrWhiteSpace(GetModelDownloadBlockReason()))
            throw new InvalidOperationException(AppLanguageManager.Choose(
                "Runtime 正在使用。请关闭客户端并停止本地服务及下载后检测模板。",
                "The runtime is in use. Close the client and stop local services and downloads before checking the template."));
        if (MessageBox.Show(this, AppLanguageManager.Choose(
                generate
                    ? "发现已支持的模板位置限制。是否生成并验证兼容模板？\n将临时加载模型，占用内存和显存；检查通过后才填写模板路径。"
                    : "是否使用 llama.cpp 检查当前模板？\n将临时加载模型。检查只验证渲染，不代表实际对话、工具调用或上下文压缩已经验证。",
                generate
                    ? "A supported message-position restriction was found. Generate and validate a compatible template?\nThe model will load temporarily. The template path is selected only after checks pass."
                    : "Check the current template with llama.cpp?\nThe model will load temporarily. Rendering checks do not verify inference, tool execution or compaction."),
                AppLanguageManager.Choose("模板检测", "Template Check"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return new(profile, false);

        StatusText.Text = AppLanguageManager.Choose("正在隔离加载模型并检查模板渲染…", "Loading the model in isolation and checking template rendering…");

        var directory = Path.Combine(root, "scripts", "templates");
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, $".template-check-{Guid.NewGuid():N}.jinja");
        var selectedText = generate ? CodexChatTemplateCompatibility.CreateOwnedOutput(source!, analysis.RepairedTemplate!)
            : string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath) ? source
            : await File.ReadAllTextAsync(Path.Combine(root, profile.ChatTemplateRelativePath), cancellationToken);
        if (string.IsNullOrWhiteSpace(selectedText)) throw new InvalidDataException("No chat template was found.");
        try
        {
            await File.WriteAllTextAsync(candidate, selectedText, new UTF8Encoding(false), cancellationToken);
            var checkedProfile = profile with { ChatTemplateRelativePath = Path.GetRelativePath(root, candidate) };
            var signatureBefore = ChatTemplateValidationCache.Signature(profile, root);
            var result = await ValidateChatTemplateWithNativeRouterAsync(checkedProfile, root, cancellationToken, analysis.ToolMarkers);
            if (!result.Passed)
                throw new InvalidOperationException(AppLanguageManager.Choose(
                    (result.Unavailable ? "模板检查未完成：" : "模板检查失败：") + result.Details,
                    "Template check " + (result.Unavailable ? "unavailable: " : "failed: ") + result.Details));
            if (GetClientRunningSnapshot(force: true) || !CanEditProfile(profile, forceClientRefresh: true))
                throw new InvalidOperationException("The client was started during validation; changes were not applied.");
            // Do not commit a result if the underlying source changed during the native check.
            var currentText = generate || string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
                ? CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(root, profile.ModelRelativePath), cancellationToken)
                : await File.ReadAllTextAsync(Path.Combine(root, profile.ChatTemplateRelativePath), cancellationToken);
            if (!string.Equals(currentText, generate ? source : selectedText, StringComparison.Ordinal)
                || signatureBefore != ChatTemplateValidationCache.Signature(profile, root))
                throw new InvalidOperationException("The template changed during validation. Check it again.");
            var prepared = generate
                ? await CodexChatTemplateCompatibility.EnsureAsync(profile, root, cancellationToken,
                    stagedFileName)
                : new CodexChatTemplateCompatibilityResult(profile, false);
            await ChatTemplateValidationCache.WriteAsync(prepared.Profile, root, cancellationToken);
            return prepared;
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }

    private async Task<ModelProfile> CheckEditorChatTemplateAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var root = _settings.LlamaRoot ?? throw new InvalidOperationException("Runtime is not configured.");
        // Editor cancellation must never alter the file referenced by the saved profile.
        var stagedFileName = $"{profile.Id}.{Guid.NewGuid():N}.codex-compatible.jinja";
        return (await _modelArtifactTransaction.ExecuteAsync(root, profile.Id, _paths.RouterPresetFile, false,
            token => PrepareChatTemplateAsync(profile, root, token, explicitCheck: true, stagedFileName), cancellationToken,
            [Path.Combine(root, "scripts", "templates", stagedFileName)])).Profile;
    }

    private async Task<ChatTemplateRenderResult> ValidateChatTemplateWithNativeRouterAsync(
        ModelProfile profile, string root, CancellationToken cancellationToken, IReadOnlyList<string>? toolMarkers)
    {
        var validating = profile with
        {
            ContextSize = 4096,
            CompactionSafetyReserve = 2048,
            ToolOutputTokenLimit = 1024,
            Parallel = 1,
            MtpEnabled = false,
            VisionEnabled = false,
            ContextShiftEnabled = false,
            ExposeReasoningEffortInChatGpt = false,
            IdleSleepSeconds = -1,
        };
        var preset = Path.Combine(root, "scripts", $".template-validation-{Guid.NewGuid():N}.ini");
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await File.WriteAllTextAsync(preset, RouterPresetGenerator.Generate(validating, root, true), new UTF8Encoding(false), cancellationToken);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            await using var manager = new LlamaRouterProcessManager(new RouterHealthClient(http));
            manager.SetDetailedDiagnosticsEnabled(_settings.DetailedDiagnosticsEnabled);
            var router = await manager.StartAsync(new LlamaRouterStartRequest
            {
                ExecutablePath = Path.Combine(root, "llama-server.exe"),
                WorkingDirectory = root,
                LogDirectory = _paths.LogsDirectory,
                Options = new LlamaRouterOptions
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = ReserveAvailableLoopbackPort(),
                    ModelsPresetPath = preset,
                    MaximumLoadedModels = 1,
                    AutoloadModels = true,
                    ApiKey = apiKey,
                    DisableMultimodalProjectorAutoDownload = true,
                },
                StartupTimeout = TimeSpan.FromMinutes(5),
            }, cancellationToken);
            return await new LlamaChatTemplateValidationClient(http).ValidateAsync(router.BaseUri, profile.Alias, cancellationToken, toolMarkers);
        }
        finally { if (File.Exists(preset)) File.Delete(preset); }
    }

    private string? _templateErrorRun;
    private async Task CheckRuntimeTemplateErrorAsync()
    {
        if (_busy || _settings.SelectedMode != ProviderMode.Local || !File.Exists(_paths.RuntimeStateFile)) return;
        try
        {
            var state = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(_paths.RuntimeStateFile));
            if (state?.AgentStartedAtUtc is not { } started || !IsFreshRuntimeState(state.UpdatedAtUtc)
                || state.Phase != RuntimePhase.Running || state.SelectedMode != ProviderMode.Local) return;
            var run = started.ToString("O");
            if (_templateErrorRun == run || !File.Exists(_paths.ProxyLogFile)) return;
            using var stream = new FileStream(_paths.ProxyLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 64 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var tail = await reader.ReadToEndAsync();
            foreach (var line in tail.Split('\n').Reverse())
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var entry = document.RootElement;
                    if (entry.TryGetProperty("errorCategory", out var category) && category.GetString() == "chat_template_message_order"
                        && entry.TryGetProperty("timestampUtc", out var time) && time.GetDateTimeOffset() >= started)
                    {
                        _templateErrorRun = run;
                        StatusText.Text = AppLanguageManager.Choose(
                            "当前模板拒绝了系统／开发者消息的位置。请停止会话后，在模型参数中执行“检测与生成”。",
                            "The template rejected system/developer message order. Stop the session and use Check and Generate in model parameters.");
                        break;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
    }
}
