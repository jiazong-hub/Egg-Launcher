using System.Text.Json;
using Launcher.Runtime.Monitoring;

namespace Launcher.Tests;

public sealed class LlamaTelemetryClientTests
{
    [Fact]
    public void ParseMetrics_ReadsOnlyFinitePrometheusSamples()
    {
        var values = LlamaTelemetryClient.ParseMetrics("""
            # HELP ignored
            llamacpp:prompt_tokens_total 100
            llamacpp:prompt_tokens_seconds 4.0
            invalid line
            llamacpp:bad NaN
            """);

        Assert.Equal(100, values["prompt_tokens_total"]);
        Assert.Equal(4, values["prompt_tokens_seconds"]);
        Assert.DoesNotContain("bad", values.Keys);
    }

    [Fact]
    public void ParseSlots_UsesNativeSlotTokenCounts()
    {
        using var document = JsonDocument.Parse("""
            [{"n_ctx":4096,"n_past":100,"is_processing":true},
             {"n_ctx":4096,"n_tokens":50,"is_processing":false}]
            """);

        var result = LlamaTelemetryClient.ParseSlots(document.RootElement);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result.Processing);
        Assert.Equal(4096, result.Capacity);
        Assert.Equal(100, result.Used);
    }

    [Fact]
    public void ParseSlots_UsesCurrentPromptTokenSchema()
    {
        using var document = JsonDocument.Parse("""
            [{"id":0,"id_task":42,"n_ctx":32768,"n_prompt_tokens":6144,
              "n_prompt_tokens_processed":6000,"n_prompt_tokens_cache":4096,
              "is_processing":true,"next_token":[{"n_decoded":128}]}]
            """);

        var result = LlamaTelemetryClient.ParseSlots(document.RootElement);

        Assert.Equal(1, result.Count);
        Assert.Equal(1, result.Processing);
        Assert.Equal(32768, result.Capacity);
        Assert.Equal(6144, result.Used);
    }

    [Fact]
    public async Task ReadAsync_DerivesLiveRateFromSlotTokenDelta_WhenMetricsAreUnavailable()
    {
        using var http = new HttpClient(new SlotSequenceHandler());
        var client = new LlamaTelemetryClient(http);

        var first = await client.ReadAsync(new Uri("http://127.0.0.1:8080/"), "local-model");
        await Task.Delay(100);
        var second = await client.ReadAsync(new Uri("http://127.0.0.1:8080/"), "local-model");

        Assert.Null(first.PredictedTokensPerSecond);
        Assert.NotNull(second.PredictedTokensPerSecond);
        Assert.True(second.PredictedTokensPerSecond > 0);
        Assert.Equal(110, second.ContextUsedTokens);
        Assert.Null(second.Diagnostic);
    }

    private sealed class SlotSequenceHandler : HttpMessageHandler
    {
        private int _slotReads;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/slots", StringComparison.Ordinal) == true)
            {
                var read = Interlocked.Increment(ref _slotReads);
                var decoded = read == 1 ? 10 : 20;
                var used = read == 1 ? 100 : 110;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        [{"id":0,"id_task":7,"n_ctx":4096,"n_prompt_tokens":{{used}},
                           "is_processing":true,"next_token":[{"n_decoded":{{decoded}}}]}]
                        """),
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        }
    }
}
