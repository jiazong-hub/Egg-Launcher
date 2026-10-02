using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Models.Profiles;
using Launcher.Runtime.Router;
using Launcher.Scripts.RouterPreset;
using Launcher.Core.Diagnostics;

namespace Launcher.App;

public partial class MainWindow
{
    private async Task<ContextShiftCapabilityResult> DetectContextShiftAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var root = _settings.LlamaRoot ?? throw new InvalidOperationException("Runtime is not configured.");
        var conflict = GetModelDownloadBlockReason();
        if (HasActiveOrQueuedDownloads || !string.IsNullOrWhiteSpace(conflict)
            || !CanEditProfile(profile, forceClientRefresh: true))
            throw new InvalidOperationException(AppLanguageManager.Choose(
                "当前 Runtime 正在使用。请结束任务并关闭本地模型客户端后重新检测；不会中断正在运行的任务。",
                "The runtime is in use. Finish the task and close the local model client before detecting support. Running tasks will not be interrupted."));

        var directory = Path.Combine(root, "scripts");
        Directory.CreateDirectory(directory);
        var preset = Path.Combine(directory, $".context-shift-validation-{Guid.NewGuid():N}.ini");
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var validating = profile with { ContextShiftEnabled = true, IdleSleepSeconds = -1 };
        // The idle timeout does not affect memory capabilities; all context, backend and MTP settings are preserved.
        try
        {
            await File.WriteAllTextAsync(preset, RouterPresetGenerator.Generate(validating, root, true),
                new UTF8Encoding(false), cancellationToken);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(12) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var health = new RouterHealthClient(http);
            var log = new ModeAwareJsonLineDiagnosticLog(_paths.AgentConciseLogFile, _paths.AgentFullLogFile,
                _settings.DetailedDiagnosticsEnabled);
            await using var manager = new LlamaRouterProcessManager(health,
                diagnosticEventWriter: diagnostic => log.AppendEventAsync(diagnostic), diagnosticRunIdChanged: log.SetRouterRunId);
            manager.SetDetailedDiagnosticsEnabled(_settings.DetailedDiagnosticsEnabled);
            var router = await manager.StartAsync(new LlamaRouterStartRequest
            {
                ExecutablePath = Path.Combine(root, "llama-server.exe"),
                WorkingDirectory = root,
                LogDirectory = _paths.LogsDirectory,
                ContextShiftRequested = true,
                ContextShiftDisabledObserver = reason => ContextShiftCapabilityCache.Write(profile, root,
                    new ContextShiftCapabilityResult(false, reason, DateTimeOffset.UtcNow)),
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

            var propsUri = new Uri(router.BaseUri, "props?model=" + Uri.EscapeDataString(profile.Alias));
            using var propsResponse = await http.GetAsync(propsUri, cancellationToken);
            propsResponse.EnsureSuccessStatusCode();
            using var props = JsonDocument.Parse(await propsResponse.Content.ReadAsStringAsync(cancellationToken));
            var slotContext = props.RootElement.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
            if (slotContext < 8) throw new InvalidDataException("No usable native slot context was reported.");

            if (manager.ContextShiftDisabledReason is null)
            {
                using var tokenizeResponse = await http.PostAsync(new Uri(router.BaseUri, "tokenize"),
                    new StringContent(JsonSerializer.Serialize(new { model = profile.Alias, content = " hello", add_special = false }),
                        Encoding.UTF8, "application/json"), cancellationToken);
                tokenizeResponse.EnsureSuccessStatusCode();
                using var tokens = JsonDocument.Parse(await tokenizeResponse.Content.ReadAsStringAsync(cancellationToken));
                var token = tokens.RootElement.GetProperty("tokens").EnumerateArray().Last().GetInt32();
                // A bounded probe, not a chat: fill the slot with token IDs and generate six tokens across its limit.
                var prompt = Enumerable.Repeat(token, slotContext - 2).ToArray();
                using var completion = await http.PostAsync(new Uri(router.BaseUri, "completion"),
                    new StringContent(JsonSerializer.Serialize(new
                    {
                        model = profile.Alias,
                        prompt,
                        n_predict = 6,
                        ignore_eos = true,
                        temperature = 0,
                        stream = false,
                        cache_prompt = false,
                    }), Encoding.UTF8, "application/json"), cancellationToken);
                completion.EnsureSuccessStatusCode();
                // Wait for log pumps to drain before deciding whether a real shift occurred.
            }
            await manager.StopAsync(CancellationToken.None);
            var result = manager.ContextShiftDisabledReason is string reason
                ? new ContextShiftCapabilityResult(false, AppLanguageManager.Choose(
                    "llama.cpp 加载当前配置时禁用了滚动：", "llama.cpp disabled shifting for this configuration: ") + reason, DateTimeOffset.UtcNow)
                : manager.ContextShiftObserved
                    ? new ContextShiftCapabilityResult(true, AppLanguageManager.Choose(
                        "已验证：llama.cpp 在当前配置下实际执行了上下文滚动。", "Verified: llama.cpp performed context shifting with this configuration."), DateTimeOffset.UtcNow)
                    : new ContextShiftCapabilityResult(null, AppLanguageManager.Choose(
                        "未取得实际滚动证据，保持关闭；可重新检测。", "No actual shifting evidence was obtained. Remains off; detection can be retried."), DateTimeOffset.UtcNow);
            if (result.Supported is not null) ContextShiftCapabilityCache.Write(profile, root, result);
            return result;
        }
        finally
        {
            try { File.Delete(preset); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
