using Launcher.ChatGPT.Processes;

namespace Launcher.Tests;

public sealed class CodexDesktopCliResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EggLauncher.CliResolverTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolve_UsesMatchingDesktopCopyWithoutIndependentCliOrSandboxHelpers()
    {
        var source = Write("package/app/resources/codex.exe", "desktop-cli");
        Write("cache/old/codex.exe", "outdated---"); // Same length, different contents.
        Directory.CreateDirectory(Path.Combine(_root, "cache", "incomplete"));
        var expected = Write("cache/current/codex.exe", "desktop-cli");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "cache", "old", "codex.exe"), DateTime.UtcNow.AddDays(1));

        var actual = await CodexDesktopCliResolver.ResolveFromDirectoriesAsync(source, Path.Combine(_root, "cache"), CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Resolve_RejectsOldCacheEvenWhenIndependentCliMatches()
    {
        var source = Write("package/app/resources/codex.exe", "desktop-cli");
        Write("cache/old/codex.exe", "outdated---");
        Write("Programs/OpenAI/Codex/bin/codex.exe", "desktop-cli");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexDesktopCliResolver.ResolveFromDirectoriesAsync(source, Path.Combine(_root, "cache"), CancellationToken.None));

        Assert.Contains("与当前 Desktop 一致", error.Message);
    }

    [Fact]
    public async Task Resolve_ExplainsMissingDesktopComponent()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexDesktopCliResolver.ResolveFromDirectoriesAsync(Path.Combine(_root, "missing.exe"), Path.Combine(_root, "cache"), CancellationToken.None));
        Assert.Contains("安装缺少配套 CLI", error.Message);
    }

    [Fact]
    public async Task Resolve_ExplainsMissingRuntimeInitialization()
    {
        var source = Write("package/codex.exe", "desktop-cli");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexDesktopCliResolver.ResolveFromDirectoriesAsync(source, Path.Combine(_root, "cache"), CancellationToken.None));
        Assert.Contains("完成初始化", error.Message);
    }

    [Fact]
    public async Task Resolve_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexDesktopCliResolver.ResolveFromDirectoriesAsync(Path.Combine(_root, "missing.exe"), Path.Combine(_root, "cache"), cancellation.Token));
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
