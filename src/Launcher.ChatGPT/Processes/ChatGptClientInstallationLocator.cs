using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Launcher.ChatGPT.Processes;

public sealed class ChatGptClientInstallationLocator
{
    internal const string TrustedPackageName = "OpenAI.Codex";
    internal const string TrustedPublisherId = "2p2nqsd0c76g0";
    internal const string TrustedPublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    private const string TrustedExecutableName = "ChatGPT.exe";
    private const string PackagesRegistryPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    [SupportedOSPlatform("windows")]
    public ChatGptClientInstallation? Locate() => LocateAll().FirstOrDefault();

    [SupportedOSPlatform("windows")]
    public IReadOnlyList<ChatGptClientInstallation> LocateAll()
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackagesRegistryPath);
            if (packages is null)
            {
                return [];
            }

            var candidates = new List<ChatGptClientInstallation>();
            foreach (var packageFullName in packages.GetSubKeyNames())
            {
                if (!packageFullName.StartsWith(
                        TrustedPackageName + "_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var package = packages.OpenSubKey(packageFullName);
                    var packageId = package?.GetValue("PackageID") as string;
                    var packageRootFolder = package?.GetValue("PackageRootFolder") as string;
                    if (package is null
                        || string.IsNullOrWhiteSpace(packageRootFolder)
                        || !string.Equals(packageFullName, packageId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var manifestPath = Path.Combine(packageRootFolder, "AppxManifest.xml");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    var registeredAppIds = package.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var manifestXml = File.ReadAllText(manifestPath);
                    var candidate = TryCreateTrustedInstallation(
                        packageFullName,
                        packageRootFolder,
                        registeredAppIds,
                        manifestXml,
                        requireExecutableExists: true);
                    if (candidate is not null)
                    {
                        candidates.Add(candidate);
                    }
                }
                catch (Exception exception) when (exception is SecurityException
                    or UnauthorizedAccessException
                    or IOException
                    or XmlException)
                {
                    // A stale or partially serviced package must not hide another
                    // healthy registered version of the official client.
                }
            }

            return candidates
                .OrderByDescending(candidate => ArchitectureScore(candidate.ProcessorArchitecture))
                .ThenByDescending(candidate => candidate.Version)
                .ToArray();
        }
        catch (Exception exception) when (exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or XmlException)
        {
            return [];
        }
    }

    public static ChatGptClientInstallation? TryCreateTrustedInstallation(
        string packageFullName,
        string packageRootFolder,
        IReadOnlySet<string> registeredAppIds,
        string manifestXml,
        bool requireExecutableExists = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRootFolder);
        ArgumentNullException.ThrowIfNull(registeredAppIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestXml);

        var publisherSeparator = packageFullName.LastIndexOf("__", StringComparison.Ordinal);
        if (publisherSeparator <= 0 || publisherSeparator + 2 >= packageFullName.Length)
        {
            return null;
        }

        var publisherId = packageFullName[(publisherSeparator + 2)..];
        if (!string.Equals(publisherId, TrustedPublisherId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(manifestXml, LoadOptions.None);
            var identity = document.Root?
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Identity");
            var packageName = identity?.Attribute("Name")?.Value;
            var publisher = identity?.Attribute("Publisher")?.Value;
            var architecture = identity?.Attribute("ProcessorArchitecture")?.Value;
            var versionText = identity?.Attribute("Version")?.Value;
            if (!string.Equals(packageName, TrustedPackageName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(publisher, TrustedPublisher, StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(versionText, out var version)
                || !packageFullName.StartsWith(
                    $"{TrustedPackageName}_{version}_",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var rootPath = Path.GetFullPath(packageRootFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var applications = document.Root?
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Applications")?
                .Elements()
                .Where(element => element.Name.LocalName == "Application")
                ?? [];
            foreach (var application in applications)
            {
                var appId = application.Attribute("Id")?.Value;
                var executable = application.Attribute("Executable")?.Value;
                if (string.IsNullOrWhiteSpace(appId)
                    || string.IsNullOrWhiteSpace(executable)
                    || !registeredAppIds.Contains(appId)
                    || !string.Equals(
                        Path.GetFileName(executable.Replace('/', Path.DirectorySeparatorChar)),
                        TrustedExecutableName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var executablePath = Path.GetFullPath(
                    Path.Combine(rootPath, executable.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsDirectOrNestedChild(executablePath, rootPath)
                    || requireExecutableExists && !File.Exists(executablePath))
                {
                    continue;
                }

                return new ChatGptClientInstallation(
                    packageFullName,
                    $"{TrustedPackageName}_{TrustedPublisherId}",
                    appId,
                    version,
                    rootPath,
                    executablePath,
                    publisherId,
                    architecture ?? string.Empty);
            }

            return null;
        }
        catch (Exception exception) when (exception is XmlException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsDirectOrNestedChild(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static int ArchitectureScore(string architecture)
    {
        var current = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => string.Empty,
        };
        return string.Equals(architecture, current, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
}
