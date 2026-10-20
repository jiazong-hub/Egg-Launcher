using System.Reflection;
using System.Text;
using Launcher.Core.Configuration;
using Launcher.Runtime.Monitoring;
using Launcher.Runtime.Router;

namespace Launcher.Tests;

public sealed class DraftAcceptanceTests
{
    [Theory]
    [InlineData("slot print_timing: id 0 | task 1 | draft acceptance = 0.80000 (80 accepted / 100 generated), mean len = 1.8", 0.8, 80, 100)]
    [InlineData("0.10.200 I slot print_timings: id  3 | task 563 | draft acceptance = 0.83154 ( 464 accepted / 558 generated)", 0.83154, 464, 558)]
    [InlineData("[worker model] slot print_timing: id 0 | task 0 | draft acceptance = 0.00000 (0 accepted / 120 generated)", 0, 0, 120)]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 1.00000 (100 accepted / 100 generated)", 1, 100, 100)]
    public void NativeRequestSummary_IsReadWithoutChangingItsValue(string line, double rate, long accepted, long drafted)
    {
        Assert.True(DraftAcceptanceParser.TryParse(line, out var value));
        Assert.NotNull(value);
        Assert.Equal(rate, value.Rate);
        Assert.Equal(accepted, value.AcceptedTokens);
        Assert.Equal(drafted, value.DraftTokens);
        Assert.True(value.IsValid);
    }

    [Theory]
    [InlineData("draft acceptance rate = 0.8 (80 accepted / 100 generated)")]
    [InlineData("statistics mtp: #gen tokens = 100, #acc tokens = 80")]
    [InlineData("accepted 2/2 draft tokens")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 0.8 (80 accepted / 0 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 0.8 (101 accepted / 100 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 0.7 (80 accepted / 100 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = NaN (80 accepted / 100 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 1.1 (80 accepted / 100 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = -0.8 (80 accepted / 100 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 0.8 (9999999999999999999999 accepted / 100 generated)")]
    [InlineData("slot print_timing: id 0 | task 0 | draft acceptance = 0.8 (80 accepted / ")]
    [InlineData("slot print_timing: id 0 | task -1 | draft acceptance = 0.8 (80 accepted / 100 generated)")]
    [InlineData("")]
    public void OtherStatisticsOrMalformedLines_CannotInventOrReplaceARequestResult(string line)
    {
        Assert.False(DraftAcceptanceParser.TryParse(line, out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOutput_IsCapturedBeforeDiagnosticFiltering_AndRetainedUntilNextResult(bool detailed)
    {
        var root = Path.Combine(Path.GetTempPath(), "EggLauncher.Acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var manager = new LlamaRouterProcessManager(new UnusedHealth());
            manager.SetDetailedDiagnosticsEnabled(detailed);
            const string line = "slot print_timing: id 0 | task 0 | draft acceptance = 0.8 (80 accepted / 100 generated)";
            await Pump(manager, root, line + "\nstatistics mtp: #gen tokens = 200\n");
            Assert.Equal(0.8, manager.LatestDraftAcceptance!.Rate);
            var logs = string.Join('\n', Directory.GetFiles(root).Select(File.ReadAllText));
            Assert.Equal(detailed, logs.Contains("draft acceptance"));
            var previous = manager.LatestDraftAcceptance;
            await Pump(manager, root, "normal output\nslot print_timing: id 0 | task 1 | draft acceptance = 0.9 (0 accepted / 0 generated)\n");
            Assert.Same(previous, manager.LatestDraftAcceptance);
            await Pump(manager, root, "slot print_timing: id 0 | task 2 | draft acceptance = 0 (0 accepted / 100 generated)\n");
            Assert.Equal(0, manager.LatestDraftAcceptance!.Rate);
            await manager.StopAsync();
            Assert.Null(manager.LatestDraftAcceptance);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DiagnosticWriteFailure_DoesNotStopNativeStatisticsCollection()
    {
        var root = Path.Combine(Path.GetTempPath(), "EggLauncher.Acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blockedDirectory = Path.Combine(root, "not-a-directory");
            await File.WriteAllTextAsync(blockedDirectory, "fixture");
            await using var manager = new LlamaRouterProcessManager(new UnusedHealth());
            await Pump(manager, blockedDirectory,
                "ordinary line\nslot print_timing: id 0 | task 0 | draft acceptance = 0.5 (50 accepted / 100 generated)\n");
            Assert.Equal(0.5, manager.LatestDraftAcceptance!.Rate);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Pump(LlamaRouterProcessManager manager, string root, string text)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        await (Task)typeof(LlamaRouterProcessManager).GetMethod("PumpLogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, [reader, root, "fixture", "stderr"])!;
    }

    private sealed class UnusedHealth : IRouterHealthClient
    {
        public Task<RouterHealthSnapshot> ProbeAsync(Uri uri, CancellationToken token = default) => throw new NotSupportedException();
        public Task<RouterHealthSnapshot> WaitUntilReadyAsync(Uri uri, TimeSpan timeout, Func<bool>? exited = null, CancellationToken token = default) => throw new NotSupportedException();
    }
}
