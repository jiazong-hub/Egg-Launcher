using Launcher.Core.Configuration;

namespace Launcher.Core.State;

public static class RuntimeSelectionPolicy
{
    public static LauncherSettings ApplyProbeResult(
        LauncherSettings settings,
        string probedRuntimeRoot,
        bool saveSelection,
        bool metricsSupported)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(probedRuntimeRoot);
        var root = Path.GetFullPath(probedRuntimeRoot);
        var changedRuntime = saveSelection && !PathsEqual(settings.LlamaRoot, root);
        return settings with
        {
            LlamaRoot = saveSelection ? root : settings.LlamaRoot,
            SelectedModelId = changedRuntime ? null : settings.SelectedModelId,
            LlamaMetricsSupported = metricsSupported,
        };
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }
}
