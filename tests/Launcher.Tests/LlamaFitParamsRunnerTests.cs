using Launcher.Runtime.Fit;

namespace Launcher.Tests;

public sealed class LlamaFitParamsRunnerTests
{
    [Fact]
    public void TryParse_AcceptsOnlyNativeFittedArgumentLine()
    {
        const string output = "backend log\n-c 32768 -ngl 41 -ts 3,1 -ot \"blk\\..*\\.ffn_.*=CUDA0\"\n";

        var parsed = LlamaFitParamsRunner.TryParse(output, out var result);

        Assert.True(parsed);
        Assert.Equal(32768, result.ContextSize);
        Assert.Equal(41, result.GpuLayers);
        Assert.Equal("3,1", result.TensorSplit);
        Assert.Equal("blk\\..*\\.ffn_.*=CUDA0", result.TensorBufferOverrides);
    }

    [Theory]
    [InlineData("required memory: 1234 MiB")]
    [InlineData("-c nope -ngl 2")]
    [InlineData("-c 4096 -ngl -1")]
    public void TryParse_RejectsUnknownOrInvalidOutput(string output) =>
        Assert.False(LlamaFitParamsRunner.TryParse(output, out _));
}
