namespace Launcher.ChatGPT.Processes;

public sealed record ChatGptClientInstallation(
    string PackageFullName,
    string PackageFamilyName,
    string AppId,
    Version Version,
    string PackageRootFolder,
    string ExecutablePath,
    string PublisherId,
    string ProcessorArchitecture)
{
    public string AppUserModelId => $"{PackageFamilyName}!{AppId}";
}
