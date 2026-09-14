using Launcher.Core.Configuration;

namespace Launcher.Core.Persistence;

public interface ISettingsStore
{
    Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default);
}

