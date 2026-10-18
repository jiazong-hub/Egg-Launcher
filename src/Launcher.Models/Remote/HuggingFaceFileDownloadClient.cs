using System.Net;
using System.Net.Http.Headers;

namespace Launcher.Models.Remote;

public sealed class HuggingFaceFileDownloadClient(
    HttpClient httpClient, Uri? endpoint = null,
    TimeSpan? responseTimeout = null, TimeSpan? idleTimeout = null)
{
    private readonly Uri _endpoint = endpoint ?? new Uri("https://huggingface.co/", UriKind.Absolute);
    private readonly TimeSpan _responseTimeout = ValidateTimeout(responseTimeout ?? TimeSpan.FromSeconds(90));
    private readonly TimeSpan _idleTimeout = ValidateTimeout(idleTimeout ?? TimeSpan.FromMinutes(2));

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
        if (sizeBytes > 0 && resumeOffset > sizeBytes)
        {
            File.Delete(partial);
            resumeOffset = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildResolveUri(repositoryId, revision, path));
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var response = await SendWithTimeoutAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
            && sizeBytes > 0
            && resumeOffset == sizeBytes)
        {
            File.Move(partial, destination, overwrite: true);
            progress?.Report(new HuggingFaceFileDownloadProgress(resumeOffset, resumeOffset));
            return destination;
        }

        response.EnsureSuccessStatusCode();
        long? rangeLength = null;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                || range.From != resumeOffset || range.To is null || range.To < range.From
                || range.Length is { } total && (total <= range.To || sizeBytes > 0 && total != sizeBytes))
                throw new InvalidDataException("下载续传区间与请求不符，已停止；请重新下载 / Invalid download resume range; restart the download.");
            rangeLength = range.To.Value - resumeOffset + 1;
            if (response.Content.Headers.ContentLength is { } length && length != rangeLength)
                throw new InvalidDataException("下载续传响应长度与区间不符 / Download response length does not match its range.");
        }
        var append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            resumeOffset = 0;
        }

        var responseLength = response.Content.Headers.ContentLength ?? 0;
        var expectedTotal = sizeBytes > 0
            ? sizeBytes
            : response.Content.Headers.ContentRange?.Length
                ?? (responseLength > 0 ? resumeOffset + responseLength : 0);
        var downloaded = resumeOffset;
        using var idleSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                int read;
                idleSource.CancelAfter(_idleTimeout);
                try
                {
                    read = await input.ReadAsync(buffer, idleSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && idleSource.IsCancellationRequested)
                {
                    throw new TimeoutException("下载长时间未收到数据，请重试 / Download timed out waiting for data; retry.");
                }
                finally { idleSource.CancelAfter(Timeout.InfiniteTimeSpan); }
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
        if (rangeLength is { } expectedRangeLength && downloaded - resumeOffset != expectedRangeLength)
            throw new InvalidDataException("下载续传数据长度与区间不符 / Received download data does not match its range.");
        if (expectedTotal > 0 && downloaded != expectedTotal)
        {
            throw new InvalidDataException(
                $"{displayType} 文件下载大小不完整：期望 {expectedTotal} 字节，实际 {downloaded} 字节。");
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_responseTimeout);
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("等待下载服务器响应超时，请重试 / Download server response timed out; retry.");
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero
        && timeout.TotalMilliseconds <= uint.MaxValue - 1
            ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));

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
