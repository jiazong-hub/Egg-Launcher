using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Launcher.Core.Startup;

public static class StartupExecutableTrustValidator
{
    public static void EnsureSafeForStartup(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到要注册的启动程序。", fullPath);
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureWindowsAclIsNotBroadlyWritable(fullPath);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsAclIsNotBroadlyWritable(string fullPath)
    {
        var broadSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
        };
        var dangerousRights = FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;
        var fileSecurity = new FileInfo(fullPath).GetAccessControl(AccessControlSections.Access);
        EnsureRulesAreNotBroadlyWritable(fileSecurity, broadSids, dangerousRights);

        var directoryPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("启动程序必须位于一个目录中。");
        var directorySecurity = new DirectoryInfo(directoryPath).GetAccessControl(AccessControlSections.Access);
        var dangerousDirectoryRights = dangerousRights
            | FileSystemRights.CreateFiles
            | FileSystemRights.CreateDirectories;
        EnsureRulesAreNotBroadlyWritable(directorySecurity, broadSids, dangerousDirectoryRights);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureRulesAreNotBroadlyWritable(
        FileSystemSecurity security,
        IReadOnlySet<string> broadSids,
        FileSystemRights dangerousRights)
    {
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            var identityName = TryTranslateIdentity(rule.IdentityReference);
            if (rule.AccessControlType == AccessControlType.Allow
                && (broadSids.Contains(rule.IdentityReference.Value)
                    || identityName.EndsWith("\\CodexSandboxUsers", StringComparison.OrdinalIgnoreCase)
                    || identityName.Equals("CodexSandboxUsers", StringComparison.OrdinalIgnoreCase))
                && (rule.FileSystemRights & dangerousRights) != 0)
            {
                throw new InvalidOperationException(
                    "启动程序所在位置允许普通用户修改，拒绝注册登录启动。请将程序放入受保护的安装目录。");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string TryTranslateIdentity(IdentityReference identity)
    {
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return identity.Value;
        }
    }
}
