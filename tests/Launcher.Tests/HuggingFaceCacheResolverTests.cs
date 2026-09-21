using Launcher.Models.Remote;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;

namespace Launcher.Tests;

public sealed class HuggingFaceCacheResolverTests
{
    [Fact]
    public void ResolveVariant_ValidatesNativeCacheAndReturnsSnapshotPath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cache = CreateCache(root, "org", "model");
            var modelPath = AddCachedFile(cache, "model-Q4_K_M.gguf", CreateGgufBytes(12));
            var variant = new HuggingFaceGgufVariant(
                "org/model",
                "Q4_K_M",
                "model-Q4_K_M.gguf",
                12,
                1,
                [new HuggingFaceGgufFile("model-Q4_K_M.gguf", 12)]);

            var candidate = new HuggingFaceCacheResolver().ResolveVariant(root, variant);

            Assert.Equal(Path.GetFullPath(modelPath), candidate.PrimaryPath);
            Assert.Equal("org/model:Q4_K_M", candidate.RemoteModelId);
            Assert.Equal(12, candidate.TotalSizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_FindsCacheSnapshotOnceAndKeepsOrdinaryGgufSupport()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cache = CreateCache(root, "org", "model");
            AddCachedFile(cache, "model-Q4_K_M.gguf", CreateGgufBytes(10));
            File.WriteAllBytes(Path.Combine(root, "manual-Q8_0.gguf"), [1, 2, 3]);

            var result = new GgufModelScanner().Scan(root);

            Assert.Equal(2, result.Models.Count);
            var cached = Assert.Single(result.Models, model => model.RemoteModelId is not null);
            Assert.Equal("org/model:Q4_K_M", cached.RemoteModelId);
            Assert.Single(result.Models, model => model.RemoteModelId is null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveVariant_RequiresEveryShard()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cache = CreateCache(root, "org", "sharded");
            AddCachedFile(cache, "model-Q4_K_M-00001-of-00002.gguf", CreateGgufBytes(8));
            var variant = new HuggingFaceGgufVariant(
                "org/sharded",
                "Q4_K_M",
                "model-Q4_K_M-00001-of-00002.gguf",
                16,
                2,
                [
                    new HuggingFaceGgufFile("model-Q4_K_M-00001-of-00002.gguf", 8),
                    new HuggingFaceGgufFile("model-Q4_K_M-00002-of-00002.gguf", 8),
                ]);

            var exception = Assert.Throws<InvalidDataException>(
                () => new HuggingFaceCacheResolver().ResolveVariant(root, variant));

            Assert.Contains("不存在", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PathIdentities_MatchSnapshotLinkAndBlobTarget()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cache = CreateCache(root, "org", "identity");
            var snapshotPath = AddCachedFile(cache, "identity-Q4_K_M.gguf", CreateGgufBytes(9));
            if ((File.GetAttributes(snapshotPath) & FileAttributes.ReparsePoint) == 0)
            {
                return;
            }

            var snapshotIdentities = HuggingFaceCacheResolver.GetPathIdentities(snapshotPath);
            var blobIdentities = HuggingFaceCacheResolver.GetPathIdentities(cache.LastBlobPath!);

            Assert.True(snapshotIdentities.Overlaps(blobIdentities));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManagedMatcher_ExcludesManualAndDownloadedModels()
    {
        var runtimeRoot = CreateTemporaryDirectory();
        try
        {
            var modelsRoot = Directory.CreateDirectory(Path.Combine(runtimeRoot, "models")).FullName;
            var manualPath = Path.Combine(modelsRoot, "manual.gguf");
            File.WriteAllBytes(manualPath, [1, 2, 3]);
            var profiles = new[]
            {
                new ModelProfile
                {
                    Id = "manual",
                    Alias = "manual",
                    DisplayName = "Manual",
                    ModelRelativePath = Path.GetRelativePath(runtimeRoot, manualPath),
                },
                new ModelProfile
                {
                    Id = "downloaded",
                    Alias = "downloaded",
                    DisplayName = "Downloaded",
                    ModelRelativePath = Path.Combine("models", "old-cache.gguf"),
                    RemoteModelId = "org/model:Q4_K_M",
                },
            };
            var manualCandidate = new GgufModelCandidate(
                manualPath,
                Path.GetRelativePath(modelsRoot, manualPath),
                "manual",
                3,
                1);
            var downloadedCandidate = new GgufModelCandidate(
                Path.Combine(modelsRoot, "new-cache.gguf"),
                "new-cache.gguf",
                "downloaded",
                10,
                1,
                "org/model:Q4_K_M",
                "org/model",
                "Q4_K_M");

            Assert.Equal("manual", ManagedModelCandidateMatcher.FindMatch(manualCandidate, runtimeRoot, profiles)?.Id);
            Assert.Equal("downloaded", ManagedModelCandidateMatcher.FindMatch(downloadedCandidate, runtimeRoot, profiles)?.Id);
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    private static CacheLayout CreateCache(string modelsRoot, string owner, string repository)
    {
        var repositoryRoot = Path.Combine(modelsRoot, $"models--{owner}--{repository}");
        var revision = new string('a', 40);
        var refs = Directory.CreateDirectory(Path.Combine(repositoryRoot, "refs")).FullName;
        var blobs = Directory.CreateDirectory(Path.Combine(repositoryRoot, "blobs")).FullName;
        var snapshot = Directory.CreateDirectory(Path.Combine(repositoryRoot, "snapshots", revision)).FullName;
        File.WriteAllText(Path.Combine(refs, "main"), revision);
        return new CacheLayout(blobs, snapshot);
    }

    private static string AddCachedFile(CacheLayout cache, string relativePath, byte[] contents)
    {
        var blobPath = Path.Combine(cache.BlobsRoot, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(blobPath, contents);
        cache.LastBlobPath = blobPath;
        var snapshotPath = Path.Combine(cache.SnapshotRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        try
        {
            File.CreateSymbolicLink(snapshotPath, Path.GetRelativePath(Path.GetDirectoryName(snapshotPath)!, blobPath));
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or PlatformNotSupportedException)
        {
            File.Copy(blobPath, snapshotPath);
        }

        return snapshotPath;
    }

    private static byte[] CreateGgufBytes(int length)
    {
        var bytes = new byte[Math.Max(4, length)];
        "GGUF"u8.CopyTo(bytes);
        return bytes;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record CacheLayout(string BlobsRoot, string SnapshotRoot)
    {
        public string? LastBlobPath { get; set; }
    }
}
