using System.IO;
using System.Text.Json;
using System.Windows;
using Launcher.ChatGPT.Networking;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Models.Profiles;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private CancellationTokenSource? _networkProbeCancellation;

    private SandboxNetworkAccess CurrentSandboxNetworkAccess => SandboxNetworkCheckBox.IsChecked switch
    {
        true => SandboxNetworkAccess.Full,
        false => SandboxNetworkAccess.Disabled,
        _ => SandboxNetworkAccess.InheritCodexSettings,
    };

    private void SandboxNetworkToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingSandbox || SandboxCompatibilityCheckBox is null) return;
        if (SandboxNetworkCheckBox.IsChecked != true) SandboxCompatibilityCheckBox.IsChecked = false;
        MarkSandboxSettingsEdited();
        UpdateSandboxNetworkState();
    }

    private void SandboxCompatibility_Changed(object sender, RoutedEventArgs e)
    {
        MarkSandboxSettingsEdited();
        if (!_isPopulatingSandbox) ClearNetworkProbeResult();
    }

    private void ClearNetworkProbeResult()
    {
        _networkProbeCancellation?.Cancel();
        SandboxNetworkResultTextBlock.Text = string.Empty;
        SandboxNetworkDetailsTextBox.Text = string.Empty;
    }

    private void UpdateSandboxNetworkState()
    {
        SandboxCompatibilityCheckBox.IsEnabled = SandboxNetworkCheckBox.IsChecked == true;
        SandboxNetworkStateTextBlock.Text = CurrentSandboxNetworkAccess switch
        {
            SandboxNetworkAccess.Full => AppLanguageManager.Choose("允许命令联网；保存后重新启动客户端，并确认聊天选择 egg_launcher_active。", "Network allowed. Save, restart the client, and select egg_launcher_active in the chat."),
            SandboxNetworkAccess.Disabled => AppLanguageManager.Choose("禁止命令联网。", "Command network access disabled."),
            _ => AppLanguageManager.Choose("沿用 Codex 当前设置（开关为中间状态）；旧模型不会被自动改动。", "Inherit Codex settings (indeterminate switch); existing models remain unchanged."),
        };
        if (!_isPopulatingSandbox) ClearNetworkProbeResult();
    }

    private async void SandboxCheckNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSandboxNetworkAccess == SandboxNetworkAccess.InheritCodexSettings)
        {
            SandboxNetworkResultTextBlock.Text = AppLanguageManager.Choose("请先明确开启或关闭允许命令联网，再检查该设置。", "Select network on or off before checking this setting.");
            return;
        }
        _networkProbeCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _networkProbeCancellation = cancellation;
        Closed += CancelNetworkProbe;
        SandboxCheckNetworkButton.IsEnabled = false;
        SandboxNetworkDetailsTextBox.Text = string.Empty;
        SandboxNetworkResultTextBlock.Text = AppLanguageManager.Choose("正在检查独立 Codex 沙箱，最多约 75 秒…", "Checking an isolated Codex sandbox, up to 75 seconds…");
        try
        {
            var result = await SandboxNetworkProbe.RunAsync(SandboxNetworkCheckBox.IsChecked == true,
                SandboxCompatibilityCheckBox.IsChecked == true, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            SandboxNetworkResultTextBlock.Text = result.Code switch
            {
                "ok" => AppLanguageManager.Choose("联网正常（诊断目标 HTTPS 可访问）。", "Network OK (diagnostic HTTPS target reachable)."),
                "unexpected_access" => AppLanguageManager.Choose("设置为禁止联网，但诊断仍可访问网站；请检查管理员策略和沙箱生效状态。", "Network was disabled but the target remained reachable; inspect policy and sandbox state."),
                "disabled" => AppLanguageManager.Choose("当前检查设置为禁止联网；详细结果见下方。", "Network disabled for this diagnostic; see details below."),
                "windows_https" => AppLanguageManager.Choose("Windows HTTPS 检查失败，但 Python HTTPS 检查成功；具体原因见详细信息。", "Windows HTTPS failed but Python HTTPS succeeded; inspect details for the cause."),
                "helper_unavailable" => AppLanguageManager.Choose("兼容组件无法在沙箱中运行，请检查运行时或重新安装。", "The compatibility helper could not run in the sandbox; inspect the runtime or reinstall."),
                "runtime_unavailable" => AppLanguageManager.Choose("Codex 沙箱组件不完整或无法定位，请检查客户端安装；无需启动模型。", "Codex sandbox components are missing or could not be located; inspect the client installation. Model startup is unnecessary."),
                "proxy_filtered" => AppLanguageManager.Choose("系统代理变量未进入沙箱，可能被 Codex 环境过滤规则移除；请查看详细信息。", "System proxy variables did not reach the sandbox; inspect Codex environment filters and details."),
                "proxy_unreachable" => AppLanguageManager.Choose("沙箱无法连接系统代理，请确认代理软件正在运行。", "The sandbox cannot connect to the system proxy; check that your proxy app is running."),
                "dns_error" => AppLanguageManager.Choose("地址解析失败，请检查网络或代理设置。", "Address resolution failed; check networking or proxy settings."),
                "certificate_error" => AppLanguageManager.Choose("HTTPS 证书验证失败，请检查系统时间、证书或代理证书配置。", "HTTPS certificate validation failed; inspect system time, certificates, or proxy certificates."),
                "tls_error" => AppLanguageManager.Choose("兼容工具的 TLS 握手也失败，请查看详细信息。", "The compatibility helper also failed the TLS handshake; inspect details."),
                "request_timeout" => AppLanguageManager.Choose("网站请求超时，请检查网络或代理连接。", "The website request timed out; inspect networking or proxy connectivity."),
                "http_error" => AppLanguageManager.Choose("网站已响应，但返回 HTTP 错误；状态码见详细信息。", "The website responded with an HTTP error; see details for the status."),
                "permission_denied" => AppLanguageManager.Choose("沙箱拒绝了连接或组件访问，请核对生效的权限配置。", "The sandbox denied the connection or component access; inspect active permissions."),
                "connection_refused" => AppLanguageManager.Choose("连接被拒绝，请检查目标服务或代理是否可用。", "Connection refused; check target service or proxy availability."),
                "request_failed" => AppLanguageManager.Choose("兼容请求失败，请查看详细信息。", "The compatibility request failed; inspect details."),
                "failed" => AppLanguageManager.Choose("HTTPS 检查失败，请查看详细信息；有系统代理时可尝试联网兼容模式。", "HTTPS check failed. Inspect details; try compatibility mode if you use a system proxy."),
                "timeout" => AppLanguageManager.Choose("沙箱检查超时，请查看诊断日志。", "Sandbox check timed out; inspect diagnostics."),
                _ => AppLanguageManager.Choose("无法完成沙箱检查，请查看详细信息。", "Sandbox check unavailable; inspect details."),
            };
            if (result.Details.StartsWith("{", StringComparison.Ordinal))
            {
                using var details = JsonDocument.Parse(result.Details);
                if (details.RootElement.TryGetProperty("proxy_source", out var source)
                    && source.GetString() == "no_system_proxy")
                    SandboxNetworkResultTextBlock.Text += AppLanguageManager.Choose(
                        " 未检测到固定系统代理，兼容模式没有注入代理。",
                        " No fixed system proxy was found; compatibility mode injected no proxy.");
            }
            SandboxNetworkDetailsTextBox.Text = AppLanguageManager.Choose(
                "范围：当前编辑设置的独立 Codex 沙箱，不代表已有聊天权限。目标：https://www.baidu.com/。\n",
                "Scope: isolated Codex sandbox for edited settings, not existing chat permissions. Target: https://www.baidu.com/.\n") + result.Details;
            await WriteNetworkProbeDiagnosticAsync(result.Code, result.Details);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SandboxNetworkResultTextBlock.Text = AppLanguageManager.Choose("检查未完成：", "Check incomplete: ") + exception.Message;
            await WriteNetworkProbeDiagnosticAsync("error", exception.Message);
        }
        finally
        {
            Closed -= CancelNetworkProbe;
            if (ReferenceEquals(_networkProbeCancellation, cancellation)) _networkProbeCancellation = null;
            SandboxCheckNetworkButton.IsEnabled = true;
        }
    }

    private void CancelNetworkProbe(object? sender, EventArgs e) => _networkProbeCancellation?.Cancel();

    private async Task WriteNetworkProbeDiagnosticAsync(string result, string? details = null)
    {
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser();
            var detailed = false;
            if (File.Exists(paths.SettingsFile))
            {
                using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
                detailed = settings.RootElement.TryGetProperty("DetailedDiagnosticsEnabled", out var flag) && flag.ValueKind == JsonValueKind.True;
            }
            var log = new ModeAwareJsonLineDiagnosticLog(Path.Combine(paths.LogsDirectory, "sandbox-network.concise.jsonl"),
                Path.Combine(paths.LogsDirectory, "sandbox-network.full.jsonl"), detailed);
            var properties = new Dictionary<string, object?>
            {
                ["result"] = result,
                ["scope"] = "isolated_codex_sandbox",
                ["networkEnabled"] = SandboxNetworkCheckBox.IsChecked == true,
                ["compatibilityEnabled"] = SandboxCompatibilityCheckBox.IsChecked == true
            };
            if (details?.StartsWith("{", StringComparison.Ordinal) == true)
            {
                using var parsed = JsonDocument.Parse(details);
                foreach (var key in new[] { "windows", "openssl", "proxy_present", "proxy_source", "http_status", "helper", "sandbox_runtime", "output_codepage" })
                    if (parsed.RootElement.TryGetProperty(key, out var value)) properties[key] = value.Clone();
                if (detailed && parsed.RootElement.TryGetProperty("errors", out var errors))
                    properties["exceptionChain"] = errors.EnumerateArray()
                        .Select(value => DiagnosticSanitizer.SanitizeText(value.GetString(), 500)).ToArray();
                if (detailed && parsed.RootElement.TryGetProperty("exception_metadata", out var metadata))
                    properties["exceptionMetadata"] = metadata.Clone();
            }
            else if (detailed && !string.IsNullOrWhiteSpace(details))
            {
                properties["failureDetails"] = DiagnosticSanitizer.SanitizeText(details, 2500);
            }
            await log.AppendEventAsync(new DiagnosticEvent("sandbox_network_check", "info", Properties: properties));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
