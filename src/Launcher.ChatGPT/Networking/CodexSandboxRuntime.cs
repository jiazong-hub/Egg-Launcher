using System.Security.Cryptography;
using Launcher.ChatGPT.Processes;

namespace Launcher.ChatGPT.Networking;

internal static class CodexSandboxRuntime
{
    private static readonly string[] RequiredFiles =
        ["codex.exe", "codex-windows-sandbox-setup.exe", "codex-command-runner.exe"];

    public static async Task<string> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("此检测需要 Windows Codex 沙箱。");
        var source = new ChatGptClientInstallationLocator().LocateAll()
            .Select(installation => Path.Combine(installation.PackageRootFolder, "app", "resources"))
            .FirstOrDefault(directory => RequiredFiles.All(file => File.Exists(Path.Combine(directory, file))));
        if (source is null)
            throw new InvalidOperationException("未找到官方 Codex 安装中的完整沙箱组件，请检查客户端安装。");

        // MSIX resources are readable but not necessarily directly executable by an unpackaged app.
        // Reuse Codex's own runnable distribution; never mix cached copies from different versions.
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        if (!Directory.Exists(cache))
            throw new InvalidOperationException("尚未找到 Codex 的完整运行副本。请先打开一次官方客户端完成运行时初始化；无需启动本地模型。");
        var candidates = Directory.GetDirectories(cache)
            .Where(directory => RequiredFiles.All(file => File.Exists(Path.Combine(directory, file))))
            .ToArray();
        var hashes = new Dictionary<string, byte[]>();
        foreach (var file in RequiredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(Path.Combine(source, file));
            hashes[file] = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        foreach (var directory in candidates)
        {
            var matches = true;
            foreach (var file in RequiredFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Path.Combine(directory, file);
                if (new FileInfo(candidate).Length != new FileInfo(Path.Combine(source, file)).Length)
                {
                    matches = false;
                    break;
                }
                await using var stream = File.OpenRead(candidate);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!hash.AsSpan().SequenceEqual(hashes[file]))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return directory;
        }
        throw new InvalidOperationException("未找到与官方安装版本一致的完整 Codex 运行副本。请重启官方客户端刷新运行时，再检查联网；无需启动本地模型。");
    }
}
