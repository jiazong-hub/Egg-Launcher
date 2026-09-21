using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Models.Scanning;

namespace Launcher.Models.Remote;

public sealed partial class HuggingFaceModelCatalogClient
{
    private static readonly Uri DefaultEndpoint = new("https://huggingface.co/", UriKind.Absolute);
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public HuggingFaceModelCatalogClient(HttpClient httpClient, Uri? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    public async Task<IReadOnlyList<HuggingFaceModelSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalized = query.Trim();
        if (normalized.Length > 200)
        {
            throw new ArgumentException("模型搜索内容不能超过 200 个字符。", nameof(query));
        }

        var relative =
            $"api/models?search={Uri.EscapeDataString(normalized)}&filter=gguf&sort=downloads&direction=-1&limit=20&full=true";
        using var response = await _httpClient.GetAsync(new Uri(_endpoint, relative), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Hugging Face 返回了无法识别的搜索结果。");
        }

        return document.RootElement.EnumerateArray()
            .Select(ParseSearchResult)
            .Where(result => result is not null)
            .Select(result => result!)
            .ToArray();
    }

    public async Task<HuggingFaceModelDetails> GetDetailsAsync(
        HuggingFaceModelSearchResult model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var parts = model.RepositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Hugging Face 仓库名称必须为“作者/仓库”。", nameof(model));
        }

        var revision = string.IsNullOrWhiteSpace(model.Revision) ? "main" : model.Revision;
        var relative = $"api/models/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}" +
                       $"/tree/{Uri.EscapeDataString(revision)}?recursive=true&expand=true";
        using var response = await _httpClient.GetAsync(new Uri(_endpoint, relative), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Hugging Face 返回了无法识别的文件列表。");
        }

        var files = document.RootElement.EnumerateArray()
            .Select(ParseFile)
            .Where(file => file is not null
                           && file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Select(file => file!)
            .ToArray();

        var revisionId = string.IsNullOrWhiteSpace(model.Revision) ? "main" : model.Revision;
        var mtpFiles = files
            .Where(file => IsRemoteMtpCandidate(file.Path))
            .Select(file => new HuggingFaceMtpFile(
                model.RepositoryId,
                revisionId,
                file.Path,
                file.SizeBytes))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visionFiles = files
            .Where(file => IsRemoteVisionCandidate(file.Path))
            .Select(file => new HuggingFaceVisionFile(
                model.RepositoryId,
                revisionId,
                file.Path,
                file.SizeBytes))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HuggingFaceModelDetails(
            model,
            BuildVariants(model.RepositoryId, files.Where(file => IsMainGguf(file.Path)).ToArray()),
            mtpFiles,
            visionFiles);
    }

