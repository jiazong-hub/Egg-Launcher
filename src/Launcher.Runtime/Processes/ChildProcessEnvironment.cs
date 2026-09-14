using System.Diagnostics;

namespace Launcher.Runtime.Processes;

public static class ChildProcessEnvironment
{
    private static readonly string[] SensitivePrefixes =
    [
        "OPENAI_",
        "CHATGPT_",
        "CODEX_",
        "ANTHROPIC_",
    ];

    private static readonly string[] SensitiveSuffixes =
    [
        "_API_KEY",
        "_AUTH_TOKEN",
        "_ACCESS_TOKEN",
        "_CLIENT_SECRET",
    ];

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HF_TOKEN",
        "HUGGING_FACE_HUB_TOKEN",
        "AZURE_CLIENT_SECRET",
        "AWS_SECRET_ACCESS_KEY",
        "GITHUB_TOKEN",
        "NUGET_AUTH_TOKEN",
    };

    public static void RemoveSensitiveVariables(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (IsSensitive(name))
            {
                startInfo.Environment.Remove(name);
            }
        }
    }

    private static bool IsSensitive(string name) =>
        SensitiveNames.Contains(name)
        || SensitivePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || SensitiveSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
