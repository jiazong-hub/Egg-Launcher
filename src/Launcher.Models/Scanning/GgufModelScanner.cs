using System.Globalization;
using System.Text.RegularExpressions;

namespace Launcher.Models.Scanning;

public sealed partial class GgufModelScanner
{
    public GgufScanResult Scan(string modelsRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        var root = Path.GetFullPath(modelsRoot);
        if (!Directory.Exists(root))
        {
            return new GgufScanResult(
                Array.Empty<GgufModelCandidate>(),
                new[] { new GgufExcludedFile(root, "模型目录不存在。") });
        }

        var models = new List<GgufModelCandidate>();
        var excluded = new List<GgufExcludedFile>();
        var shardGroups = new Dictionary<string, ShardGroup>(StringComparer.OrdinalIgnoreCase);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*.gguf", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInsideHuggingFaceCache(root, path))
            {
                // Cache snapshots need dedicated link-target and revision validation below.
                continue;
            }

            var fileName = Path.GetFileName(path);

            if (GgufAuxiliaryFileClassifier.IsAuxiliaryModel(path))
            {
                excluded.Add(new GgufExcludedFile(path, GgufAuxiliaryFileClassifier.Describe(path)));
                continue;
            }

            var shardMatch = ShardFileRegex().Match(fileName);
            if (!shardMatch.Success)
            {
                var file = new FileInfo(path);
                models.Add(new GgufModelCandidate(
                    file.FullName,
                    Path.GetRelativePath(root, file.FullName),
                    Path.GetFileNameWithoutExtension(file.Name),
                    file.Length,
                    ShardCount: 1));
                continue;
            }

            var shardIndex = int.Parse(shardMatch.Groups["index"].Value, CultureInfo.InvariantCulture);
            var shardCount = int.Parse(shardMatch.Groups["count"].Value, CultureInfo.InvariantCulture);
            var groupKey = Path.Combine(
                Path.GetDirectoryName(path) ?? root,
                $"{shardMatch.Groups["prefix"].Value}-of-{shardCount:D5}");

            if (!shardGroups.TryGetValue(groupKey, out var group))
            {
                group = new ShardGroup(shardMatch.Groups["prefix"].Value, shardCount);
                shardGroups.Add(groupKey, group);
            }

            group.Files[shardIndex] = new FileInfo(path);
        }

        foreach (var group in shardGroups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var missing = Enumerable.Range(1, group.ExpectedCount)
                .Where(index => !group.Files.ContainsKey(index))
                .ToArray();

            if (missing.Length > 0 || !group.Files.TryGetValue(1, out var firstShard))
            {
                var missingText = string.Join(", ", missing.Select(index => index.ToString("D5", CultureInfo.InvariantCulture)));
                foreach (var file in group.Files.Values)
                {
                    excluded.Add(new GgufExcludedFile(file.FullName, $"分片不完整，缺少：{missingText}。"));
                }

                continue;
            }

            models.Add(new GgufModelCandidate(
                firstShard.FullName,
                Path.GetRelativePath(root, firstShard.FullName),
                group.Prefix,
                group.Files.Values.Sum(file => file.Length),
                group.ExpectedCount));
        }

        var cacheScan = new HuggingFaceCacheResolver().Scan(root, cancellationToken);
        models.AddRange(cacheScan.Models);
        excluded.AddRange(cacheScan.ExcludedFiles);

        return new GgufScanResult(
            models
                .GroupBy(model => Path.GetFullPath(model.PrimaryPath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(model => model.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            excluded.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsInsideHuggingFaceCache(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var firstSegment = separator < 0 ? relative : relative[..separator];
        return firstSegment.StartsWith("models--", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^(?<prefix>.+)-(?<index>\\d{5})-of-(?<count>\\d{5})\\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShardFileRegex();

    private sealed record ShardGroup(string Prefix, int ExpectedCount)
    {
        public Dictionary<int, FileInfo> Files { get; } = new();
    }
}
