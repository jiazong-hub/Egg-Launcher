using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Launcher.ChatGPT.Processes;

public sealed class ChatGptClientDetector(
    ChatGptClientInstallationLocator? installationLocator = null) : IChatGptClientDetector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private readonly ChatGptClientInstallationLocator _installationLocator =
        installationLocator ?? new ChatGptClientInstallationLocator();

    public bool IsRunning()
    {
        var installations = OperatingSystem.IsWindows()
            ? _installationLocator.LocateAll()
            : [];
        var processNames = installations
            .Select(installation => Path.GetFileNameWithoutExtension(installation.ExecutablePath))
            .Append("ChatGPT")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var processName in processNames)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                foreach (var process in processes)
                {
                    if (installations.Count == 0)
                    {
                        // Package discovery can be temporarily unavailable during a
                        // Store update. Never edit config while a ChatGPT-named process
                        // is present and its package identity cannot be enumerated.
                        return true;
                    }

                    var appUserModelId = TryGetApplicationUserModelId(process.Id);
                    if (!string.IsNullOrWhiteSpace(appUserModelId))
                    {
                        if (IsTrustedAppUserModelId(appUserModelId)
                            || installations.Any(installation => string.Equals(
                                installation.AppUserModelId,
                                appUserModelId,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }

                        continue;
                    }

                    var executablePath = TryGetExecutablePath(process);
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        if (installations.Any(installation => string.Equals(
                                installation.ExecutablePath,
                                executablePath,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }

                        continue;
                    }

                    // If Windows refuses both identity checks, fail closed. A false
                    // positive merely asks the user to close a same-named process;
                    // a false negative could corrupt live Desktop configuration.
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    private static bool IsTrustedAppUserModelId(string appUserModelId) =>
        appUserModelId.StartsWith(
            $"{ChatGptClientInstallationLocator.TrustedPackageName}_"
            + $"{ChatGptClientInstallationLocator.TrustedPublisherId}!",
            StringComparison.OrdinalIgnoreCase);

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName is { } path ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetApplicationUserModelId(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        uint length = 0;
        var result = GetApplicationUserModelId(processHandle, ref length, null);
        if (result != ErrorInsufficientBuffer || length is 0 or > 4096)
        {
            return null;
        }

        var buffer = new char[length];
        result = GetApplicationUserModelId(processHandle, ref length, buffer);
        return result == 0
            ? new string(buffer, 0, checked((int)Math.Max(0, length - 1)))
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        SafeProcessHandle processHandle,
        ref uint applicationUserModelIdLength,
        [Out] char[]? applicationUserModelId);
}
