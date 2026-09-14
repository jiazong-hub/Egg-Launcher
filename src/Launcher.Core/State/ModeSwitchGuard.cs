using Launcher.Core.Configuration;

namespace Launcher.Core.State;

public static class ModeSwitchGuard
{
    public static bool RequiresSwitch(ProviderMode currentMode, ProviderMode targetMode) =>
        currentMode != targetMode;

    public static void EnsureCanSwitch(
        ProviderMode currentMode,
        ProviderMode targetMode,
        bool isChatGptDesktopRunning)
    {
        if (!RequiresSwitch(currentMode, targetMode))
        {
            return;
        }

        if (isChatGptDesktopRunning)
        {
            throw new InvalidOperationException("请先完全关闭 ChatGPT Desktop 后再切换模式。");
        }
    }

    public static void EnsureCanChangeRuntime(
        ProviderMode currentMode,
        string? currentRuntimeRoot,
        string requestedRuntimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRuntimeRoot);
        if (currentMode != ProviderMode.Local || PathsEqual(currentRuntimeRoot, requestedRuntimeRoot))
        {
            return;
        }

        throw new InvalidOperationException(
            "Local 模式正在使用当前 llama.cpp Runtime。请先切换到 OpenAI 模式，再更换 Runtime 文件夹。");
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
