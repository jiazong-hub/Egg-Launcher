using System.Diagnostics;
using System.Text.RegularExpressions;
using Launcher.Runtime.Processes;

namespace Launcher.Runtime.Detection;

/// <summary>Best-effort diagnostic lookup; callers serialize access with Router startup.</summary>
public sealed partial class LlamaRuntimeVersionReader(IProcessRunner processRunner)
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
    private ExecutableIdentity? _cachedIdentity;
    private LlamaRuntimeVersion? _cachedVersion;

    public async Task<LlamaRuntimeVersion> ReadAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutableIdentity identity;
        try
        {
            var file = new FileInfo(executablePath);
            identity = new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return new("unknown", "unavailable", "executable_unavailable");
        }

        if (_cachedIdentity == identity && _cachedVersion is not null) return _cachedVersion;

        LlamaRuntimeVersion? detected = null;
        string queryResult;
        try
        {
            var result = await processRunner.RunAsync(identity.Path, ["--version"], QueryTimeout, cancellationToken)
                .ConfigureAwait(false);
            var version = !result.TimedOut && result.ExitCode == 0 ? ParseOutput(result.CombinedOutput) : null;
            queryResult = result.TimedOut ? "timed_out" : result.ExitCode != 0 ? "nonzero_exit"
                : version is null ? "unrecognized_output" : "success";
            if (version is not null) detected = new(version, "version_command", queryResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            queryResult = "query_failed";
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (detected is null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(identity.Path);
                if (Normalize(info.ProductVersion) is { } product) detected = new(product, "product_version", queryResult);
                else if (Normalize(info.FileVersion) is { } file) detected = new(file, "file_version", queryResult);
            }
            catch
            {
                // Missing version resources must not prevent Router startup.
            }
        }

        _cachedIdentity = identity;
        return _cachedVersion = detected ?? new("unknown", "unavailable", queryResult);
    }

    public static string? ParseOutput(string output)
    {
        // CUDA/backend initialization may precede the actual version on either stream.
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(128))
        {
            if (line.Length > 512) continue;
            var match = VersionLine().Match(line.Trim());
            if (!match.Success) continue;
            var version = match.Groups["version"].Value;
            if (match.Groups["commit"].Success) version += "-" + match.Groups["commit"].Value;
            return Normalize(version);
        }
        return null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new string(value.Take(80)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+').ToArray());
        return string.IsNullOrEmpty(result) || (!result.Any(char.IsAsciiDigit) && !CommitToken().IsMatch(result)) ? null : result;
    }

    [GeneratedRegex(@"^(?:llama(?:\.cpp|-server|-cli)?\s+)?(?:version|build)\s*:\s*(?<version>[bv]?\d+(?:\.[0-9a-z]+)*(?:[-+][0-9a-z._-]+)?|[0-9a-f]{7,40})(?:\s+\((?<commit>[0-9a-f]{7,40})\))?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitToken();

    private sealed record ExecutableIdentity(string Path, long Length, long LastWriteTicks);
}

public sealed record LlamaRuntimeVersion(string Version, string Source, string QueryResult);
