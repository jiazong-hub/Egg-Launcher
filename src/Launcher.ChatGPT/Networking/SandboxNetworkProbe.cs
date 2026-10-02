using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.ChatGPT.Networking;

public sealed record SandboxNetworkProbeResult(string Code, string Details);

public static class SandboxNetworkProbe
{
    public static async Task<SandboxNetworkProbeResult> RunAsync(bool networkEnabled, bool compatibility,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new("runtime_unavailable", "此检测需要 Windows Codex 沙箱。");
        string codexDirectory;
        try { codexDirectory = await CodexSandboxRuntime.ResolveAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new("runtime_unavailable", "无法准备 Codex 沙箱运行组件：" + exception.Message);
        }
        var codex = Path.Combine(codexDirectory, "codex.exe");
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe");
        if (!File.Exists(pwsh))
            return new("runtime_unavailable", "未找到 Codex PowerShell 7 运行时，请先完成客户端运行时初始化；无需启动模型。");
        var proxy = compatibility ? SystemProxyReader.Read() : new SystemProxySettings(new Dictionary<string, string>(), "inherit");
        NetworkCompatibilityRuntime? runtime = null;
        try { runtime = NetworkCompatibilityRuntime.Resolve(); }
        catch (InvalidOperationException) { }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        var profile = "egg_network_probe_" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(codex)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = codexDirectory
        };
        void Add(params string[] args) { foreach (var arg in args) start.ArgumentList.Add(arg); }
        Add("sandbox", "--include-managed-config", "-C", Path.GetTempPath(), "-P", profile,
            "-c", $"permissions.{profile} = {{ extends = \":workspace\", network = {{ enabled = {networkEnabled.ToString().ToLowerInvariant()} }} }}");
        foreach (var pair in proxy.EnvironmentValues)
            Add("-c", "shell_environment_policy.set." + pair.Key + "=" + JsonSerializer.Serialize(pair.Value));
        static string Literal(string? value) => "'" + (value ?? "").Replace("'", "''") + "'";
        var script = "$eggPython=" + Literal(runtime?.PythonPath) + "; $eggHelper=" + Literal(runtime?.HelperPath) + ";\n" + Script;
        Add("--", pwsh, "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex 沙箱检测。");
        using var kill = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var json = output.Split('\n').LastOrDefault(line => line.StartsWith("EGG_NETWORK_RESULT:", StringComparison.Ordinal));
            if (json is null) return new("unavailable", "沙箱检测未产生结果（可能不支持当前接口或初始化失败）。\n" + error);
            using var parsed = JsonDocument.Parse(json["EGG_NETWORK_RESULT:".Length..]);
            var root = parsed.RootElement;
            var windows = root.GetProperty("windows").GetString();
            var openssl = root.GetProperty("openssl").GetString();
            var proxyFiltered = proxy.EnvironmentValues.ContainsKey("HTTPS_PROXY")
                && !root.GetProperty("proxy_present").GetBoolean();
            var failure = "failed";
            if (root.TryGetProperty("helper", out var helper))
            {
                failure = helper.TryGetProperty("proxy_tcp", out var tcp) && tcp.GetString() == "failed"
                    ? "proxy_unreachable" : helper.GetProperty("code").GetString() ?? "failed";
                if (failure == "timeout") failure = "request_timeout";
            }
            var code = !networkEnabled ? (windows == "ok" || openssl == "ok" ? "unexpected_access" : "disabled")
                : proxyFiltered ? "proxy_filtered"
                : compatibility && openssl == "unavailable" ? "helper_unavailable"
                : windows == "ok" && (!compatibility || openssl == "ok") ? "ok"
                : openssl == "ok" ? "windows_https" : failure;
            var details = JsonNode.Parse(json["EGG_NETWORK_RESULT:".Length..])!;
            details["proxy_source"] = proxy.Status;
            details["sandbox_runtime"] = "official_hash_verified_codex_cache";
            return new(code, details.ToJsonString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("timeout", "沙箱检测超时，没有将超时视为联网正常。");
        }
        finally
        {
            // Observe both drain tasks even when cancellation terminates the child process.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (output.Length < 32768) output.Append(buffer, 0, Math.Min(count, 32768 - output.Length));
        return output.ToString();
    }

    // Fixed, read-only diagnostic. No model calls, downloads, TLS bypass, or elevation.
    private const string Script = """
        # The Windows sandbox may default to CP936 while the caller reads UTF-8.
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $result = @{ user=[System.Security.Principal.WindowsIdentity]::GetCurrent().Name; windows='failed'; openssl='unavailable'; errors=@(); exception_metadata=@(); proxy_present=[bool]$env:HTTPS_PROXY; output_codepage=[Console]::OutputEncoding.CodePage }
        try {
            $options = @{ Uri='https://www.baidu.com/'; Method='Head'; TimeoutSec=12; ErrorAction='Stop' }
            if ($env:HTTPS_PROXY) { $options.Proxy = $env:HTTPS_PROXY }
            $r = Invoke-WebRequest @options
            $result.windows='ok'
            $result.http_status=[int]$r.StatusCode
        } catch {
            $e=$_.Exception; $depth=0
            while ($null -ne $e -and $depth -lt 6) {
                $result.errors += "$($e.GetType().Name): $($e.Message)"
                $metadata = @{ type=$e.GetType().FullName; hresult=('0x{0:X8}' -f $e.HResult) }
                if ($e -is [System.ComponentModel.Win32Exception]) {
                    $metadata.native_error_code = $e.NativeErrorCode
                    $metadata.native_error_hex = ('0x{0:X8}' -f $e.NativeErrorCode)
                }
                $result.exception_metadata += $metadata
                $e=$e.InnerException; $depth++
            }
        }
        if ($eggPython -and $eggHelper) {
            try {
                $output = & $eggPython $eggHelper 'https://www.baidu.com/' --head --timeout 12 2>&1
                $exit = $LASTEXITCODE
                $helper = ($output | Select-Object -Last 1 | Out-String).Trim() | ConvertFrom-Json -ErrorAction Stop
                $result.openssl = if ($exit -eq 0 -and $helper.ok) { 'ok' } else { 'failed' }
                $result.helper = $helper
            } catch {
                $result.openssl='unavailable'
                $result.errors += 'Compatibility helper could not run in the sandbox'
            }
        }
        # ASCII-safe JSON remains readable even if an intermediate console changes its code page.
        Write-Output ('EGG_NETWORK_RESULT:' + ($result | ConvertTo-Json -Compress -Depth 5 -EscapeHandling EscapeNonAscii))
        """;
}
