using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Launcher.Core.Security;

public static class PrivateFilePermissions
{
    public static void HardenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(security, CurrentUserSid());
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(fullPath).SetAccessControl(security);
    }

    public static void HardenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFileRule(security, CurrentUserSid());
        AddFileRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFileRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new FileInfo(fullPath).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("无法确定当前 Windows 用户 SID。");

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    [SupportedOSPlatform("windows")]
    private static void AddFileRule(FileSecurity security, SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
}
