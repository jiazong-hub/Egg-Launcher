using Launcher.Models.Scanning;

namespace Launcher.Tests;

public sealed class GgufModelScannerTests
{
    [Fact]
    public void Scan_ExcludesMmprojAndReturnsStandaloneModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "coder-Q4_K_M.gguf"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "mmproj-coder-BF16.gguf"), [1]);

            var result = new GgufModelScanner().Scan(root);

            var model = Assert.Single(result.Models);
            Assert.Equal("coder-Q4_K_M", model.DisplayName);
            Assert.Equal(3, model.TotalSizeBytes);
            Assert.Single(result.ExcludedFiles);
            Assert.Contains("mmproj", result.ExcludedFiles[0].Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_WhenShardSetIsComplete_ReturnsOneModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "model-00001-of-00002.gguf"), [1, 2]);
            File.WriteAllBytes(Path.Combine(root, "model-00002-of-00002.gguf"), [3, 4, 5]);

            var result = new GgufModelScanner().Scan(root);

            var model = Assert.Single(result.Models);
            Assert.True(model.IsSharded);
            Assert.Equal(2, model.ShardCount);
            Assert.Equal(5, model.TotalSizeBytes);
            Assert.Empty(result.ExcludedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_ExcludesMtpCompanionFromStandaloneModels()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "coder-Q4_K_M.gguf"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "mtp-coder-Q4_K_M.gguf"), [4, 5]);

            var result = new GgufModelScanner().Scan(root);

            Assert.Single(result.Models);
            var excluded = Assert.Single(result.ExcludedFiles);
            Assert.Contains("MTP", excluded.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_ExcludesEveryDownloadedFileInsideLauncherMtpDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "coder-Q4_K_M.gguf"), [1, 2, 3]);
            var mtpRoot = Directory.CreateDirectory(Path.Combine(root, "egg-launcher-mtp", "org", "repo"));
            File.WriteAllBytes(Path.Combine(mtpRoot.FullName, "companion-Q4_K_M.gguf"), [4, 5]);

            var result = new GgufModelScanner().Scan(root);

            Assert.Single(result.Models);
            Assert.Single(result.ExcludedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_WhenShardSetIsIncomplete_ExcludesPartialModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "model-00001-of-00003.gguf"), [1]);
            File.WriteAllBytes(Path.Combine(root, "model-00003-of-00003.gguf"), [3]);

            var result = new GgufModelScanner().Scan(root);

            Assert.Empty(result.Models);
            Assert.Equal(2, result.ExcludedFiles.Count);
            Assert.All(result.ExcludedFiles, file => Assert.Contains("00002", file.Reason));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
