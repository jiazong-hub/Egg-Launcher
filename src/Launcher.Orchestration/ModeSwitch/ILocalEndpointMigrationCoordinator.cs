using Launcher.Core.Configuration;

namespace Launcher.Orchestration.ModeSwitch;

public interface ILocalEndpointMigrationCoordinator
{
    Task<LauncherSettings> MigrateAsync(
        LauncherSettings expectedSettings,
        Uri publicBaseUri,
        CancellationToken cancellationToken = default);
}
