namespace Launcher.ChatGPT.Configuration;

using Launcher.Core.Configuration;

public sealed record ManagedConfigSnapshot
{
    public required Guid TransactionId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required ConfigTransactionStage Stage { get; init; }

    public required string ConfigPath { get; init; }

    public required bool OriginalConfigExisted { get; init; }

    public required string OriginalConfigSha256 { get; init; }

    public string? BackupPath { get; init; }

    public required IReadOnlyDictionary<string, string?> OriginalAssignments { get; init; }

    public required IReadOnlyDictionary<string, string?> AppliedAssignments { get; init; }

    public IReadOnlyDictionary<string, string?>? PreviousAppliedAssignments { get; init; }

    public LauncherSettings? OriginalLauncherSettings { get; init; }

    public LauncherSettings? TargetLauncherSettings { get; init; }

    public bool SettingsCommitted { get; init; } = true;
}
