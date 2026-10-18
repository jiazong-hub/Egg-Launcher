using System.Net;
using System.Net.Http.Headers;
using Launcher.Models.Remote;

namespace Launcher.Tests;

public sealed class AuxiliaryDownloadBoundaryTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("wrong-start", false)]
    [InlineData("wrong-total", false)]
    [InlineData("missing-range", false)]
    [InlineData("wrong-length", false)]
    [InlineData("truncated", false)]
    [InlineData("full-response", true)]
    public async Task Resume_ValidatesRangeBeforePublishing(string scenario, bool success)
    {
        var root = CreateRoot();
        var target = Destination(root);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target + ".partial", [1, 2, 3, 4]);
            using var http = new HttpClient(new Handler((request, _) =>
            {
                Assert.Equal(4, request.Headers.Range!.Ranges.Single().From);
                var bytes = scenario == "full-response" ? new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }
                    : scenario == "truncated" ? new byte[] { 5, 6 } : [5, 6, 7, 8];
                var response = new HttpResponseMessage(scenario == "full-response" ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(bytes) };
                if (scenario is not ("missing-range" or "full-response"))
                    response.Content.Headers.ContentRange = scenario switch
                    {
                        "wrong-start" => new(0, 3, 8),
                        "wrong-total" => new(4, 7, 12),
                        _ => new ContentRangeHeaderValue(4, 7, 8),
                    };
                if (scenario == "wrong-length") response.Content.Headers.ContentLength = 3;
                if (scenario == "truncated") response.Content.Headers.ContentLength = null;
                return Task.FromResult(response);
            }));
            var client = new HuggingFaceFileDownloadClient(http);
            if (success)
            {
                Assert.Equal(target, await client.DownloadMtpFileAsync(FileDescriptor(), root));
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await File.ReadAllBytesAsync(target));
                Assert.False(File.Exists(target + ".partial"));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadMtpFileAsync(FileDescriptor(), root));
                Assert.False(File.Exists(target));
                if (scenario != "truncated")
                    Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(target + ".partial"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OversizedPartial_RestartsFromBeginning()
    {
        var root = CreateRoot();
        try
        {
            var target = Destination(root);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target + ".partial", new byte[9]);
            using var http = new HttpClient(new Handler((request, _) =>
            {
                Assert.Null(request.Headers.Range);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8]) });
            }));
            await new HuggingFaceFileDownloadClient(http).DownloadMtpFileAsync(FileDescriptor(), root);
            Assert.Equal(8, new FileInfo(target).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownFileSize_UsesResumeResponseTotal(bool complete)
    {
        var root = CreateRoot();
        try
        {
            var target = Destination(root);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target + ".partial", new byte[4]);
            using var http = new HttpClient(new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(new byte[complete ? 4 : 2]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, complete ? 7 : 5, 8);
                return Task.FromResult(response);
            }));
            var client = new HuggingFaceFileDownloadClient(http);
            var file = FileDescriptor() with { SizeBytes = 0 };
            if (complete)
            {
                await client.DownloadMtpFileAsync(file, root);
                Assert.Equal(8, new FileInfo(target).Length);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadMtpFileAsync(file, root));
                Assert.False(File.Exists(target));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NoResponseHeaders_TimesOut()
    {
        var root = CreateRoot();
        try
        {
            using var http = new HttpClient(new Handler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            }))
            { Timeout = Timeout.InfiniteTimeSpan };
            var client = new HuggingFaceFileDownloadClient(http, responseTimeout: TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadMtpFileAsync(FileDescriptor(), root));
            Assert.False(File.Exists(Destination(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleBody_TimesOut_AndUserCancellationRemainsCancellation(bool userCancel)
    {
        var root = CreateRoot();
        try
        {
            using var cancel = new CancellationTokenSource();
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new DelayedStream(Timeout.InfiniteTimeSpan)) })))
            { Timeout = Timeout.InfiniteTimeSpan };
            var client = new HuggingFaceFileDownloadClient(http, idleTimeout: userCancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(150));
            if (userCancel)
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadMtpFileAsync(FileDescriptor(), root, cancellationToken: cancel.Token));
            }
            else await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadMtpFileAsync(FileDescriptor(), root));
            Assert.False(File.Exists(Destination(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SlowButProgressingBody_DoesNotHaveAFixedTotalDeadline()
    {
        var root = CreateRoot();
        try
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new DelayedStream(TimeSpan.FromMilliseconds(100))) })));
            var client = new HuggingFaceFileDownloadClient(http, idleTimeout: TimeSpan.FromMilliseconds(600));
            await client.DownloadMtpFileAsync(FileDescriptor(), root);
            Assert.Equal(8, new FileInfo(Destination(root)).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    private static HuggingFaceMtpFile FileDescriptor() => new("org/repo", "revision", "draft.gguf", 8);
    private static string Destination(string root) => Path.Combine(root, "egg-launcher-mtp", "org", "repo", "revision", "draft.gguf");
    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "EggLauncher.DownloadFixTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class DelayedStream(TimeSpan delay) : MemoryStream(new byte[8])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        }
    }
}
