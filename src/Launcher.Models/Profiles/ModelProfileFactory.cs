using System.Text;
using Launcher.Models.Scanning;

namespace Launcher.Models.Profiles;

public static class ModelProfileFactory
{
    public static ModelProfile RestoreModelDefaults(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.DefaultParameters?.ApplyTo(profile) ?? profile;
    }

    public static ModelProfile SaveCurrentParametersAsDefault(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { DefaultParameters = ModelParameterDefaults.FromProfile(profile) };
    }

    public static ModelProfile CreateDefault(
        GgufModelCandidate candidate,
        string runtimeRoot,
        IEnumerable<ModelProfile>? existingProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var root = Path.GetFullPath(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var modelPath = Path.GetFullPath(candidate.PrimaryPath);
        if (!modelPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("扫描到的模型不在 Runtime Root 内。", nameof(candidate));
        }

        var existing = (existingProfiles ?? Array.Empty<ModelProfile>()).ToArray();
        var baseIdentifier = MakeIdentifier(candidate.DisplayName);
        var identifier = baseIdentifier;
        for (var suffix = 2;
             existing.Any(profile => string.Equals(profile.Id, identifier, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(profile.Alias, identifier, StringComparison.OrdinalIgnoreCase));
             suffix++)
        {
            identifier = $"{baseIdentifier}-{suffix}";
        }

        return new ModelProfile
        {
            Id = identifier,
            DisplayName = candidate.DisplayName,
            ModelRelativePath = Path.GetRelativePath(root, modelPath),
            Alias = identifier,
            SourceKind = string.IsNullOrWhiteSpace(candidate.RemoteModelId)
                ? ModelSourceKind.LocalFile
                : ModelSourceKind.LlamaCache,
            RemoteModelId = candidate.RemoteModelId,
            RemoteRepositoryId = candidate.RemoteRepositoryId,
            RemoteQuantization = candidate.RemoteQuantization,
            KnownSizeBytes = candidate.TotalSizeBytes > 0 ? candidate.TotalSizeBytes : null,
            KnownShardCount = candidate.ShardCount > 0 ? candidate.ShardCount : null,
            CompactionSafetyReserve = ModelProfile.DefaultCompactionSafetyReserve,
        };
    }

    private static string MakeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value)
        {
            var normalized = char.ToLowerInvariant(character);
            if (char.IsAsciiLetterOrDigit(normalized) || normalized is '.' or '_')
            {
                builder.Append(normalized);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var identifier = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(identifier) ? "local-model" : identifier;
    }
}
