using System.Text;

namespace Launcher.Runtime.Router;

public static class RouterModelDownloadLogInspector
{
    private const int MaximumTailBytes = 128 * 1024;

    public static string? FindFailure(LlamaRouterProcessInfo? processInfo)
    {
        if (processInfo is null)
        {
            return null;
        }

        var text = ReadTail(processInfo.StandardErrorLogPath) + "\n" + ReadTail(processInfo.StandardOutputLogPath);
        if (text.Contains("HTTPS is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return "当前 llama.cpp 构建不支持 HTTPS，无法从 Hugging Face 下载；请更换启用了 SSL 的官方构建。";
        }

        if (text.Contains("SSL connection failed", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp 与 Hugging Face 建立 TLS/SSL 连接失败；请检查系统时间、证书或公司网络策略。";
        }

        if (text.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp 无法解析 Hugging Face 域名；请检查 DNS 或公司网络策略。";
        }

        if (text.Contains("HTTPLIB failed: Could not establish connection", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp 无法连接 Hugging Face，实际模型字节尚未开始下载；请检查网络、防火墙或公司安全软件是否允许 llama-server.exe 联网。";
        }

        if (text.Contains("failed to resolve commit", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp 无法解析 Hugging Face 仓库版本，实际模型字节尚未开始下载。";
        }

        if (text.Contains("download failed", StringComparison.OrdinalIgnoreCase))
        {
            return "llama.cpp 原生下载失败；请查看本次下载 Router 日志。";
        }

        return null;
    }

    private static string ReadTail(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumTailBytes)
            {
                stream.Seek(-MaximumTailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
