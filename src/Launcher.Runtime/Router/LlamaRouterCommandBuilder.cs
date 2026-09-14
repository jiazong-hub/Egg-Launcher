using System.Globalization;
using System.Net;

namespace Launcher.Runtime.Router;

public static class LlamaRouterCommandBuilder
{
    public static IReadOnlyList<string> BuildArguments(LlamaRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelsPresetPath)
            && string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            throw new ArgumentException("Router 至少需要模型目录或模型预设。", nameof(options));
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Router 端口必须在 1 到 65535 之间。");
        }

        if (options.MaximumLoadedModels != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "V1 必须限制为同时加载一个本地模型。");
        }

        if (!IsLoopbackHost(options.Host))
        {
            throw new ArgumentException("V1 Router 只能监听回环地址。", nameof(options));
        }

        if (!string.IsNullOrWhiteSpace(options.ApiKey)
            && (options.ApiKey.Length > 256 || options.ApiKey.IndexOfAny(['\r', '\n', '\0']) >= 0))
        {
            throw new ArgumentException("Router API key 无效。", nameof(options));
        }

        var arguments = new List<string>
        {
            "--host",
            options.Host,
            "--port",
            options.Port.ToString(CultureInfo.InvariantCulture),
            "--models-max",
            options.MaximumLoadedModels.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(options.ModelsPresetPath))
        {
            arguments.Add("--models-preset");
            arguments.Add(Path.GetFullPath(options.ModelsPresetPath));
        }

        if (!string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            arguments.Add("--models-dir");
            arguments.Add(Path.GetFullPath(options.ModelsDirectory));
        }


        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            arguments.Add("--api-key");
            arguments.Add(options.ApiKey);
        }

        if (options.DisableMultimodalProjectorAutoDownload)
        {
            arguments.Add("--no-mmproj");
        }

        if (options.EnableMetrics)
        {
            arguments.Add("--metrics");
        }

        if (options.AutoloadModels)
        {
            arguments.Add("--models-autoload");
        }
        else
        {
            arguments.Add("--no-models-autoload");
        }

        return arguments;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
