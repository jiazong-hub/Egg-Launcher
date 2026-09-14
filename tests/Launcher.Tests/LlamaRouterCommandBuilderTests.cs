using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class LlamaRouterCommandBuilderTests
{
    [Fact]
    public void BuildArguments_UsesSingleModelAutoloadRouterMode()
    {
        var root = Path.GetTempPath();
        var options = new LlamaRouterOptions
        {
            ModelsDirectory = Path.Combine(root, "models"),
            ModelsPresetPath = Path.Combine(root, "router-presets.ini"),
        };

        var arguments = LlamaRouterCommandBuilder.BuildArguments(options);

        Assert.Contains("--models-dir", arguments);
        Assert.Contains("--models-preset", arguments);
        Assert.Contains("--models-max", arguments);
        Assert.Contains("--models-autoload", arguments);
        var argumentsArray = arguments.ToArray();
        Assert.Equal("1", argumentsArray[Array.IndexOf(argumentsArray, "--models-max") + 1]);
    }

    [Fact]
    public void BuildArguments_RejectsNonLoopbackHost()
    {
        var options = new LlamaRouterOptions
        {
            Host = "0.0.0.0",
            ModelsDirectory = Path.GetTempPath(),
            ModelsPresetPath = Path.Combine(Path.GetTempPath(), "router-presets.ini"),
        };

        Assert.Throws<ArgumentException>(() => LlamaRouterCommandBuilder.BuildArguments(options));
    }

    [Fact]
    public void BuildArguments_AllowsPresetOnlyRouter()
    {
        var options = new LlamaRouterOptions
        {
            ModelsPresetPath = Path.Combine(Path.GetTempPath(), "router-presets.ini"),
        };

        var arguments = LlamaRouterCommandBuilder.BuildArguments(options);

        Assert.DoesNotContain("--models-dir", arguments);
        Assert.Contains("--models-preset", arguments);
    }

    [Fact]
    public void BuildArguments_AllowsDirectoryOnlyManagementRouterWithoutAutoload()
    {
        var options = new LlamaRouterOptions
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), "models"),
            ModelsPresetPath = null,
            AutoloadModels = false,
            ApiKey = "temporary-secret",
            DisableMultimodalProjectorAutoDownload = true,
        };

        var arguments = LlamaRouterCommandBuilder.BuildArguments(options);

        Assert.Contains("--models-dir", arguments);
        Assert.DoesNotContain("--models-preset", arguments);
        Assert.Contains("--no-models-autoload", arguments);
        Assert.DoesNotContain("--models-autoload", arguments);
        var argumentsArray = arguments.ToArray();
        Assert.Equal("temporary-secret", argumentsArray[Array.IndexOf(argumentsArray, "--api-key") + 1]);
        Assert.Contains("--no-mmproj", arguments);
    }

    [Fact]
    public void BuildArguments_RejectsRouterWithoutAnyModelSource()
    {
        var options = new LlamaRouterOptions
        {
            ModelsDirectory = null,
            ModelsPresetPath = null,
        };

        Assert.Throws<ArgumentException>(() => LlamaRouterCommandBuilder.BuildArguments(options));
    }

    [Fact]
    public void BuildArguments_EnablesNativeMetricsOnlyWhenRequested()
    {
        var enabled = LlamaRouterCommandBuilder.BuildArguments(new LlamaRouterOptions
        {
            ModelsDirectory = Path.GetTempPath(),
            EnableMetrics = true,
        });
        var disabled = LlamaRouterCommandBuilder.BuildArguments(new LlamaRouterOptions
        {
            ModelsDirectory = Path.GetTempPath(),
        });

        Assert.Contains("--metrics", enabled);
        Assert.DoesNotContain("--metrics", disabled);
    }
}
