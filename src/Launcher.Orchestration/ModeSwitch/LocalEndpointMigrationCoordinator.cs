using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;

namespace Launcher.Orchestration.ModeSwitch;

public sealed class LocalEndpointMigrationCoordinator(
    ISettingsStore settingsStore,
    IChatGptClientDetector clientDetector,
    ChatGptConfigTransactionService configTransactionService,
    LauncherDataPaths dataPaths) : ILocalEndpointMigrationCoordinator
{
    public async Task<LauncherSettings> MigrateAsync(
        LauncherSettings expectedSettings,
        Uri publicBaseUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSettings);
        ArgumentNullException.ThrowIfNull(publicBaseUri);
        if (publicBaseUri.Scheme != Uri.UriSchemeHttp
            || publicBaseUri.Port is < 1 or > 65535
            || !System.Net.IPAddress.TryParse(publicBaseUri.Host, out var host)
            || !System.Net.IPAddress.IsLoopback(host))
        {
            throw new ArgumentException("Local 连接端点必须是有效的 HTTP 回环地址。", nameof(publicBaseUri));
        }

        await using var modeLock = await ModeSwitchFileLock.AcquireAsync(
            dataPaths.ModeSwitchLockFile,
            cancellationToken).ConfigureAwait(false);
        var current = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current != expectedSettings || current.SelectedMode != ProviderMode.Local)
        {
            throw new InvalidOperationException("端口迁移期间 Launcher 设置已改变，已停止迁移。");
        }

        if (clientDetector.IsRunning())
        {
            throw new InvalidOperationException(
                "ChatGPT Desktop 正在运行，不能迁移 Local 连接端口。请关闭客户端后重试。");
        }

        var target = current with
        {
            RouterPort = publicBaseUri.Port,
            PublicProxyPortInitialized = true,
        };
        if (target == current)
        {
            return current;
        }

        await configTransactionService.UpdateLocalEndpointAsync(
            dataPaths.RecoveryFile,
            new Uri(publicBaseUri, "v1/"),
            current,
            target,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await settingsStore.SaveAsync(target, cancellationToken).ConfigureAwait(false);
            await configTransactionService.CommitLocalUpdateAsync(
                dataPaths.RecoveryFile,
                cancellationToken).ConfigureAwait(false);
            return target;
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
                await settingsStore.SaveAsync(current, CancellationToken.None).ConfigureAwait(false);
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
                    "Local 端口迁移失败且未能完整回滚，请勿启动 ChatGPT 并检查恢复记录。",
                    rollbackErrors);
            }

            throw;
        }
    }
}
