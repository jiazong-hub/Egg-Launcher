namespace Launcher.Runtime.Router;

/// <summary>Checks prompts recorded by the isolated native inference process, not token counts.</summary>
public sealed class NativePromptEvidence(string directory)
{
    public async Task<bool> MatchesAsync(string expected, Func<Task<bool>> send, CancellationToken token)
    {
        var before = Directory.GetFiles(directory, "*.txt").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!await send().ConfigureAwait(false)) return false;
        var added = Directory.GetFiles(directory, "*.txt").Where(path => !before.Contains(path)).ToArray();
        if (added.Length != 1 || new FileInfo(added[0]).Length > 2 * 1024 * 1024) return false;
        // llama.cpp uses a text-mode std::ofstream: the Windows CRT expands every LF.
        // Transform only the expected log representation; never trim or collapse content.
        var expectedLog = OperatingSystem.IsWindows() ? expected.Replace("\n", "\r\n", StringComparison.Ordinal) : expected;
        return string.Equals(expectedLog, await File.ReadAllTextAsync(added[0], token).ConfigureAwait(false), StringComparison.Ordinal);
    }
}
