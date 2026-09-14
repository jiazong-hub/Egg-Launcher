namespace Launcher.Models.Scanning;

public sealed record GgufScanResult(
    IReadOnlyList<GgufModelCandidate> Models,
    IReadOnlyList<GgufExcludedFile> ExcludedFiles);

