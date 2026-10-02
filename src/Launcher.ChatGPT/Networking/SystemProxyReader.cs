using Microsoft.Win32;

namespace Launcher.ChatGPT.Networking;

public sealed record SystemProxySettings(IReadOnlyDictionary<string, string> EnvironmentValues, string Status);

public static class SystemProxyReader
{
    public static SystemProxySettings Read()
    {
        if (!OperatingSystem.IsWindows()) return new(new Dictionary<string, string>(), "not_windows");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) != 0;
        var raw = key?.GetValue("ProxyServer") as string;
        if (!enabled || string.IsNullOrWhiteSpace(raw))
        {
            if (!string.IsNullOrWhiteSpace(key?.GetValue("AutoConfigURL") as string))
                throw new InvalidOperationException("系统使用自动代理脚本（PAC），当前兼容模式不支持，请使用代理软件的固定系统代理。");
            return new(new Dictionary<string, string>(), "no_system_proxy");
        }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!raw.Contains('='))
        {
            var address = Normalize(raw);
            values["HTTP_PROXY"] = address;
            values["HTTPS_PROXY"] = address;
        }
        else
        {
            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2) continue;
                var protocol = pair[0].Trim().ToLowerInvariant();
                if (protocol is "http" or "https")
                    values[protocol.ToUpperInvariant() + "_PROXY"] = Normalize(pair[1]);
            }
            if (!values.ContainsKey("HTTPS_PROXY"))
                throw new InvalidOperationException("系统代理没有可用于 HTTPS 的固定代理地址。");
        }
        return new(values, "system_proxy_available");
    }

    private static string Normalize(string value)
    {
        value = value.Trim();
        if (!value.Contains("://")) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("系统代理地址无效或需要账号密码，无法自动传入沙箱。");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
