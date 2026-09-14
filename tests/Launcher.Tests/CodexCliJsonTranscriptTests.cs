using Launcher.ChatGPT.Processes;

namespace Launcher.Tests;

public sealed class CodexCliJsonTranscriptTests
{
    [Fact]
    public void FindThreadId_ReturnsFirstValidThreadId()
    {
        var expected = Guid.NewGuid();
        var transcript = $$"""
            not-json
            {"type":"thread.started","thread_id":"not-a-guid"}
            {"type":"thread.started","thread_id":"{{expected}}"}
            {"type":"thread.started","thread_id":"{{Guid.NewGuid()}}"}
            """;

        Assert.Equal(expected.ToString(), CodexCliJsonTranscript.FindThreadId(transcript));
    }

    [Fact]
    public void FindThreadId_ReturnsNullWhenMissing()
    {
        Assert.Null(CodexCliJsonTranscript.FindThreadId("{\"type\":\"turn.started\"}"));
    }

    [Fact]
    public void CountSuccessfulCommandExecutions_CountsOnlyCompletedZeroExitCommands()
    {
        const string transcript = """
            {"type":"thread.started","thread_id":"one"}
            {"type":"item.completed","item":{"type":"command_execution","exit_code":0}}
            {"type":"item.completed","item":{"type":"command_execution","exit_code":1}}
            {"type":"item.completed","item":{"type":"agent_message","text":"done"}}
            """;

        Assert.Equal(1, CodexCliJsonTranscript.CountSuccessfulCommandExecutions(transcript));
    }

    [Fact]
    public void CountSuccessfulCommandExecutions_ToleratesNonJsonDiagnostics()
    {
        const string transcript = "warning\n{\"type\":\"turn.completed\"}\n";

        Assert.Equal(0, CodexCliJsonTranscript.CountSuccessfulCommandExecutions(transcript));
    }

    [Fact]
    public void Analyze_SeparatesAttemptsSuccessesAndPolicyBlocks()
    {
        const string transcript = """
            {"type":"item.completed","item":{"type":"command_execution","exit_code":0,"aggregated_output":"ok"}}
            {"type":"item.completed","item":{"type":"command_execution","exit_code":1,"aggregated_output":"blocked by policy"}}
            {"type":"item.completed","item":{"type":"command_execution","exit_code":2,"error":{"message":"failed"}}}
            {"type":"item.completed","item":{"type":"agent_message","text":"blocked by policy"}}
            """;

        var result = CodexCliJsonTranscript.Analyze(transcript);

        Assert.Equal(3, result.CommandExecutionAttempts);
        Assert.Equal(1, result.SuccessfulCommandExecutions);
        Assert.Equal(1, result.PolicyBlockedCommandExecutions);
    }
}
