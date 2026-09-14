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
}
