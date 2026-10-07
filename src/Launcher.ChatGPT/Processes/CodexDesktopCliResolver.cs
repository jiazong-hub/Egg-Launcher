using System.Security.Cryptography;

namespace Launcher.ChatGPT.Processes;

public static class CodexDesktopCliResolver
{
    public static Task<string> ResolveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Desktop 配套 CLI 检测需要 Windows。");
        var installation = new ChatGptClientInstallationLocator().Locate()
            ?? throw new InvalidOperationException("未找到可信的 Desktop 安装，请检查客户端安装。");
        var source = Path.Combine(installation.PackageRootFolder, "app", "resources", "codex.exe");
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin");
        return ResolveFromDirectoriesAsync(source, cache, cancellationToken);
    }

    internal static async Task<string> ResolveFromDirectoriesAsync(string source, string cache, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(source))
            throw new InvalidOperationException("当前 Desktop 安装缺少配套 CLI，请检查客户端安装。");
        if (!Directory.Exists(cache))
            throw new InvalidOperationException("尚未找到 Desktop 运行副本，请先打开一次客户端完成初始化。");

        // A cache can retain older releases. Only use the CLI belonging to the selected Desktop.
        var sourceLength = new FileInfo(source).Length;
        await using var sourceStream = File.OpenRead(source);
        var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken).ConfigureAwait(false);
        foreach (var directory in Directory.GetDirectories(cache))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory, "codex.exe");
            try
            {
                if (!File.Exists(candidate) || new FileInfo(candidate).Length != sourceLength) continue;
                await using var stream = File.OpenRead(candidate);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (hash.AsSpan().SequenceEqual(sourceHash)) return candidate;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An inaccessible or disappearing old cache must not hide a matching copy.
            }
        }
        throw new InvalidOperationException("未找到与当前 Desktop 一致的 CLI 运行副本，请重启客户端刷新运行组件。");
    }
}
