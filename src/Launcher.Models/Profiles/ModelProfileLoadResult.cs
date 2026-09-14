namespace Launcher.Models.Profiles;

public sealed record ModelProfileLoadResult(
    IReadOnlyList<ModelProfile> Profiles,
    IReadOnlyList<string> Diagnostics);
