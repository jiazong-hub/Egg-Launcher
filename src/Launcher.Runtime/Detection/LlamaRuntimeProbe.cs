using Launcher.Runtime.Processes;

namespace Launcher.Runtime.Detection;

public sealed class LlamaRuntimeProbe(IProcessRunner processRunner)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<LlamaRuntimeProbeResult> ProbeAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var executablePath = Path.Combine(Path.GetFullPath(runtimeRoot), "llama-server.exe");
        if (!File.Exists(executablePath))
        {
            return new LlamaRuntimeProbeResult(
                executablePath,
                IsValid: false,
                VersionText: null,
                DeviceOutput: string.Empty,
                LlamaRuntimeCapabilities.FromHelpText(null),
                new[] { "所选目录中不存在 llama-server.exe。" });
        }

        var diagnostics = new List<string>();
        var version = await processRunner.RunAsync(
            executablePath,
            new[] { "--version" },
            ProbeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (version.TimedOut)
        {
            diagnostics.Add("llama-server --version 超时。");
        }
        else if (version.ExitCode is not 0)
        {
            diagnostics.Add($"llama-server --version 退出码：{version.ExitCode}。");
        }

        var help = await processRunner.RunAsync(
            executablePath,
            new[] { "--help" },
            ProbeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (help.TimedOut)
        {
            diagnostics.Add("llama-server --help 超时。");
        }

        var capabilities = LlamaRuntimeCapabilities.FromHelpText(help.CombinedOutput);
        var devices = string.Empty;

        if (capabilities.SupportsDeviceListing)
        {
            var deviceResult = await processRunner.RunAsync(
                executablePath,
                new[] { "--list-devices" },
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            devices = deviceResult.CombinedOutput;

            if (deviceResult.TimedOut)
            {
                diagnostics.Add("llama-server --list-devices 超时。");
            }
        }
        else
        {
            diagnostics.Add("当前 Runtime 未公开 --list-devices 能力。");
        }

        if (!capabilities.SupportsRouter)
        {
            diagnostics.Add("当前 Runtime 缺少完整 Router 参数，本版本不支持；请升级或选择兼容的 llama.cpp Runtime。");
        }


        if (!capabilities.SupportsIdleSleep)
        {
            diagnostics.Add("当前 Runtime 缺少 --sleep-idle-seconds，无法使用 llama.cpp 原生空闲休眠。");
        }

        if (!capabilities.SupportsChatTemplateFile)
        {
            diagnostics.Add("当前 Runtime 缺少 --chat-template-file，无法加载模型专用 Codex 兼容模板。");
        }

        var versionText = FirstNonEmptyLine(version.CombinedOutput);
        var isValid = !version.TimedOut && version.ExitCode is 0 && !string.IsNullOrWhiteSpace(versionText);

        return new LlamaRuntimeProbeResult(
            executablePath,
            isValid,
            versionText,
            devices,
            capabilities,
            diagnostics);
    }

    private static string? FirstNonEmptyLine(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
}
