using System.Text.Json;
using Launcher.Core.Configuration;
using Launcher.Models.Profiles;

namespace Launcher.Orchestration.Models;

/// <summary>One committed runtime snapshot. Editing a user document never changes this file.</summary>
public sealed record AppliedLocalRun(int FormatVersion, string RuntimeRoot, ModelProfile Profile,
    string? ConfigurationDigest, bool CodexConfigurationApplied)
{
    public static Task WriteAsync(ModelProfile profile, string root, LauncherDataPaths paths, CancellationToken token = default,
        bool codexApplied = true) => ModelConfigurationFile.WriteAtomicallyAsync(paths.AppliedLocalRunFile,
        JsonSerializer.Serialize(new AppliedLocalRun(1, Path.GetFullPath(root), profile, profile.ConfigurationContentDigest, codexApplied)), token);

    public static async Task<ModelProfile> ReadAsync(string modelId, string root, LauncherDataPaths paths, CancellationToken token = default)
    {
        var snapshot = JsonSerializer.Deserialize<AppliedLocalRun>(await File.ReadAllTextAsync(paths.AppliedLocalRunFile, token).ConfigureAwait(false));
        if (snapshot is null || snapshot.FormatVersion != 1 || !snapshot.CodexConfigurationApplied
            || snapshot.Profile is null || snapshot.Profile.Id != modelId
            || !string.Equals(snapshot.RuntimeRoot, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("运行快照不可用，请关闭客户端后重新启动 / Close the client and start a new Local run.");
        return snapshot.Profile with { ConfigurationContentDigest = snapshot.ConfigurationDigest };
    }
}
