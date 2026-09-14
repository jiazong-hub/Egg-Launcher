using System.Diagnostics;
using System.Runtime.Versioning;

namespace Launcher.ChatGPT.Processes;

public sealed class ChatGptClientLauncher(
    IChatGptClientDetector clientDetector,
    ChatGptClientInstallationLocator installationLocator)
{
    [SupportedOSPlatform("windows")]
    public ChatGptClientLaunchResult Launch()
    {
        if (clientDetector.IsRunning())
        {
            return new ChatGptClientLaunchResult(true, true, null);
        }

        var installation = installationLocator.Locate();
        if (installation is null)
        {
            return new ChatGptClientLaunchResult(
                false,
                false,
                "未找到身份、发布者和应用入口均可信的 ChatGPT Desktop MSIX 安装包。"
                + "请通过 Microsoft Store 产品 9PLM9XGG6VKS 安装或修复客户端。");
        }

        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = explorerPath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($@"shell:AppsFolder\{installation.AppUserModelId}");
        using var process = Process.Start(startInfo);
        return process is null
            ? new ChatGptClientLaunchResult(false, false, "Windows Shell 未接受 ChatGPT 启动请求。")
            : new ChatGptClientLaunchResult(true, false, null);
    }
}
