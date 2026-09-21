using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Launcher.Runtime.Detection;

public static partial class LlamaRuntimeOptionDetector
{
    public static async Task<IReadOnlySet<string>?> DetectAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var executable = Path.Combine(Path.GetFullPath(runtimeRoot), "llama-server.exe");
        if (!File.Exists(executable)) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--help");
        try
        {
            if (!process.Start()) return null;
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false) + Environment.NewLine
                + await standardError.ConfigureAwait(false);
            return OptionRegex().Matches(output)
                .Select(match => match.Groups["name"].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or System.ComponentModel.Win32Exception
                                          or OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    [GeneratedRegex("--(?<name>[a-z][a-z0-9-]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OptionRegex();
}
