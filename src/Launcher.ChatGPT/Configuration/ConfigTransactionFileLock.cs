namespace Launcher.ChatGPT.Configuration;

internal static class ConfigTransactionFileLock
{
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<FileStream> AcquireAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("配置事务锁必须位于一个目录中。");
        Directory.CreateDirectory(directory);
        var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    fullPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    "另一个 Launcher 或 Agent 正在修改 ChatGPT 配置或恢复记录，请稍后重试。",
                    exception);
            }
        }
    }

    public static string ForRecoveryPath(string recoveryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPath);
        var fullPath = Path.GetFullPath(recoveryPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("恢复记录必须位于一个目录中。");
        return Path.Combine(directory, "config-transaction.lock");
    }
}
