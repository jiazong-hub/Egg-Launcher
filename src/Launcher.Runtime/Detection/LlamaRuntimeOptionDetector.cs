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
            return ParseHelp(output);
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

    /// <summary>Option names plus explicit enum/default evidence; an option name alone is not proof of a strategy.</summary>
    public static IReadOnlySet<string> ParseHelp(string output)
    {
        var result = OptionRegex().Matches(output)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typeBlock = Regex.Match(output, @"(?m)^.*--spec-type[^\r\n]*(?:\r?\n(?![^\r\n]*--)[^\r\n]*)*").Value;
        foreach (Match match in Regex.Matches(typeBlock, @"(?<![a-z0-9-])(draft-mtp|ngram-simple|ngram-map-k4v|ngram-map-k|ngram-mod|ngram-cache)(?![a-z0-9-])"))
            result.Add("spec-type:" + match.Value);
        foreach (Match block in Regex.Matches(output, @"(?m)^[^\r\n]*--(?<key>spec-ngram-[a-z0-9-]+)[^\r\n]*(?:\r?\n(?![^\r\n]*--)[^\r\n]*)*"))
        {
            var value = Regex.Match(block.Value, @"default:\s*(?<number>[0-9]+)");
            if (value.Success) result.Add($"option-default:{block.Groups["key"].Value}:{value.Groups["number"].Value}");
        }
        return result;
    }

    [GeneratedRegex("--(?<name>[a-z][a-z0-9-]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OptionRegex();
}
