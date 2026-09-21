using System.Net;
using System.Net.Http.Headers;

namespace Launcher.Models.Remote;

public sealed class HuggingFaceFileDownloadClient(HttpClient httpClient, Uri? endpoint = null)
{
    private readonly Uri _endpoint = endpoint ?? new Uri("https://huggingface.co/", UriKind.Absolute);

    public async Task<string> DownloadMtpFileAsync(
        HuggingFaceMtpFile file,
        string modelsRoot,
        IProgress<HuggingFaceFileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return await DownloadAuxiliaryFileAsync(
            file.RepositoryId,
            file.Revision,
            file.Path,
            file.SizeBytes,
            "egg-launcher-mtp",
            "MTP",
            modelsRoot,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<string> DownloadVisionFileAsync(
        HuggingFaceVisionFile file,
        string modelsRoot,
        IProgress<HuggingFaceFileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return DownloadAuxiliaryFileAsync(
            file.RepositoryId,
            file.Revision,
            file.Path,
            file.SizeBytes,
            "egg-launcher-vision",
            "Vision",
            modelsRoot,
            progress,
            cancellationToken);
    }

    private async Task<string> DownloadAuxiliaryFileAsync(
        string repositoryId,
        string revision,
        string path,
        long sizeBytes,
        string managedDirectory,
        string displayType,
        string modelsRoot,
        IProgress<HuggingFaceFileDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ValidateRepositoryId(repositoryId);
        var relativeFilePath = ValidateRelativeFilePath(path);
        var destinationRoot = Path.Combine(
            Path.GetFullPath(modelsRoot),
            managedDirectory,
            repositoryId.Replace('/', Path.DirectorySeparatorChar),
            SanitizePathSegment(revision));
        var destination = Path.GetFullPath(Path.Combine(destinationRoot, relativeFilePath));
        var destinationPrefix = destinationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{displayType} 下载目标路径越出了受管理目录。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            var existingLength = new FileInfo(destination).Length;
            if (sizeBytes <= 0 || existingLength == sizeBytes)
            {
                progress?.Report(new HuggingFaceFileDownloadProgress(existingLength, existingLength));
                return destination;
            }
        }

        var partial = destination + ".partial";
        var resumeOffset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildResolveUri(repositoryId, revision, path));
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
            && sizeBytes > 0
            && resumeOffset == sizeBytes)
        {
            File.Move(partial, destination, overwrite: true);
            progress?.Report(new HuggingFaceFileDownloadProgress(resumeOffset, resumeOffset));
            return destination;
        }

        response.EnsureSuccessStatusCode();
        var append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            resumeOffset = 0;
        }

        var responseLength = response.Content.Headers.ContentLength ?? 0;
        var expectedTotal = sizeBytes > 0
            ? sizeBytes
            : responseLength > 0 ? resumeOffset + responseLength : 0;
        var downloaded = resumeOffset;
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partial,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            progress?.Report(new HuggingFaceFileDownloadProgress(downloaded, expectedTotal));
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                progress?.Report(new HuggingFaceFileDownloadProgress(downloaded, expectedTotal));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (sizeBytes > 0 && downloaded != sizeBytes)
        {
            throw new InvalidDataException(
                $"{displayType} 文件下载大小不完整：期望 {sizeBytes} 字节，实际 {downloaded} 字节。");
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private Uri BuildResolveUri(string repositoryId, string revisionValue, string filePath)
    {
        var repository = string.Join('/', repositoryId.Split('/').Select(Uri.EscapeDataString));
        var revision = Uri.EscapeDataString(revisionValue);
        var path = string.Join('/', filePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return new Uri(_endpoint, $"{repository}/resolve/{revision}/{path}?download=true");
    }

    private static void ValidateRepositoryId(string repositoryId)
    {
        var parts = repositoryId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(part => part is "." or ".." || part.IndexOfAny(['\\', '\0', '\r', '\n']) >= 0))
        {
            throw new InvalidDataException("Hugging Face 仓库 ID 无效。");
        }
    }

    private static string ValidateRelativeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || !path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("MTP 文件路径无效。");
        }

        var parts = path.Replace('\\', '/').Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            throw new InvalidDataException("MTP 文件路径无效。");
        }

        return Path.Combine(parts);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "main" : result;
    }
}

public sealed record HuggingFaceFileDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
}
