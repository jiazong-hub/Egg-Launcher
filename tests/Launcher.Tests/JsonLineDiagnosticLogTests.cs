using System.Text.Json;
using Launcher.Core.Diagnostics;

namespace Launcher.Tests;

public sealed class JsonLineDiagnosticLogTests
{
    [Fact]
    public async Task AppendAsync_CreatesDirectoryAndWritesOneJsonObjectPerLine()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ChatGPTLocalLauncher.Tests",
            Guid.NewGuid().ToString("N"));
        var logPath = System.IO.Path.Combine(root, "nested", "agent.log");

        try
        {
            var log = new JsonLineDiagnosticLog(logPath);

            await log.AppendAsync("info", "first");
            await log.AppendAsync("error", "second");

            var lines = await File.ReadAllLinesAsync(logPath);
            Assert.Equal(2, lines.Length);

            using var first = JsonDocument.Parse(lines[0]);
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("info", first.RootElement.GetProperty("level").GetString());
            Assert.Equal("first", first.RootElement.GetProperty("message").GetString());
            Assert.Equal("error", second.RootElement.GetProperty("level").GetString());
            Assert.Equal("second", second.RootElement.GetProperty("message").GetString());
            Assert.Equal(System.IO.Path.GetFullPath(logPath), log.Path);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppendAsync_WhenSizeLimitIsReached_RotatesOldLog()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ChatGPTLocalLauncher.Tests",
            Guid.NewGuid().ToString("N"));
        var logPath = System.IO.Path.Combine(root, "agent.log");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(logPath, new string('x', 128));
            var log = new JsonLineDiagnosticLog(logPath, maximumBytes: 64, retainedFiles: 2);

            await log.AppendAsync("info", "after-rotation");

            Assert.True(File.Exists(logPath + ".1"));
            Assert.Contains("after-rotation", await File.ReadAllTextAsync(logPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
