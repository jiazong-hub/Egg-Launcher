using System.Globalization;
using System.Text.RegularExpressions;
using Launcher.Models.Remote;

namespace Launcher.Models.Scanning;

public sealed partial class HuggingFaceCacheResolver
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    public GgufModelCandidate ResolveVariant(
        string modelsRoot,
        HuggingFaceGgufVariant variant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentNullException.ThrowIfNull(variant);

        var root = Path.GetFullPath(modelsRoot);
        var repositoryRoot = GetRepositoryRoot(root, variant.RepositoryId);
        var snapshotRoot = GetActiveSnapshotRoot(repositoryRoot);
        var expectedFiles = ExpandExpectedFiles(variant);
        if (expectedFiles.Count != Math.Max(1, variant.ShardCount))
        {
            throw new InvalidDataException(
                $"缓存校验所需的 GGUF 文件数量不完整：预期 {variant.ShardCount} 个，已知 {expectedFiles.Count} 个。");
        }

        var resolved = new List<ValidatedCacheFile>(expectedFiles.Count);
        foreach (var expected in expectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalPath = CombineRemotePath(snapshotRoot, expected.Path);
            var file = ValidateSnapshotFile(repositoryRoot, snapshotRoot, logicalPath);
            if (expected.SizeBytes > 0 && file.SizeBytes != expected.SizeBytes)
            {
                throw new InvalidDataException(
                    $"缓存文件大小不完整：{expected.Path}，预期 {expected.SizeBytes:N0} 字节，实际 {file.SizeBytes:N0} 字节。");
            }

            resolved.Add(file);
        }

        var actualTotal = resolved.Sum(file => file.SizeBytes);
        if (variant.TotalSizeBytes > 0 && actualTotal != variant.TotalSizeBytes)
        {
            throw new InvalidDataException(
                $"缓存模型总大小不完整：预期 {variant.TotalSizeBytes:N0} 字节，实际 {actualTotal:N0} 字节。");
        }

        var primaryPath = CombineRemotePath(snapshotRoot, variant.PrimaryFilePath);
        if (!resolved.Any(file => string.Equals(
                file.LogicalPath,
                primaryPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("缓存中未找到该模型的主 GGUF 文件。");
        }

        var repositoryName = variant.RepositoryId.Split('/').Last();
        return new GgufModelCandidate(
            primaryPath,
            Path.GetRelativePath(root, primaryPath),
            $"{repositoryName}-{variant.Quantization}",
            actualTotal,
            resolved.Count,
            variant.DownloadId,
            variant.RepositoryId,
            variant.Quantization);
    }

    public GgufScanResult Scan(string modelsRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        var root = Path.GetFullPath(modelsRoot);
        if (!Directory.Exists(root))
        {
            return new GgufScanResult([], []);
        }

        var models = new List<GgufModelCandidate>();
        var excluded = new List<GgufExcludedFile>();
        foreach (var repositoryRoot in Directory.EnumerateDirectories(root, "models--*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(repositoryRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    excluded.Add(new GgufExcludedFile(repositoryRoot, "缓存仓库目录是重解析点，已为安全起见跳过。"));
                    continue;
                }

                if (!TryReadRepositoryId(repositoryRoot, out var repositoryId))
                {
                    continue;
                }

                var snapshotRoot = GetActiveSnapshotRoot(repositoryRoot);
                var files = EnumerateSnapshotGgufFiles(repositoryRoot, snapshotRoot, excluded, cancellationToken);
                AddCandidates(root, repositoryId, files, models, excluded, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException
                                                   or ArgumentException)
            {
                excluded.Add(new GgufExcludedFile(repositoryRoot, $"缓存仓库不可用：{exception.Message}"));
            }
        }

        return new GgufScanResult(
            models.OrderBy(model => model.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            excluded.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static IReadOnlySet<string> GetPathIdentities(string path)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var fullPath = Path.GetFullPath(path);
            identities.Add(fullPath);
            if (File.Exists(fullPath)
                && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
                && new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                identities.Add(Path.GetFullPath(target.FullName));
            }
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or NotSupportedException)
        {
            // The normalized logical path, when available, remains sufficient for ordinary files.
        }

        return identities;
    }

    private static IReadOnlyList<HuggingFaceGgufFile> ExpandExpectedFiles(HuggingFaceGgufVariant variant)
    {
        if (variant.ExpectedFiles.Count > 1 || variant.ShardCount <= 1)
        {
            return variant.ExpectedFiles;
        }

        var normalized = variant.PrimaryFilePath.Replace('\\', '/');
        var directory = normalized.Contains('/')
            ? normalized[..(normalized.LastIndexOf('/') + 1)]
            : string.Empty;
        var fileName = normalized[directory.Length..];
        var match = ShardFileRegex().Match(fileName);
        if (!match.Success
            || int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) != 1
            || int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) != variant.ShardCount)
        {
            return variant.ExpectedFiles;
        }

        return Enumerable.Range(1, variant.ShardCount)
            .Select(index => new HuggingFaceGgufFile(
                $"{directory}{match.Groups["prefix"].Value}-{index:D5}-of-{variant.ShardCount:D5}.gguf",
                0))
            .ToArray();
    }

    private static List<ValidatedCacheFile> EnumerateSnapshotGgufFiles(
        string repositoryRoot,
        string snapshotRoot,
        ICollection<GgufExcludedFile> excluded,
        CancellationToken cancellationToken)
    {
        var results = new List<ValidatedCacheFile>();
        var pending = new Stack<string>();
        pending.Push(snapshotRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    excluded.Add(new GgufExcludedFile(childDirectory, "快照子目录是重解析点，已为安全起见跳过。"));
                    continue;
                }

                pending.Push(childDirectory);
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GgufAuxiliaryFileClassifier.IsAuxiliaryModel(path))
                {
                    excluded.Add(new GgufExcludedFile(path, GgufAuxiliaryFileClassifier.Describe(path)));
                    continue;
                }

                try
                {
                    results.Add(ValidateSnapshotFile(repositoryRoot, snapshotRoot, path));
                }
                catch (Exception exception) when (exception is IOException
                                                       or UnauthorizedAccessException
                                                       or InvalidDataException
                                                       or ArgumentException)
                {
                    excluded.Add(new GgufExcludedFile(path, exception.Message));
                }
            }
        }

        return results;
    }

    private static void AddCandidates(
        string modelsRoot,
        string repositoryId,
        IReadOnlyList<ValidatedCacheFile> files,
        ICollection<GgufModelCandidate> models,
        ICollection<GgufExcludedFile> excluded,
        CancellationToken cancellationToken)
    {
        var shardGroups = new Dictionary<string, CacheShardGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file.LogicalPath);
            var match = ShardFileRegex().Match(fileName);
            if (!match.Success)
            {
                var quantization = ExtractQuantization(fileName);
                models.Add(CreateCandidate(
                    modelsRoot,
                    repositoryId,
                    file.LogicalPath,
                    Path.GetFileNameWithoutExtension(fileName),
                    file.SizeBytes,
                    1,
                    quantization));
                continue;
            }

            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
            var key = Path.Combine(
                Path.GetDirectoryName(file.LogicalPath) ?? modelsRoot,
                $"{match.Groups["prefix"].Value}-of-{count:D5}");
            if (!shardGroups.TryGetValue(key, out var group))
            {
                group = new CacheShardGroup(match.Groups["prefix"].Value, count);
                shardGroups.Add(key, group);
            }

            group.Files[index] = file;
        }

        foreach (var group in shardGroups.Values)
        {
            var missing = Enumerable.Range(1, group.ExpectedCount)
                .Where(index => !group.Files.ContainsKey(index))
                .ToArray();
            if (missing.Length > 0 || !group.Files.TryGetValue(1, out var firstShard))
            {
                var missingText = string.Join(", ", missing.Select(index => index.ToString("D5", CultureInfo.InvariantCulture)));
                foreach (var file in group.Files.Values)
                {
                    excluded.Add(new GgufExcludedFile(file.LogicalPath, $"缓存分片不完整，缺少：{missingText}。"));
                }

                continue;
            }

            var quantization = ExtractQuantization(Path.GetFileName(firstShard.LogicalPath));
            models.Add(CreateCandidate(
                modelsRoot,
                repositoryId,
                firstShard.LogicalPath,
                group.Prefix,
                group.Files.Values.Sum(file => file.SizeBytes),
                group.ExpectedCount,
                quantization));
        }
    }

    private static GgufModelCandidate CreateCandidate(
        string modelsRoot,
        string repositoryId,
        string primaryPath,
        string fallbackDisplayName,
        long totalSizeBytes,
        int shardCount,
        string? quantization)
    {
        var repositoryName = repositoryId.Split('/').Last();
        var displayName = string.IsNullOrWhiteSpace(quantization)
            ? fallbackDisplayName
            : $"{repositoryName}-{quantization}";
        return new GgufModelCandidate(
            primaryPath,
            Path.GetRelativePath(modelsRoot, primaryPath),
            displayName,
            totalSizeBytes,
            shardCount,
            string.IsNullOrWhiteSpace(quantization) ? null : $"{repositoryId}:{quantization}",
            repositoryId,
            quantization);
    }

    private static ValidatedCacheFile ValidateSnapshotFile(
        string repositoryRoot,
        string snapshotRoot,
        string logicalPath)
    {
        var fullLogicalPath = Path.GetFullPath(logicalPath);
        EnsureContained(snapshotRoot, fullLogicalPath, "缓存快照文件越出了当前快照目录。");
        EnsureNoReparseDirectories(snapshotRoot, Path.GetDirectoryName(fullLogicalPath)!);
        if (!File.Exists(fullLogicalPath))
        {
            throw new InvalidDataException("缓存快照文件不存在或符号链接已经失效。");
        }

        var attributes = File.GetAttributes(fullLogicalPath);
        string physicalPath;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = new FileInfo(fullLogicalPath).ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidDataException("无法解析缓存快照符号链接。");
            physicalPath = Path.GetFullPath(target.FullName);
            var blobsRoot = Path.Combine(repositoryRoot, "blobs");
            EnsureOrdinaryDirectory(blobsRoot, "缓存仓库的 blobs 目录不存在或不是普通目录。");
            EnsureContained(
                blobsRoot,
                physicalPath,
                "缓存快照符号链接没有指向同一仓库的 blobs 目录。");
        }
        else
        {
            physicalPath = fullLogicalPath;
        }

        var file = new FileInfo(physicalPath);
        if (!file.Exists || file.Length < GgufMagic.Length)
        {
            throw new InvalidDataException("缓存 GGUF 文件为空或尚未下载完整。");
        }

        Span<byte> header = stackalloc byte[GgufMagic.Length];
        using (var stream = new FileStream(
                   physicalPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete,
                   bufferSize: 4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Read(header) != header.Length || !header.SequenceEqual(GgufMagic))
            {
                throw new InvalidDataException("缓存文件不是有效的 GGUF 模型。");
            }
        }

        return new ValidatedCacheFile(fullLogicalPath, physicalPath, file.Length);
    }

    private static string GetRepositoryRoot(string modelsRoot, string repositoryId)
    {
        var separator = repositoryId.IndexOf('/');
        if (separator <= 0
            || separator != repositoryId.LastIndexOf('/')
            || separator == repositoryId.Length - 1)
        {
            throw new ArgumentException("Hugging Face 仓库标识无效。", nameof(repositoryId));
        }

        var owner = repositoryId[..separator];
        var name = repositoryId[(separator + 1)..];
        if (!IsSafeRepositoryComponent(owner) || !IsSafeRepositoryComponent(name))
        {
            throw new ArgumentException("Hugging Face 仓库标识包含不安全的路径字符。", nameof(repositoryId));
        }

        var repositoryRoot = Path.GetFullPath(Path.Combine(modelsRoot, $"models--{owner}--{name}"));
        EnsureContained(modelsRoot, repositoryRoot, "缓存仓库目录越出了 models 目录。");
        if (!Directory.Exists(repositoryRoot))
        {
            throw new InvalidDataException("未找到 llama.cpp 创建的模型缓存仓库。");
        }

        EnsureOrdinaryDirectory(repositoryRoot, "模型缓存仓库不是普通目录。");

        return repositoryRoot;
    }

    private static string GetActiveSnapshotRoot(string repositoryRoot)
    {
        var refsRoot = Path.Combine(repositoryRoot, "refs");
        EnsureOrdinaryDirectory(refsRoot, "缓存仓库缺少有效的 refs 目录。");
        var mainRef = Path.Combine(refsRoot, "main");
        if (!File.Exists(mainRef)
            || (File.GetAttributes(mainRef) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("缓存仓库缺少有效的 refs/main。");
        }

        var revision = File.ReadAllText(mainRef).Trim();
        if (!RevisionRegex().IsMatch(revision))
        {
            throw new InvalidDataException("缓存仓库的 refs/main 内容无效。");
        }

        var snapshotsRoot = Path.Combine(repositoryRoot, "snapshots");
        EnsureOrdinaryDirectory(snapshotsRoot, "缓存仓库缺少有效的 snapshots 目录。");
        var snapshotRoot = Path.GetFullPath(Path.Combine(snapshotsRoot, revision));
        EnsureContained(snapshotsRoot, snapshotRoot, "缓存快照目录越出了 snapshots 目录。");
        if (!Directory.Exists(snapshotRoot)
            || (File.GetAttributes(snapshotRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("refs/main 指向的缓存快照不存在。");
        }

        return snapshotRoot;
    }

    private static string CombineRemotePath(string snapshotRoot, string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var segments = remotePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || Path.IsPathRooted(remotePath)
            || segments.Any(segment => segment is "." or ".." || !IsSafePathSegment(segment)))
        {
            throw new InvalidDataException("远程 GGUF 文件路径无效。");
        }

        var combined = Path.GetFullPath(segments.Aggregate(snapshotRoot, Path.Combine));
        EnsureContained(snapshotRoot, combined, "远程 GGUF 文件路径越出了缓存快照目录。");
        return combined;
    }

    private static bool TryReadRepositoryId(string repositoryRoot, out string repositoryId)
    {
        repositoryId = string.Empty;
        var name = Path.GetFileName(repositoryRoot);
        if (!name.StartsWith("models--", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = name["models--".Length..];
        var separator = encoded.IndexOf("--", StringComparison.Ordinal);
        if (separator <= 0 || separator == encoded.Length - 2)
        {
            return false;
        }

        repositoryId = $"{encoded[..separator]}/{encoded[(separator + 2)..]}";
        return true;
    }

    private static bool IsSafeRepositoryComponent(string value) =>
        value.Length is > 0 and <= 128
        && value is not "." and not ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains(Path.DirectorySeparatorChar)
        && !value.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsSafePathSegment(string value) =>
        value.Length is > 0 and <= 255
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains(':');

    private static void EnsureContained(string root, string path, string message)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureOrdinaryDirectory(string path, string message)
    {
        if (!Directory.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureNoReparseDirectories(string root, string directory)
    {
        var current = Path.GetFullPath(directory);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        while (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            EnsureContained(normalizedRoot, current, "缓存文件的父目录越出了当前快照。");
            EnsureOrdinaryDirectory(current, "缓存文件位于重解析点目录中，已为安全起见拒绝使用。");
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("无法验证缓存文件的父目录。");
        }

        EnsureOrdinaryDirectory(normalizedRoot, "缓存快照目录不是普通目录。");
    }

    private static string? ExtractQuantization(string fileName)
    {
        var match = QuantizationRegex().Match(fileName);
        return match.Success ? match.Groups["quant"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionRegex();

    [GeneratedRegex("^(?<prefix>.+)-(?<index>\\d{5})-of-(?<count>\\d{5})\\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShardFileRegex();

    [GeneratedRegex("(?:^|[-.])(?<quant>(?:(?:UD|DQ)-)?(?:IQ|TQ|Q)\\d[A-Z0-9_]*|BF16|F16|F32|MXFP4|NVFP4)(?=(?:-\\d{5}-of-\\d{5})?\\.gguf$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantizationRegex();

    private sealed record ValidatedCacheFile(string LogicalPath, string PhysicalPath, long SizeBytes);

    private sealed record CacheShardGroup(string Prefix, int ExpectedCount)
    {
        public Dictionary<int, ValidatedCacheFile> Files { get; } = new();
    }
}
