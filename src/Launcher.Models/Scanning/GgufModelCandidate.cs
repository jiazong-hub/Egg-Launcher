namespace Launcher.Models.Scanning;

public sealed record GgufModelCandidate(
    string PrimaryPath,
    string RelativePath,
    string DisplayName,
    long TotalSizeBytes,
    int ShardCount,
    string? RemoteModelId = null,
    string? RemoteRepositoryId = null,
    string? RemoteQuantization = null)
{
    public bool IsSharded => ShardCount > 1;
}
