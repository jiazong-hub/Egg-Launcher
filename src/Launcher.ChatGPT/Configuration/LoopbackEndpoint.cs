using System.Net;

namespace Launcher.ChatGPT.Configuration;

internal static class LoopbackEndpoint
{
    public static string NormalizeBaseUrl(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri
            || baseUrl.Scheme != Uri.UriSchemeHttp
            || !(string.Equals(baseUrl.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(baseUrl.Host, out var address) && IPAddress.IsLoopback(address)))
        {
            throw new ArgumentException("本地服务只允许 HTTP 回环地址。", nameof(baseUrl));
        }

        return baseUrl.AbsoluteUri.TrimEnd('/') + "/";
    }
}
