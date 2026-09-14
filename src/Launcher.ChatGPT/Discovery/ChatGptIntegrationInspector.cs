using System.Text.RegularExpressions;

namespace Launcher.ChatGPT.Discovery;

public sealed partial class ChatGptIntegrationInspector
{
    public static readonly IReadOnlySet<string> ManagedConfigurationKeys = new HashSet<string>(
        new[]
        {
            "model",
            "model_provider",
            "model_catalog_json",
            "openai_base_url",
            "oss_provider",
        },
        StringComparer.Ordinal);

    private readonly string _codexHome;

    public ChatGptIntegrationInspector(string? codexHomeOverride = null)
    {
        _codexHome = Path.GetFullPath(codexHomeOverride ?? ResolveCodexHome());
    }

    public Task<ChatGptIntegrationSnapshot> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configPath = Path.Combine(_codexHome, "config.toml");
        var keys = File.Exists(configPath)
            ? ReadTopLevelKeys(configPath, cancellationToken)
            : Array.Empty<string>();

        var managedKeys = keys
            .Where(ManagedConfigurationKeys.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new ChatGptIntegrationSnapshot(
            _codexHome,
            configPath,
            Directory.Exists(_codexHome),
            File.Exists(configPath),
            File.Exists(Path.Combine(_codexHome, "auth.json")),
            HasHistoryState(_codexHome),
            keys,
            managedKeys));
    }

    private static string ResolveCodexHome()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return configuredHome;
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, ".codex");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    private static string[] ReadTopLevelKeys(string configPath, CancellationToken cancellationToken)
    {
        var keys = new List<string>();

        foreach (var line in File.ReadLines(configPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                break;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var match = TopLevelAssignmentRegex().Match(trimmed);
            if (match.Success)
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasHistoryState(string codexHome)
    {
        if (!Directory.Exists(codexHome))
        {
            return false;
        }

        return Directory.EnumerateFiles(codexHome, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(fileName => fileName is not null)
            .Any(fileName =>
                fileName!.Contains("history", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("session", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("state", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("^([A-Za-z0-9_-]+)\\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelAssignmentRegex();
}
