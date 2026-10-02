using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace Launcher.App;

internal sealed record CommonSandboxDirectory(string Name, string? Path, bool AllowWrite);

internal static class CommonSandboxDirectories
{
    public static async Task<IReadOnlyList<CommonSandboxDirectory>> DetectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var npm = QueryAsync("if (Get-Command npm.cmd -ErrorAction SilentlyContinue) { & npm.cmd config get cache 2>$null }", cancellationToken);
        var pip = QueryAsync("if (Get-Command python.exe -ErrorAction SilentlyContinue) { & python.exe -m pip cache dir 2>$null } elseif (Get-Command py.exe -ErrorAction SilentlyContinue) { & py.exe -m pip cache dir 2>$null }", cancellationToken);
        await Task.WhenAll(npm, pip);
        cancellationToken.ThrowIfCancellationRequested();
        var gradle = Env("GRADLE_USER_HOME") ?? Existing(System.IO.Path.Combine(home, ".gradle"));
        var nuget = Env("NUGET_PACKAGES") ?? Existing(System.IO.Path.Combine(home, ".nuget", "packages"));
        var hf = Env("HF_HUB_CACHE") ?? Env("HUGGINGFACE_HUB_CACHE");
        if (hf is null && Env("HF_HOME") is { } hfHome) hf = System.IO.Path.Combine(hfHome, "hub");
        hf ??= Existing(System.IO.Path.Combine(Env("XDG_CACHE_HOME") ?? System.IO.Path.Combine(home, ".cache"), "huggingface", "hub"));
        return
        [
            new(AppLanguageManager.Choose("Gradle 缓存", "Gradle cache"), Absolute(gradle), true),
            new(AppLanguageManager.Choose("Maven 本地仓库", "Maven repository"), MavenRepository(home), true),
            new(AppLanguageManager.Choose("npm 缓存", "npm cache"), Absolute(await npm), true),
            new(AppLanguageManager.Choose("pip 缓存", "pip cache"), Absolute(await pip), true),
            new(AppLanguageManager.Choose("NuGet 包缓存", "NuGet packages"), Absolute(nuget), true),
            new(AppLanguageManager.Choose("Hugging Face 缓存", "Hugging Face cache"), Absolute(hf), false),
        ];
    }

    private static string? MavenRepository(string home)
    {
        var settings = System.IO.Path.Combine(home, ".m2", "settings.xml");
        try
        {
            if (File.Exists(settings))
            {
                // Disable external entities; never load remote DTDs from a local configuration.
                using var reader = System.Xml.XmlReader.Create(settings, new System.Xml.XmlReaderSettings
                { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                var value = XDocument.Load(reader).Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "localRepository")?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    value = value.Replace("${user.home}", home, StringComparison.Ordinal);
                    value = System.Text.RegularExpressions.Regex.Replace(value, @"\$\{env\.([^}]+)\}",
                        match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
                    return value.Contains("${", StringComparison.Ordinal) ? null : Absolute(value);
                }
            }
            // Global Maven settings may redirect the repository. Ask users to browse in that case.
            var mavenHome = Env("MAVEN_HOME") ?? Env("M2_HOME");
            if (mavenHome is not null && File.Exists(System.IO.Path.Combine(mavenHome, "conf", "settings.xml")))
            {
                using var reader = System.Xml.XmlReader.Create(System.IO.Path.Combine(mavenHome, "conf", "settings.xml"),
                    new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                if (XDocument.Load(reader).Root?.Elements().Any(x => x.Name.LocalName == "localRepository") == true) return null;
            }
            return Existing(System.IO.Path.Combine(home, ".m2", "repository"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException) { return null; }
    }

    private static string? Env(string name) => Absolute(Environment.GetEnvironmentVariable(name));
    private static string? Existing(string path) => Directory.Exists(path) ? Absolute(path) : null;
    private static string? Absolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return System.IO.Path.IsPathFullyQualified(path.Trim()) ? System.IO.Path.GetFullPath(path.Trim()) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    private static async Task<string?> QueryAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + command)) })
                info.ArgumentList.Add(argument);
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(info);
            if (process is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await stderr;
                var output = (await stdout).Trim();
                return process.ExitCode == 0 && !output.Contains('\n') && !output.Contains('\r') ? Absolute(output) : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
}
