using System.Text.Json;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Scripts.Templates;

namespace Launcher.Orchestration.Models;

public static class ReasoningStartupGuard
{
    public static async Task<ModelProfile> EnsureAsync(ModelProfile profile, LauncherSettings settings,
        ISettingsStore settingsStore, LauncherDataPaths paths, IChatGptClientDetector client, CancellationToken token)
    {
        if (profile.ThinkingEnabled is null && !profile.ExposeReasoningEffortInChatGpt) return profile;
        var check = ReasoningValidationState.Check(profile, settings.LlamaRoot!);
        if (check.State == ReasoningValidationStateKind.Current) return profile;
        if (client.IsRunning()) throw new InvalidOperationException("思考验证已过期；请关闭 Codex 后重新保存或检测思考设置。后台未沿用旧验证结果。");
        if (check.State == ReasoningValidationStateKind.Unreadable) throw new InvalidOperationException(check.Reason);
        if (!File.Exists(paths.RecoveryFile)) throw new InvalidOperationException("思考验证已过期且缺少配置恢复记录，请在启动器中重新保存设置。");
        var snapshot = JsonSerializer.Deserialize<ManagedConfigSnapshot>(await File.ReadAllTextAsync(paths.RecoveryFile, token).ConfigureAwait(false));
        var codexHome = snapshot is null ? null : Path.GetDirectoryName(snapshot.ConfigPath);
        if (codexHome is null) throw new InvalidOperationException("无法确定受管理的 Codex 配置位置，请重新保存设置。");
        var reset = check.ApplyTo(profile);
        var coordinator = new ModeSwitchCoordinator(settingsStore, client, new ChatGptConfigTransactionService(client), paths);
        await new ModelArtifactWriter(new JsonModelProfileStore(), new ModelArtifactTransaction()).SaveAsync(
            reset, settings.LlamaRoot!, paths.RouterPresetFile, paths.LocalModelCatalogFile, true, token,
            async (saved, cancellation) => await coordinator.SwitchToLocalAsync(new LocalModeSwitchRequest
            {
                CodexHome = codexHome,
                RuntimeRoot = settings.LlamaRoot!,
                Profile = saved,
                RouterPort = settings.RouterPort,
            }, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        return reset;
    }
}
