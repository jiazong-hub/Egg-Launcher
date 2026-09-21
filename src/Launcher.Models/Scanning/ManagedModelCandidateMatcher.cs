using Launcher.Models.Profiles;

namespace Launcher.Models.Scanning;

public static class ManagedModelCandidateMatcher
{
    public static ModelProfile? FindMatch(
        GgufModelCandidate candidate,
        string runtimeRoot,
        IEnumerable<ModelProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(profiles);

        var availableProfiles = profiles as IReadOnlyCollection<ModelProfile> ?? profiles.ToArray();
        if (!string.IsNullOrWhiteSpace(candidate.RemoteModelId))
        {
            var remoteMatch = availableProfiles.FirstOrDefault(profile => string.Equals(
                profile.RemoteModelId,
                candidate.RemoteModelId,
                StringComparison.OrdinalIgnoreCase));
            if (remoteMatch is not null)
            {
                return remoteMatch;
            }
        }

        var candidateIdentities = HuggingFaceCacheResolver.GetPathIdentities(candidate.PrimaryPath);
        return availableProfiles.FirstOrDefault(profile =>
        {
            var managedPath = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
            return HuggingFaceCacheResolver.GetPathIdentities(managedPath).Overlaps(candidateIdentities);
        });
    }
}
