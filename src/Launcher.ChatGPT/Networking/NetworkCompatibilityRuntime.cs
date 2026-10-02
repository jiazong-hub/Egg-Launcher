namespace Launcher.ChatGPT.Networking;

public sealed record NetworkCompatibilityRuntime(string PythonPath, string HelperPath)
{
    public static NetworkCompatibilityRuntime Resolve()
    {
        var python = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "python", "python.exe");
        var helper = Path.Combine(AppContext.BaseDirectory, "Networking", "egg-network.py");
        if (!File.Exists(python))
            throw new InvalidOperationException("联网兼容组件缺少 Codex Python 运行时。请先完成 Codex 运行时初始化，或关闭联网兼容模式。");
        if (!File.Exists(helper))
            throw new InvalidOperationException("联网兼容脚本缺失，请重新安装 Egg Launcher。");
        return new(python, helper);
    }

    public string Instructions => """
        [Egg Launcher network compatibility]
        Network compatibility is enabled for command HTTPS requests. Run the helper within the normal sandbox:
        PowerShell: & $env:EGG_NETWORK_PYTHON $env:EGG_NETWORK_HELPER 'https://example.com/'
        Connectivity check: add --head. Download: add --output '<new destination within allowed workspace>'.
        Prefer this helper for simple HTTPS reads/downloads instead of Invoke-WebRequest or Windows Schannel curl.
        It uses verified TLS/OpenSSL and the configured proxy; output is JSON with ok/code/http_status and a bounded text preview.
        Never disable certificate verification. Do not repeatedly retry Windows TLS credential failures or switch to unsandboxed execution.
        If the helper fails, report its code and obey current network, filesystem, and approval permissions.
        For git/package tools, keep their native commands; the helper does not transparently replace them or supply a web-search tool.
        """ + "\nIf environment filtering removes the helper variables, use these absolute paths instead:\n& '"
            + PythonPath.Replace("'", "''") + "' '" + HelperPath.Replace("'", "''") + "' 'https://example.com/'";
}
