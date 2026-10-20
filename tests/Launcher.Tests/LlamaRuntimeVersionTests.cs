using Launcher.Runtime.Detection;
using Launcher.Runtime.Processes;

namespace Launcher.Tests;

public sealed class LlamaRuntimeVersionTests
{
    [Theory]
    [InlineData("version: 12345 (abcdef12)", "12345-abcdef12")]
    [InlineData("ggml_cuda_init: found 1 CUDA devices\nversion: b12345 (abcdef12)\nbuilt with MSVC", "b12345-abcdef12")]
    [InlineData("llama.cpp version: v1.2.3", "v1.2.3")]
    [InlineData("build: 12345 (abcdef12)", "12345-abcdef12")]
    [InlineData("version: abcdefa", "abcdefa")]
    [InlineData("version: abc12345", "abc12345")]
    [InlineData("version: 12345-custom.2", "12345-custom.2")]
    [InlineData("ggml_cuda_init: CUDA version: 12.8\nserver listening on port 8080", null)]
    [InlineData("version: unknown", null)]
    [InlineData("error: version: 12345", null)]
    [InlineData("version: https://example.com/12345", null)]
    [InlineData("", null)]
    public void Parser_RecognizesOnlyVersionOrBuildLines(string output, string? expected) =>
        Assert.Equal(expected, LlamaRuntimeVersionReader.ParseOutput(output));

    [Fact]
    public async Task Reader_UsesBothStreams_OnlyRequestsVersion_AndCachesUnchangedExecutable()
    {
        var path = CreateExecutable();
        try
        {
            var runner = new StubRunner(new(0, "ggml_cuda_init: CUDA version: 12.8", "version: 12345 (abcdef12)", false));
            var reader = new LlamaRuntimeVersionReader(runner);
            var first = await reader.ReadAsync(path);
            var second = await reader.ReadAsync(path);
            Assert.Equal(new("12345-abcdef12", "version_command", "success"), first);
            Assert.Equal(first, second);
            Assert.Equal(1, runner.Calls);
            Assert.Equal(Path.GetFullPath(path), runner.Executable);
            Assert.Equal(["--version"], runner.Arguments);
            Assert.Equal(TimeSpan.FromSeconds(3), runner.Timeout);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_RefreshesAfterExecutableReplacement_AndPathChange()
    {
        var path = CreateExecutable();
        var secondPath = CreateExecutable();
        try
        {
            var runner = new StubRunner(new(0, "version: 12345", "", false));
            var reader = new LlamaRuntimeVersionReader(runner);
            await reader.ReadAsync(path);
            await File.AppendAllTextAsync(path, "replacement");
            runner.Result = new(0, "version: 23456", "", false);
            Assert.Equal("23456", (await reader.ReadAsync(path)).Version);
            await reader.ReadAsync(secondPath);
            Assert.Equal(3, runner.Calls);
        }
        finally { File.Delete(path); File.Delete(secondPath); }
    }

    [Theory]
    [InlineData(true, 0, "timed_out")]
    [InlineData(false, 1, "nonzero_exit")]
    [InlineData(false, 0, "unrecognized_output")]
    public async Task Reader_QueryFailureDoesNotAcceptMisleadingPartialOutput_AndIsCached(bool timedOut, int exitCode, string reason)
    {
        var path = CreateExecutable();
        try
        {
            var runner = new StubRunner(new(exitCode, exitCode == 0 && !timedOut ? "CUDA version: 12.8" : "version: 12345", "", timedOut));
            var reader = new LlamaRuntimeVersionReader(runner);
            var result = await reader.ReadAsync(path);
            Assert.Equal(new("unknown", "unavailable", reason), result);
            await reader.ReadAsync(path);
            Assert.Equal(1, runner.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_ProcessFailureFallsBackToRealProductVersion()
    {
        var path = typeof(LlamaRuntimeVersionReader).Assembly.Location;
        var runner = new StubRunner(new(1, "", "", false)) { Failure = new IOException("must not be logged") };
        var result = await new LlamaRuntimeVersionReader(runner).ReadAsync(path);
        Assert.StartsWith("0.9.9", result.Version);
        Assert.Equal("product_version", result.Source);
        Assert.Equal("query_failed", result.QueryResult);
    }

    [Fact]
    public async Task Reader_MissingExecutableIsNotQueried_AndCanAppearLater()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var runner = new StubRunner(new(0, "version: 12345", "", false));
            var reader = new LlamaRuntimeVersionReader(runner);
            Assert.Equal("executable_unavailable", (await reader.ReadAsync(path)).QueryResult);
            Assert.Equal(0, runner.Calls);
            await File.WriteAllTextAsync(path, "fixture");
            Assert.Equal("12345", (await reader.ReadAsync(path)).Version);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reader_PropagatesCallerCancellation_WithoutCachingItAsFailure()
    {
        var path = CreateExecutable();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var runner = new StubRunner(new(0, "version: 12345", "", false))
            {
                BeforeReturn = cancellation.Cancel,
                Failure = new OperationCanceledException(cancellation.Token),
            };
            var reader = new LlamaRuntimeVersionReader(runner);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(path, cancellation.Token));
            runner.BeforeReturn = null;
            runner.Failure = null;
            Assert.Equal("12345", (await reader.ReadAsync(path)).Version);
            Assert.Equal(2, runner.Calls);
        }
        finally { File.Delete(path); }
    }

    private static string CreateExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "fixture");
        return path;
    }

    private sealed class StubRunner(ProcessRunResult result) : IProcessRunner
    {
        public ProcessRunResult Result { get; set; } = result;
        public Exception? Failure { get; set; }
        public Action? BeforeReturn { get; set; }
        public int Calls { get; private set; }
        public string? Executable { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public TimeSpan Timeout { get; private set; }

        public Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            Executable = executablePath;
            Arguments = arguments;
            Timeout = timeout;
            BeforeReturn?.Invoke();
            return Failure is null ? Task.FromResult(Result) : Task.FromException<ProcessRunResult>(Failure);
        }
    }
}
