namespace Launcher.ChatGPT.Configuration;

public sealed record LegacyBackupMigrationResult(
    int MigratedCount,
    int RemainingPlaintextCount);
