using Launcher.Core.Configuration;
using Launcher.Core.State;

namespace Launcher.Tests;

public sealed class RuntimeSelectionPolicyTests
{
    [Fact]
    public void ApplyProbeResult_WhenRuntimeChanges_ClearsStaleModelSelection()
    {
        var settings = new LauncherSettings
        {
            LlamaRoot = @"D:\llama-old",
            SelectedModelId = "old-model",
        };

        var updated = RuntimeSelectionPolicy.ApplyProbeResult(
            settings,
            @"D:\llama-new",
            saveSelection: true,
            metricsSupported: true);

        Assert.Equal(@"D:\llama-new", updated.LlamaRoot);
        Assert.Null(updated.SelectedModelId);
        Assert.True(updated.LlamaMetricsSupported);
    }

    [Fact]
    public void ApplyProbeResult_WhenOnlyReprobingSameRuntime_PreservesModelSelection()
    {
        var settings = new LauncherSettings
        {
            LlamaRoot = @"D:\llama",
            SelectedModelId = "local-model",
        };

        var updated = RuntimeSelectionPolicy.ApplyProbeResult(
            settings,
            @"d:\llama\",
            saveSelection: true,
            metricsSupported: false);

        Assert.Equal("local-model", updated.SelectedModelId);
    }
}
