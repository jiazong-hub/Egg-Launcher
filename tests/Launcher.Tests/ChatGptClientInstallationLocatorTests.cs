using Launcher.ChatGPT.Processes;

namespace Launcher.Tests;

public sealed class ChatGptClientInstallationLocatorTests
{
    [Fact]
    public void TryCreateTrustedInstallation_BuildsVersionIndependentAumidFromManifest()
    {
        var result = ChatGptClientInstallationLocator.TryCreateTrustedInstallation(
            "OpenAI.Codex_26.903.8094.0_x64__2p2nqsd0c76g0",
            Path.Combine(Path.GetTempPath(), "OpenAI.Codex.test"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DesktopEntry" },
            Manifest("DesktopEntry", "app/ChatGPT.exe"),
            requireExecutableExists: false);

        Assert.NotNull(result);
        Assert.Equal(new Version(26, 903, 8094, 0), result.Version);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", result.PackageFamilyName);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!DesktopEntry", result.AppUserModelId);
        Assert.EndsWith(Path.Combine("app", "ChatGPT.exe"), result.ExecutablePath);
    }

    [Theory]
    [InlineData("OpenAI.Other_26.903.8094.0_x64__2p2nqsd0c76g0", "OpenAI.Codex", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", "App", "app/ChatGPT.exe")]
    [InlineData("OpenAI.Codex_26.903.8094.0_x64__untrusted", "OpenAI.Codex", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", "App", "app/ChatGPT.exe")]
    [InlineData("OpenAI.Codex_26.903.8094.0_x64__2p2nqsd0c76g0", "OpenAI.Codex", "CN=untrusted", "App", "app/ChatGPT.exe")]
    [InlineData("OpenAI.Codex_26.903.8094.0_x64__2p2nqsd0c76g0", "OpenAI.Codex", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", "Other", "app/ChatGPT.exe")]
    [InlineData("OpenAI.Codex_26.903.8094.0_x64__2p2nqsd0c76g0", "OpenAI.Codex", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", "App", "app/Other.exe")]
    public void TryCreateTrustedInstallation_WhenIdentityIsInvalid_ReturnsNull(
        string packageFullName,
        string identityName,
        string publisher,
        string registeredAppId,
        string executable)
    {
        Assert.Null(ChatGptClientInstallationLocator.TryCreateTrustedInstallation(
            packageFullName,
            Path.Combine(Path.GetTempPath(), "OpenAI.Codex.test"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { registeredAppId },
            Manifest("App", executable, identityName, publisher),
            requireExecutableExists: false));
    }

    [Fact]
    public void TryCreateTrustedInstallation_DoesNotDependOnLocalizedDisplayName()
    {
        var manifest = Manifest("App", "app/ChatGPT.exe")
            .Replace("DisplayName=\"ChatGPT\"", "DisplayName=\"ms-resource:AppName\"", StringComparison.Ordinal);

        var result = ChatGptClientInstallationLocator.TryCreateTrustedInstallation(
            "OpenAI.Codex_26.903.8094.0_x64__2p2nqsd0c76g0",
            Path.Combine(Path.GetTempPath(), "OpenAI.Codex.test"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "App" },
            manifest,
            requireExecutableExists: false);

        Assert.NotNull(result);
    }

    private static string Manifest(
        string appId,
        string executable,
        string identityName = "OpenAI.Codex",
        string publisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Identity Name="{identityName}" ProcessorArchitecture="x64" Version="26.903.8094.0" Publisher="{publisher}" />
          <Applications>
            <Application Id="{appId}" Executable="{executable}" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements DisplayName="ChatGPT" Description="ChatGPT" />
            </Application>
          </Applications>
        </Package>
        """;
}
