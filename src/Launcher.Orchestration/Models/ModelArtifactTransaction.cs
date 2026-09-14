using System.Text;
using Launcher.Models.Profiles;
using Launcher.Scripts.Batch;

namespace Launcher.Orchestration.Models;

public sealed class ModelArtifactTransaction
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public async Task<T> ExecuteAsync<T>(
        string runtimeRoot,
        string profileId,
        string routerPresetPath,
        bool includeRouterPreset,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(routerPresetPath);
        ArgumentNullException.ThrowIfNull(action);

        var root = Path.GetFullPath(runtimeRoot);
        var scriptsDirectory = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        await using var transactionLock = await AcquireLockAsync(
            Path.Combine(scriptsDirectory, ".launcher-artifacts.lock"),
            cancellationToken).ConfigureAwait(false);
        var targets = new List<string>
        {
            Path.Combine(JsonModelProfileStore.GetProfilesDirectory(root), profileId + ".json"),
            Path.Combine(JsonModelProfileStore.GetProfilesDirectory(root), profileId + ".json.bak"),
            BatchScriptGenerator.GetOutputPath(root, profileId),
            Path.Combine(root, "scripts", "templates", profileId + ".codex-compatible.jinja"),
        };
        if (includeRouterPreset)
        {
            targets.Add(Path.GetFullPath(routerPresetPath));
        }

        var snapshots = new List<FileSnapshot>(targets.Count);
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            snapshots.Add(await CaptureAsync(target, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception operationException)
        {
            try
            {
                foreach (var snapshot in snapshots.AsEnumerable().Reverse())
                {
                    await RestoreAsync(snapshot).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "模型配置写入失败，并且文件回滚未能完整完成。",
                    operationException,
                    rollbackException);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationException).Throw();
            throw;
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + LockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<FileSnapshot> CaptureAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? new FileSnapshot(
                fullPath,
                Exists: true,
                await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false))
            : new FileSnapshot(fullPath, Exists: false, null);
    }

    private static async Task RestoreAsync(FileSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            if (File.Exists(snapshot.Path))
            {
                File.Delete(snapshot.Path);
            }

            return;
        }

        var directory = Path.GetDirectoryName(snapshot.Path)
            ?? throw new InvalidOperationException("模型配置文件缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(snapshot.Path)}.{Guid.NewGuid():N}.rollback.tmp");
        try
        {
            await File.WriteAllBytesAsync(
                temporaryPath,
                snapshot.Content!,
                CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryPath, snapshot.Path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original operation/rollback failure.
        }
    }

    private sealed record FileSnapshot(string Path, bool Exists, byte[]? Content);
}
