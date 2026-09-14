using Launcher.Core.Agent;

namespace Launcher.Tests;

public sealed class AgentControlPipeTests
{
    [Fact]
    public async Task ShutdownRequest_IsAcceptedByCurrentUserPipe()
    {
        var pipeName = $"ChatGPTLocalLauncher.Tests.{Guid.NewGuid():N}";
        var shutdownRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerTask = AgentControlPipe.ListenForShutdownAsync(
            () => shutdownRequested.TrySetResult(),
            pipeName: pipeName);

        var result = await AgentControlPipe.RequestShutdownAsync(
            TimeSpan.FromSeconds(5),
            pipeName: pipeName);

        Assert.True(result.Succeeded, result.Diagnostic);
        await shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await listenerTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ShutdownRequest_WhenNoAgentListens_ReturnsTimeoutWithoutThrowing()
    {
        var result = await AgentControlPipe.RequestShutdownAsync(
            TimeSpan.FromMilliseconds(50),
            pipeName: $"ChatGPTLocalLauncher.Tests.{Guid.NewGuid():N}");

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.Diagnostic, StringComparison.Ordinal);
    }
}
