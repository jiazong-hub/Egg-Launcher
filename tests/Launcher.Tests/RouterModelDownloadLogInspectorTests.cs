using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class RouterModelDownloadLogInspectorTests
{
    [Fact]
    public async Task FindFailure_MapsNativeConnectionFailureWithoutEchoingRawLog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-download-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stdout = Path.Combine(root, "stdout.log");
            var stderr = Path.Combine(root, "stderr.log");
            await File.WriteAllTextAsync(stdout, string.Empty);
            await File.WriteAllTextAsync(
                stderr,
                "secret-path-value\nget_repo_commit: error: HTTPLIB failed: Could not establish connection\n");
            var processInfo = new LlamaRouterProcessInfo(
                42,
                DateTimeOffset.UtcNow,
                new Uri("http://127.0.0.1:12345/"),
                stdout,
                stderr,
                new RouterHealthSnapshot(true, 200, 200, [], null));

            var diagnostic = RouterModelDownloadLogInspector.FindFailure(processInfo);

            Assert.Contains("无法连接 Hugging Face", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-path-value", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindFailure_WhenLogsAreUnavailable_ReturnsNull()
    {
        var processInfo = new LlamaRouterProcessInfo(
            42,
            DateTimeOffset.UtcNow,
            new Uri("http://127.0.0.1:12345/"),
            "missing.stdout.log",
            "missing.stderr.log",
            new RouterHealthSnapshot(true, 200, 200, [], null));

        Assert.Null(RouterModelDownloadLogInspector.FindFailure(processInfo));
    }
}
