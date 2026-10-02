using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using Launcher.Core.Diagnostics;
using Launcher.Core.Security;
using Launcher.Runtime.Processes;

namespace Launcher.Runtime.Router;

public sealed class LlamaRouterProcessManager(
    IRouterHealthClient healthClient,
    Func<DiagnosticEvent, Task>? diagnosticEventWriter = null,
    Action<string?>? diagnosticRunIdChanged = null) : ILlamaRouterProcessManager
{
    private const long MaximumNativeLogBytes = 16L * 1024 * 1024;
    private const int MaximumRouterLogFiles = 10;
    private static readonly string[] ConciseNativeLogMarkers =
    [
        "error", "warn", "failed", "failure", "fatal", "assert", "abort", "out of memory",
        "allocation", "device lost", "cuda", "vulkan", "driver", "backend", "build",
        "model loaded", "loading model", "listening on", "server is listening", "shutting down",
        "worker exited",
    ];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _nativeObservationGate = new();
    private readonly Dictionary<string, NativeObservationBurst> _nativeObservationBursts = new(StringComparer.Ordinal);
    private Process? _process;
    private SafeFileHandle? _jobHandle;
    private Task? _standardOutputPump;
    private Task? _standardErrorPump;
    private FileStream? _runtimeLease;
    private string? _activeLogDirectory;
    private int _detailedDiagnosticsEnabled;
    private int? _lastExitCode;
    private Action<string>? _contextShiftDisabledObserver;
    private int _contextShiftObserved;
    public bool ContextShiftObserved => Volatile.Read(ref _contextShiftObserved) == 1;
    public string? ContextShiftDisabledReason { get; private set; }
    private string? _lastDiagnosticRunId;

    public void SetDetailedDiagnosticsEnabled(bool enabled) =>
        Volatile.Write(ref _detailedDiagnosticsEnabled, enabled ? 1 : 0);

    public int? LastExitCode => _lastExitCode;

    public string? LastDiagnosticRunId => Volatile.Read(ref _lastDiagnosticRunId);

    public int? OwnedProcessId => _process is { HasExited: false } process ? process.Id : null;

    public async Task<LlamaRouterProcessInfo> StartAsync(
        LlamaRouterStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _lastDiagnosticRunId, null);
            diagnosticRunIdChanged?.Invoke(null);
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("当前 Manager 已拥有一个运行中的 Router。");
            }

            if (_process is not null)
            {
                await StopOwnedProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var executablePath = Path.GetFullPath(request.ExecutablePath);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("找不到 llama-server.exe。", executablePath);
            }

            var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException($"Router 工作目录不存在：{workingDirectory}");
            }

            var runtimeLease = AcquireRuntimeLease(workingDirectory);
            try
            {
                _lastExitCode = null;
                _contextShiftDisabledObserver = request.ContextShiftDisabledObserver;
                ContextShiftDisabledReason = null;
                Volatile.Write(ref _contextShiftObserved, 0);
                Directory.CreateDirectory(request.LogDirectory);
                _activeLogDirectory = Path.GetFullPath(request.LogDirectory);
                RotateRouterLogs(request.LogDirectory);
                lock (_nativeObservationGate)
                {
                    _nativeObservationBursts.Clear();
                }
                var runId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                Volatile.Write(ref _lastDiagnosticRunId, runId);
                diagnosticRunIdChanged?.Invoke(runId);
                var initialMode = CurrentDiagnosticMode();
                var outputLogPath = NativeLogPath(request.LogDirectory, runId, initialMode, "stdout", 0);
                var errorLogPath = NativeLogPath(request.LogDirectory, runId, initialMode, "stderr", 0);
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                ChildProcessEnvironment.RemoveSensitiveVariables(startInfo);

                if (!string.IsNullOrWhiteSpace(request.ModelCacheDirectory))
                {
                    var cacheDirectory = Path.GetFullPath(request.ModelCacheDirectory);
                    Directory.CreateDirectory(cacheDirectory);
                    startInfo.Environment["LLAMA_CACHE"] = cacheDirectory;
                }

                foreach (var argument in LlamaRouterCommandBuilder.BuildArguments(request.Options))
                {
                    startInfo.ArgumentList.Add(argument);
                }

                PreparePrivateLogFile(outputLogPath);
                PreparePrivateLogFile(errorLogPath);
                RotateRouterLogs(request.LogDirectory, outputLogPath, errorLogPath);
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("无法启动 llama.cpp Router。");
                }

                SafeFileHandle? jobHandle = null;
                try
                {
                    jobHandle = CreateKillOnCloseJob();
                    if (!AssignProcessToJobObject(jobHandle, process.Handle))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将 llama-server 加入清理作业对象。");
                    }
                }
                catch
                {
                    jobHandle?.Dispose();
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    process.Dispose();
                    throw;
                }

                _runtimeLease = runtimeLease;
                runtimeLease = null;
                _process = process;
                _jobHandle = jobHandle;
                _standardOutputPump = PumpLogAsync(process.StandardOutput, request.LogDirectory, runId, "stdout");
                _standardErrorPump = PumpLogAsync(process.StandardError, request.LogDirectory, runId, "stderr");
                await TryWriteDiagnosticEventAsync(new DiagnosticEvent("context_shift_requested", "info",
                    "Explicit context shifting configuration for the selected model.",
                    new Dictionary<string, object?>
                    {
                        ["requested"] = request.ContextShiftRequested,
                        ["parameter"] = request.ContextShiftRequested is true ? "--context-shift"
                            : request.ContextShiftRequested is false ? "--no-context-shift" : null,
                        ["routerRunId"] = runId
                    })).ConfigureAwait(false);
                var baseUri = new Uri($"http://{request.Options.Host}:{request.Options.Port}/", UriKind.Absolute);

                try
                {
                    var health = await healthClient.WaitUntilReadyAsync(
                        baseUri,
                        request.StartupTimeout,
                        () => process.HasExited,
                        cancellationToken).ConfigureAwait(false);

                    return new LlamaRouterProcessInfo(
                        process.Id,
                        process.StartTime.ToUniversalTime(),
                        baseUri,
                        outputLogPath,
                        errorLogPath,
                        health)
                    {
                        DiagnosticRunId = runId,
                    };
                }
                catch (Exception exception)
                {
                    if (exception is RouterReadinessException readinessException)
                    {
                        await TryWriteDiagnosticEventAsync(new DiagnosticEvent(
                            "router_readiness_failed",
                            "error",
                            "Router health endpoints did not become ready before startup ended.",
                            new Dictionary<string, object?>
                            {
                                ["startupTimeoutMs"] = readinessException.StartupTimeout.TotalMilliseconds,
                                ["processExited"] = readinessException.ProcessExited,
                                ["healthStatusCode"] = readinessException.LastSnapshot?.HealthStatusCode,
                                ["modelsStatusCode"] = readinessException.LastSnapshot?.ModelsStatusCode,
                                ["healthy"] = readinessException.LastSnapshot?.IsHealthy,
                                ["diagnostic"] = DiagnosticSanitizer.SanitizeText(
                                    readinessException.LastSnapshot?.Diagnostic),
                                ["routerRunId"] = runId,
                            })).ConfigureAwait(false);
                    }

                    await StopOwnedProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    if (IsPortBindingFailure(errorLogPath) || IsPortBindingFailure(outputLogPath))
                    {
                        throw new LlamaRouterPortBindingException(request.Options.Port, exception);
                    }

                    throw;
                }
            }
            finally
            {
                runtimeLease?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopOwnedProcessCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StopOwnedProcessCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (process.HasExited)
            {
                _lastExitCode = process.ExitCode;
            }

            if (_standardOutputPump is not null)
            {
                await _standardOutputPump.ConfigureAwait(false);
            }

            if (_standardErrorPump is not null)
            {
                await _standardErrorPump.ConfigureAwait(false);
            }

            if (_activeLogDirectory is not null)
            {
                RotateRouterLogs(_activeLogDirectory);
            }
        }
        finally
        {
            process.Dispose();
            _jobHandle?.Dispose();
            _process = null;
            _jobHandle = null;
            _standardOutputPump = null;
            _standardErrorPump = null;
            _runtimeLease?.Dispose();
            _runtimeLease = null;
        }
    }

    private static FileStream AcquireRuntimeLease(string workingDirectory)
    {
        var scriptsDirectory = Path.Combine(workingDirectory, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        var path = Path.Combine(scriptsDirectory, ".launcher-runtime.lock");
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "当前 llama.cpp Runtime 已由另一个 Launcher Router 使用，不能并发启动第二个服务。",
                exception);
        }
    }

    private async Task PumpLogAsync(StreamReader reader, string logDirectory, string runId, string streamName)
    {
        StreamWriter? writer = null;
        FileStream? stream = null;
        string? activeMode = null;
        var segment = 0;
        var writtenBytes = 0L;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var requestedMode = CurrentDiagnosticMode();
                if (!string.Equals(activeMode, requestedMode, StringComparison.Ordinal))
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = null;
                        stream = null;
                    }

                    activeMode = requestedMode;
                    segment = FindAvailableNativeLogSegment(logDirectory, runId, activeMode, streamName);
                    (stream, writer, writtenBytes) = OpenNativeLog(
                        logDirectory,
                        runId,
                        activeMode,
                        streamName,
                        segment);
                }

                var logMode = activeMode ?? requestedMode;
                if (IsContextShiftDisabledLine(line))
                {
                    ContextShiftDisabledReason = DiagnosticSanitizer.SanitizeText(line, 500);
                    try { _contextShiftDisabledObserver?.Invoke(ContextShiftDisabledReason); }
                    catch { /* Capability persistence must never stop draining output. */ }
                    await TryWriteDiagnosticEventAsync(new DiagnosticEvent("context_shift_disabled", "warning",
                        ContextShiftDisabledReason, new Dictionary<string, object?>
                        { ["effectiveEnabled"] = false, ["routerRunId"] = runId })).ConfigureAwait(false);
                }
                if (line.Contains("slot context shift,", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("context shift:", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _contextShiftObserved, 1);
                    await TryWriteDiagnosticEventAsync(new DiagnosticEvent("context_shift_performed", "info",
                        DiagnosticSanitizer.SanitizeText(line, 500), new Dictionary<string, object?>
                        { ["effectiveEnabled"] = true, ["routerRunId"] = runId })).ConfigureAwait(false);
                }
                if (logMode == "concise" && !IsImportantNativeLine(line))
                {
                    continue;
                }

                var safeLine = DiagnosticSanitizer.SanitizeText(line, 16_000);
                await TryWriteNativeObservationAsync(runId, streamName, safeLine).ConfigureAwait(false);
                var timestampedLine = $"[{DateTimeOffset.UtcNow:O}] {safeLine}";
                var nextBytes = Encoding.UTF8.GetByteCount(timestampedLine) + Environment.NewLine.Length;
                if (writtenBytes + nextBytes > MaximumNativeLogBytes)
                {
                    await writer!.WriteLineAsync(
                        $"[{DateTimeOffset.UtcNow:O}] [Launcher] Log segment reached 16 MiB; output continues in the next segment.")
                        .ConfigureAwait(false);
                    await writer.DisposeAsync().ConfigureAwait(false);
                    writer = null;
                    stream = null;
                    segment++;
                    (stream, writer, writtenBytes) = OpenNativeLog(
                        logDirectory,
                        runId,
                        logMode,
                        streamName,
                        segment);
                    RotateRouterLogs(logDirectory);
                }

                await writer!.WriteLineAsync(timestampedLine).ConfigureAwait(false);
                writtenBytes += nextBytes;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await TryWriteDiagnosticEventAsync(new DiagnosticEvent(
                "router_native_log_write_failed",
                "warning",
                "llama.cpp output logging failed; process output was drained to keep the Router responsive.",
                new Dictionary<string, object?>
                {
                    ["routerRunId"] = runId,
                    ["stream"] = streamName,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["hResult"] = $"0x{exception.HResult:X8}",
                    ["message"] = DiagnosticSanitizer.SanitizeText(exception.Message),
                })).ConfigureAwait(false);
            // Keep draining redirected output so a log failure cannot block or crash llama supervision.
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                stream?.Dispose();
            }
        }
    }

    private string CurrentDiagnosticMode() => Volatile.Read(ref _detailedDiagnosticsEnabled) == 1
        ? "full"
        : "concise";

    private async Task TryWriteNativeObservationAsync(string runId, string streamName, string line)
    {
        if (diagnosticEventWriter is null)
        {
            return;
        }

        var isError = ContainsAny(line, "error", "failed", "fatal", "device lost", "out of memory");
        var isLoading = ContainsAny(line, "loading model", "load model", "model loaded", "model is loaded");
        var backend = new[] { "cuda", "vulkan", "sycl", "metal", "kompute", "opencl", "rpc", "cpu" }
            .FirstOrDefault(candidate => line.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        var isBackendObservation = backend is not null
            && ContainsAny(line, "backend", "device", "initializ", "loaded", "using", "build");
        if (!isError && !isLoading && !isBackendObservation)
        {
            return;
        }

        var marker = isError ? "error" : isLoading ? "model_load" : "backend";
        var observationKey = string.Join('|', runId, streamName, marker, backend ?? "unknown");
        NativeObservationBurst burst;
        lock (_nativeObservationGate)
        {
            if (_nativeObservationBursts.TryGetValue(observationKey, out var existing))
            {
                burst = existing with
                {
                    RepeatCount = existing.RepeatCount + 1,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                burst = new NativeObservationBurst(Guid.NewGuid().ToString("N"), 1, now, now);
            }

            _nativeObservationBursts[observationKey] = burst;
        }

        if (burst.RepeatCount is not 1 and not 2 and not 5 and not 10
            && burst.RepeatCount % 50 != 0)
        {
            return;
        }

        await TryWriteDiagnosticEventAsync(new DiagnosticEvent(
            "router_native_log_observation",
            isError ? "warning" : "info",
            "A categorized llama.cpp runtime log line was observed.",
            new Dictionary<string, object?>
            {
                ["routerRunId"] = runId,
                ["stream"] = streamName,
                ["marker"] = marker,
                ["backend"] = backend,
                ["nativeLineLength"] = line.Length,
                ["observationBurstId"] = burst.BurstId,
                ["observationRepeatCount"] = burst.RepeatCount,
                ["observationFirstSeenUtc"] = burst.FirstSeenUtc,
                ["observationLastSeenUtc"] = burst.LastSeenUtc,
            })).ConfigureAwait(false);
    }

    private async Task TryWriteDiagnosticEventAsync(DiagnosticEvent diagnosticEvent)
    {
        if (diagnosticEventWriter is null)
        {
            return;
        }

        try
        {
            await diagnosticEventWriter(diagnosticEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                Console.Error.WriteLine(
                    "Router diagnostic event write failed: "
                    + DiagnosticSanitizer.SanitizeText(exception.GetType().Name + ": " + exception.Message, 500));
            }
            catch
            {
                // Logging must not stop draining llama.cpp's redirected output.
            }
        }
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string NativeLogPath(
        string logDirectory,
        string runId,
        string mode,
        string streamName,
        int segment) =>
        Path.Combine(
            logDirectory,
            segment == 0
                ? $"router-{runId}.{mode}.{streamName}.log"
                : $"router-{runId}.{mode}.{streamName}.part{segment:000}.log");

    private static int FindAvailableNativeLogSegment(
        string logDirectory,
        string runId,
        string mode,
        string streamName)
    {
        var segment = 0;
        while (true)
        {
            var path = NativeLogPath(logDirectory, runId, mode, streamName, segment);
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumNativeLogBytes)
            {
                return segment;
            }

            segment++;
        }
    }

    private static (FileStream Stream, StreamWriter Writer, long WrittenBytes) OpenNativeLog(
        string logDirectory,
        string runId,
        string mode,
        string streamName,
        int segment)
    {
        var path = NativeLogPath(logDirectory, runId, mode, streamName, segment);
        var exists = File.Exists(path);
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        if (!exists)
        {
            PrivateFilePermissions.HardenFile(path);
        }

        return (stream, new StreamWriter(stream) { AutoFlush = true }, stream.Length);
    }

    private static bool IsImportantNativeLine(string line)
        => IsContextShiftDisabledLine(line)
            || line.Contains("context shift", StringComparison.OrdinalIgnoreCase)
            || ConciseNativeLogMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static bool IsContextShiftDisabledLine(string line) =>
        ContainsAny(line, "KV cache shifting", "ctx_shift", "context shift", "context-shift")
        && ContainsAny(line, "not supported", "disabling", "will be disabled");

    private static void PreparePrivateLogFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
        }

        PrivateFilePermissions.HardenFile(path);
    }

    private static void RotateRouterLogs(string logDirectory, params string[] protectedPaths)
    {
        try
        {
            var protectedSet = protectedPaths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingSlots = Math.Max(0, MaximumRouterLogFiles - protectedSet.Count);
            var files = Directory.EnumerateFiles(logDirectory, "router-*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => !protectedSet.Contains(Path.GetFullPath(file.FullName)))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(remainingSlots)
                .ToArray();
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // A diagnostic file must never prevent Local routing from starting.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve startup even if an administrator owns an older log.
                }
            }
        }
        catch (IOException)
        {
            // Log retention is best effort and must never stop Router supervision.
        }
        catch (UnauthorizedAccessException)
        {
            // Log retention is best effort and must never stop Router supervision.
        }
    }

    private sealed record NativeObservationBurst(
        string BurstId,
        int RepeatCount,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc);

    private static bool IsPortBindingFailure(string errorLogPath)
    {
        try
        {
            if (!File.Exists(errorLogPath))
            {
                return false;
            }

            const int maximumTailBytes = 64 * 1024;
            using var stream = new FileStream(
                errorLogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > maximumTailBytes)
            {
                stream.Seek(-maximumTailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var tail = reader.ReadToEnd();
            return tail.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("WSAEADDRINUSE", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("failed to bind", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("bind failed", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("cannot bind", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("error binding", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("10048", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("llama-server 作业对象仅支持 Windows。");
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 llama-server 清理作业对象。");
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, pointer, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置 llama-server 清理作业对象。");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
