using Launcher.Core.Configuration;
using Launcher.Core.State;

namespace Launcher.Tests;

public sealed class ModeSwitchGuardTests
{
    [Fact]
    public void EnsureCanSwitch_WhenClientIsRunning_RejectsProviderChange()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModeSwitchGuard.EnsureCanSwitch(ProviderMode.OpenAI, ProviderMode.Local, isChatGptDesktopRunning: true));

        Assert.Contains("关闭 ChatGPT Desktop", exception.Message);
    }

    [Fact]
    public void EnsureCanSwitch_WhenModeIsUnchanged_AllowsRunningClient()
    {
        ModeSwitchGuard.EnsureCanSwitch(
            ProviderMode.Local,
            ProviderMode.Local,
            isChatGptDesktopRunning: true);
    }

    [Fact]
    public void EnsureCanChangeRuntime_WhenLocalAndPathChanges_RejectsChange()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModeSwitchGuard.EnsureCanChangeRuntime(ProviderMode.Local, @"D:\llama-a", @"D:\llama-b"));

        Assert.Contains("OpenAI 模式", exception.Message);
    }

    [Fact]
    public void EnsureCanChangeRuntime_WhenLocalAndPathIsSame_AllowsReadOnlyProbe()
    {
        ModeSwitchGuard.EnsureCanChangeRuntime(ProviderMode.Local, @"D:\llama", @"d:\llama\");
    }
}
