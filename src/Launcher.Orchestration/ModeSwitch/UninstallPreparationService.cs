using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.Startup;

namespace Launcher.Orchestration.ModeSwitch;

/// <summary>
/// Restores a usable official configuration before the installed binaries disappear.
/// User data and the inactive provider used for reading old Local tasks remain intact.
/// </summary>
public sealed class UninstallPreparationService(
    ISettingsStore settingsStore,
    IChatGptClientDetector clientDetector,
    ChatGptConfigTransactionService configTransactionService,
    LauncherDataPaths dataPaths,
    IUserRunEntryStore runEntryStore)
{
    public async Task PrepareAsync(string installedAppExecutablePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedAppExecutablePath);

        if (clientDetector.IsRunning())
        {
            throw new InvalidOperationException("请先完全关闭 ChatGPT Desktop，再卸载 Egg Launcher。");
        }

        var recovery = await new ModeRecoveryCoordinator(settingsStore, configTransactionService, dataPaths)
            .RecoverAsync(cancellationToken).ConfigureAwait(false);

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SelectedMode == ProviderMode.OpenAI
            && recovery.Snapshot is { Stage: not ConfigTransactionStage.Restored })
        {
            throw new InvalidOperationException("恢复记录仍指向 Local 配置，已停止卸载。");
        }
        if (settings.SelectedMode == ProviderMode.Local)
        {
            await new ModeSwitchCoordinator(
                settingsStore,
                clientDetector,
                configTransactionService,
                dataPaths).SwitchToOpenAIAsync(cancellationToken).ConfigureAwait(false);
        }

        settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.SelectedMode != ProviderMode.OpenAI)
        {
            throw new InvalidOperationException("OpenAI 配置未完成恢复，已中止卸载。");
        }

        RemoveMatchingStartupEntry(installedAppExecutablePath);
    }

    private void RemoveMatchingStartupEntry(string installedAppExecutablePath)
    {
        var expectedCommand = AgentStartupRegistration.BuildCommand(installedAppExecutablePath, "--startup");
        foreach (var valueName in new[]
                 {
                     AgentStartupRegistration.ValueName,
                     AgentStartupRegistration.LegacyValueName,
                 })
        {
            if (string.Equals(runEntryStore.Read(valueName), expectedCommand, StringComparison.OrdinalIgnoreCase))
            {
                runEntryStore.Delete(valueName);
            }
        }
    }
}