    private static HuggingFaceModelSearchResult? ParseSearchResult(JsonElement element)
    {
        var id = ReadString(element, "id") ?? ReadString(element, "modelId");
        if (string.IsNullOrWhiteSpace(id) || id.Count(character => character == '/') != 1)
        {
            return null;
        }

        var tags = ReadStrings(element, "tags");
        if (!tags.Any(tag => string.Equals(tag, "gguf", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var license = ReadNestedString(element, "cardData", "license")
            ?? tags.FirstOrDefault(tag => tag.StartsWith("license:", StringComparison.OrdinalIgnoreCase))?["license:".Length..];
        var gated = false;
        if (element.TryGetProperty("gated", out var gatedElement))
        {
            gated = gatedElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => !string.Equals(gatedElement.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        DateTimeOffset? lastModified = null;
        if (DateTimeOffset.TryParse(
                ReadString(element, "lastModified"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            lastModified = parsed;
        }

        return new HuggingFaceModelSearchResult(
            id,
            ReadString(element, "author") ?? id[..id.IndexOf('/')],
            ReadString(element, "pipeline_tag"),
            license,
            gated,
            ReadInt64(element, "downloads"),
            ReadInt64(element, "likes"),
            lastModified,
            ReadString(element, "sha"));
    }

    private static RemoteFile? ParseFile(JsonElement element)
    {
        if (!string.Equals(ReadString(element, "type"), "file", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = ReadString(element, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var size = ReadInt64(element, "size");
        if (size <= 0 && element.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
        {
            size = ReadInt64(lfs, "size");
        }

        return new RemoteFile(path, Math.Max(0, size));
    }

    private static IReadOnlyList<HuggingFaceGgufVariant> BuildVariants(
        string repositoryId,
        IReadOnlyList<RemoteFile> files)
    {
        var singleFiles = new List<HuggingFaceGgufVariant>();
        var shardGroups = new Dictionary<string, List<(int Index, int Count, RemoteFile File)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var match = ShardRegex().Match(file.Path);
            if (!match.Success)
            {
                var quantization = ExtractQuantization(file.Path);
                if (quantization is not null)
                {
                    singleFiles.Add(new HuggingFaceGgufVariant(
                        repositoryId,
                        quantization,
                        file.Path,
                        file.SizeBytes,
                        1,
                        [new HuggingFaceGgufFile(file.Path, file.SizeBytes)]));
                }

                continue;
            }

            var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            var key = match.Groups["prefix"].Value;
            if (!shardGroups.TryGetValue(key, out var group))
            {
                group = new List<(int, int, RemoteFile)>();
                shardGroups.Add(key, group);
            }

            group.Add((index, count, file));
        }

        foreach (var group in shardGroups.Values)
        {
            var expected = group[0].Count;
            if (group.Any(item => item.Count != expected)
                || group.Select(item => item.Index).Distinct().Count() != expected
                || group.All(item => item.Index != 1))
            {
                continue;
            }

            var first = group.Single(item => item.Index == 1).File;
            var quantization = ExtractQuantization(first.Path);
            if (quantization is null)
            {
                continue;
            }

            singleFiles.Add(new HuggingFaceGgufVariant(
                repositoryId,
                quantization,
                first.Path,
                group.Sum(item => item.File.SizeBytes),
                expected,
                group
                    .OrderBy(item => item.Index)
                    .Select(item => new HuggingFaceGgufFile(item.File.Path, item.File.SizeBytes))
                    .ToArray()));
        }

        return singleFiles
            .GroupBy(variant => variant.Quantization, StringComparer.OrdinalIgnoreCase)
            // The Router download API accepts repo:quant, not an exact file path. If one repo
            // publishes multiple main files with the same quant, llama's selection is ambiguous;
            // omit that entry instead of showing a size for a file we cannot select exactly.
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(variant => variant.TotalSizeBytes == 0 ? long.MaxValue : variant.TotalSizeBytes)
            .ThenBy(variant => variant.Quantization, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ExtractQuantization(string path)
    {
        var fileName = Path.GetFileName(path);
        var match = QuantizationRegex().Match(fileName);
        return match.Success ? match.Groups["quant"].Value.ToUpperInvariant() : null;
    }

    private static bool IsMainGguf(string path)
    {
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !GgufAuxiliaryFileClassifier.IsAuxiliaryModel(path);
    }

    private static bool IsRemoteMtpCandidate(string path)
    {
        if (GgufAuxiliaryFileClassifier.IsMtpCompanion(path))
        {
            return true;
        }

        var tokens = path.Replace('\\', '/').Split(
            ['/', '-', '_', '.'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => string.Equals(token, "mtp", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRemoteVisionCandidate(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains(".mmproj.", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, string parentName, string propertyName) =>
        element.TryGetProperty(parentName, out var parent) && parent.ValueKind == JsonValueKind.Object
            ? ReadString(parent, propertyName)
            : null;

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt64(out var result) ? result : 0;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : Array.Empty<string>();

    [GeneratedRegex("^(?<prefix>.+)-(?<index>\\d{5})-of-(?<count>\\d{5})\\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShardRegex();

    [GeneratedRegex("(?:^|[-.])(?<quant>(?:(?:UD|DQ)-)?(?:IQ|TQ|Q)\\d[A-Z0-9_]*|BF16|F16|F32|MXFP4|NVFP4)(?=(?:-\\d{5}-of-\\d{5})?\\.gguf$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantizationRegex();

    private sealed record RemoteFile(string Path, long SizeBytes);
}

public sealed record HuggingFaceModelSearchResult(
    string RepositoryId,
    string Author,
    string? PipelineTag,
    string? License,
    bool IsGated,
    long Downloads,
    long Likes,
    DateTimeOffset? LastModified,
    string? Revision);

public sealed record HuggingFaceModelDetails(
    HuggingFaceModelSearchResult Model,
    IReadOnlyList<HuggingFaceGgufVariant> Variants,
    IReadOnlyList<HuggingFaceMtpFile>? MtpFiles = null,
    IReadOnlyList<HuggingFaceVisionFile>? VisionFiles = null)
{
    public IReadOnlyList<HuggingFaceMtpFile> ExternalMtpFiles =>
        MtpFiles ?? Array.Empty<HuggingFaceMtpFile>();

    public IReadOnlyList<HuggingFaceVisionFile> ExternalVisionFiles =>
        VisionFiles ?? Array.Empty<HuggingFaceVisionFile>();
}

public sealed record HuggingFaceGgufVariant(
    string RepositoryId,
    string Quantization,
    string PrimaryFilePath,
    long TotalSizeBytes,
    int ShardCount,
    IReadOnlyList<HuggingFaceGgufFile>? Files = null)
{
    public string DownloadId => $"{RepositoryId}:{Quantization}";

    public IReadOnlyList<HuggingFaceGgufFile> ExpectedFiles => Files is { Count: > 0 }
        ? Files
        : [new HuggingFaceGgufFile(PrimaryFilePath, ShardCount == 1 ? TotalSizeBytes : 0)];
}

public sealed record HuggingFaceGgufFile(string Path, long SizeBytes);

public sealed record HuggingFaceMtpFile(
    string RepositoryId,
    string Revision,
    string Path,
    long SizeBytes)
{
    public string DownloadId => $"{RepositoryId}@{Revision}:{Path}";
}

public sealed record HuggingFaceVisionFile(
    string RepositoryId,
    string Revision,
    string Path,
    long SizeBytes)
{
    public string DownloadId => $"{RepositoryId}@{Revision}:{Path}";
}
