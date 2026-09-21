namespace Launcher.Models.Remote;

public static class HuggingFaceWebUriBuilder
{
    private static readonly Uri HubEndpoint = new("https://huggingface.co/", UriKind.Absolute);

    public static Uri BuildRepositoryUri(string repositoryId)
    {
        var repositoryPath = BuildRepositoryPath(repositoryId);
        return new Uri(HubEndpoint, repositoryPath);
    }

    public static Uri BuildVariantUri(
        HuggingFaceModelSearchResult model,
        HuggingFaceGgufVariant variant)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(variant);
        if (!string.Equals(model.RepositoryId, variant.RepositoryId, StringComparison.Ordinal))
        {
            throw new ArgumentException("所选量化版本不属于当前模型仓库。", nameof(variant));
        }

        var repositoryPath = BuildRepositoryPath(model.RepositoryId);
        var revision = string.IsNullOrWhiteSpace(model.Revision) ? "main" : model.Revision.Trim();
        var revisionSegment = EscapeSafeSegment(revision, nameof(model));
        var filePath = BuildSafeFilePath(variant.PrimaryFilePath);
        return new Uri(HubEndpoint, $"{repositoryPath}/blob/{revisionSegment}/{filePath}");
    }

    private static string BuildRepositoryPath(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var parts = repositoryId.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Hugging Face 仓库名称必须为“作者/仓库”。", nameof(repositoryId));
        }

        return $"{EscapeSafeSegment(parts[0], nameof(repositoryId))}/" +
               EscapeSafeSegment(parts[1], nameof(repositoryId));
    }

    private static string BuildSafeFilePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\\'))
        {
            throw new ArgumentException("Hugging Face 文件路径不能包含反斜杠。", nameof(path));
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Hugging Face 文件路径不能为空。", nameof(path));
        }

        return string.Join('/', parts.Select(part => EscapeSafeSegment(part, nameof(path))));
    }

    private static string EscapeSafeSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            throw new ArgumentException("Hugging Face 地址包含无效路径段。", parameterName);
        }

        return Uri.EscapeDataString(value);
    }
}
