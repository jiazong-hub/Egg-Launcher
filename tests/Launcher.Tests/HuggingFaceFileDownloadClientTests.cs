using System.Net;
using Launcher.Models.Remote;

namespace Launcher.Tests;

public sealed class HuggingFaceFileDownloadClientTests
{
    [Fact]
    public async Task DownloadMtpFileAsync_DownloadsExactFileWithoutRegisteringModel()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(
                "https://huggingface.co/org/repo/resolve/revision/weights/mtp-model.gguf?download=true",
                request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
        }));
        var root = Path.Combine(Path.GetTempPath(), "launcher-mtp-download-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var client = new HuggingFaceFileDownloadClient(http);
            var file = new HuggingFaceMtpFile(
                "org/repo", "revision", "weights/mtp-model.gguf", bytes.Length);

            var result = await client.DownloadMtpFileAsync(file, root);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
            Assert.Contains(Path.Combine("egg-launcher-mtp", "org", "repo", "revision"), result, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(result + ".partial"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadMtpFileAsync_RejectsTraversalPath()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException()));
        var client = new HuggingFaceFileDownloadClient(http);
        var file = new HuggingFaceMtpFile("org/repo", "main", "../mtp.gguf", 1);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadMtpFileAsync(file, Path.GetTempPath()));
    }

    [Fact]
    public async Task DownloadVisionFileAsync_UsesSeparateManagedDirectory()
    {
        var bytes = new byte[] { 4, 3, 2, 1 };
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        }));
        var root = Path.Combine(Path.GetTempPath(), "launcher-vision-download-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new HuggingFaceFileDownloadClient(http).DownloadVisionFileAsync(
                new HuggingFaceVisionFile("org/repo", "main", "mmproj-model.gguf", bytes.Length),
                root);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
            Assert.Contains(Path.Combine("egg-launcher-vision", "org", "repo", "main"), result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
