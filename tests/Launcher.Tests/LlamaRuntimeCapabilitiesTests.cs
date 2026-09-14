using Launcher.Runtime.Detection;

namespace Launcher.Tests;

public sealed class LlamaRuntimeCapabilitiesTests
{
    [Fact]
    public void FromHelpText_WhenAllRouterOptionsExist_ReportsRouterSupport()
    {
        const string help = """
            --list-devices
            --models-dir PATH
            --models-preset PATH
            --models-max N
            --models-autoload
            --sleep-idle-seconds N
            --chat-template-file PATH
            --metrics
            """;

        var result = LlamaRuntimeCapabilities.FromHelpText(help);

        Assert.True(result.SupportsRouter);
        Assert.True(result.SupportsDeviceListing);
        Assert.True(result.SupportsIdleSleep);
        Assert.True(result.SupportsChatTemplateFile);
        Assert.True(result.SupportsMetrics);
    }

    [Fact]
    public void FromHelpText_WhenOneRouterOptionIsMissing_ReportsNoRouterSupport()
    {
        const string help = "--models-dir PATH --models-preset PATH --models-max N";

        var result = LlamaRuntimeCapabilities.FromHelpText(help);

        Assert.False(result.SupportsRouter);
        Assert.False(result.SupportsChatTemplateFile);
    }
}
