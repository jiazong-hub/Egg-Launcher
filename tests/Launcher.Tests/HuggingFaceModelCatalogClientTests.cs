using System.Net;
using Launcher.Models.Remote;

namespace Launcher.Tests;

public sealed class HuggingFaceModelCatalogClientTests
{
    [Fact]
    public async Task SearchAsync_ReadsPublicGgufMetadata()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Contains("search=Qwen3%20Coder", request.RequestUri!.Query, StringComparison.Ordinal);
            Assert.Contains("filter=gguf", request.RequestUri.Query, StringComparison.Ordinal);
            return JsonResponse("""
                [{
                  "id":"org/Qwen3-Coder-GGUF",
                  "author":"org",
                  "pipeline_tag":"text-generation",
                  "downloads":1234,
                  "likes":56,
                  "lastModified":"2026-08-01T12:00:00Z",
                  "sha":"abcdef",
                  "gated":false,
                  "tags":["gguf","license:apache-2.0"]
                }]
                """);
        }));
        var client = new HuggingFaceModelCatalogClient(httpClient);

        var results = await client.SearchAsync("Qwen3 Coder");

        var model = Assert.Single(results);
        Assert.Equal("org/Qwen3-Coder-GGUF", model.RepositoryId);
        Assert.Equal("apache-2.0", model.License);
        Assert.Equal(1234, model.Downloads);
        Assert.False(model.IsGated);
    }

    [Fact]
    public async Task GetDetailsAsync_GroupsCompleteShardsAndExcludesSidecars()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Contains("/api/models/org/model/tree/abcdef", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return JsonResponse("""
                [
                  {"type":"file","path":"model-Q4_K_M-00001-of-00002.gguf","size":100},
                  {"type":"file","path":"model-Q4_K_M-00002-of-00002.gguf","size":150},
                  {"type":"file","path":"model-Q8_0.gguf","lfs":{"size":400}},
                  {"type":"file","path":"model-TQ1_0.gguf","size":90},
                  {"type":"file","path":"model-UD-Q4_K_XL.gguf","size":110},
                  {"type":"file","path":"mmproj-model-F16.gguf","size":50},
                  {"type":"file","path":"mtp-model-F16.gguf","size":60},
                  {"type":"file","path":"broken-Q5_K_M-00001-of-00002.gguf","size":80}
                ]
                """);
        }));
        var client = new HuggingFaceModelCatalogClient(httpClient);
        var model = new HuggingFaceModelSearchResult(
            "org/model", "org", null, null, false, 0, 0, null, "abcdef");

        var details = await client.GetDetailsAsync(model);

        Assert.Equal(4, details.Variants.Count);
        var q4 = Assert.Single(details.Variants, variant => variant.Quantization == "Q4_K_M");
        Assert.Equal(250, q4.TotalSizeBytes);
        Assert.Equal(2, q4.ShardCount);
        Assert.EndsWith("00001-of-00002.gguf", q4.PrimaryFilePath, StringComparison.OrdinalIgnoreCase);
        var q8 = Assert.Single(details.Variants, variant => variant.Quantization == "Q8_0");
        Assert.Equal(400, q8.TotalSizeBytes);
        Assert.Contains(details.Variants, variant => variant.Quantization == "TQ1_0");
        Assert.Contains(details.Variants, variant => variant.Quantization == "UD-Q4_K_XL");
        var mtp = Assert.Single(details.ExternalMtpFiles);
        Assert.Equal("mtp-model-F16.gguf", mtp.Path);
        Assert.Equal(60, mtp.SizeBytes);
        Assert.Equal("abcdef", mtp.Revision);
        var vision = Assert.Single(details.ExternalVisionFiles);
        Assert.Equal("mmproj-model-F16.gguf", vision.Path);
        Assert.Equal(50, vision.SizeBytes);
    }

    [Fact]
    public void WebUriBuilder_CreatesRepositoryAndPinnedVariantPages()
    {
        var model = new HuggingFaceModelSearchResult(
            "org/model name", "org", null, null, false, 0, 0, null, "abc123");
        var variant = new HuggingFaceGgufVariant(
            model.RepositoryId,
            "Q4_K_M",
            "weights/model Q4_K_M.gguf",
            100,
            1);

        var repositoryUri = HuggingFaceWebUriBuilder.BuildRepositoryUri(model.RepositoryId);
        var variantUri = HuggingFaceWebUriBuilder.BuildVariantUri(model, variant);

        Assert.Equal("https://huggingface.co/org/model%20name", repositoryUri.AbsoluteUri);
        Assert.Equal(
            "https://huggingface.co/org/model%20name/blob/abc123/weights/model%20Q4_K_M.gguf",
            variantUri.AbsoluteUri);
    }

    [Fact]
    public void WebUriBuilder_RejectsCrossRepositoryOrTraversalPaths()
    {
        var model = new HuggingFaceModelSearchResult(
            "org/model", "org", null, null, false, 0, 0, null, null);
        var otherRepository = new HuggingFaceGgufVariant(
            "other/model", "Q4_K_M", "model-Q4_K_M.gguf", 100, 1);
        var traversal = new HuggingFaceGgufVariant(
            model.RepositoryId, "Q4_K_M", "../model-Q4_K_M.gguf", 100, 1);

        Assert.Throws<ArgumentException>(() =>
            HuggingFaceWebUriBuilder.BuildVariantUri(model, otherRepository));
        Assert.Throws<ArgumentException>(() =>
            HuggingFaceWebUriBuilder.BuildVariantUri(model, traversal));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
