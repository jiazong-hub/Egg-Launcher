using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using Launcher.Core.Security;
using Launcher.Runtime.Processes;

namespace Launcher.Runtime.Router;

public sealed class LlamaRouterProcessManager(IRouterHealthClient healthClient) : ILlamaRouterProcessManager
{
    private const long MaximumNativeLogBytes = 32L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private SafeFileHandle? _jobHandle;
    private Task? _standardOutputPump;
    private Task? _standardErrorPump;
    private FileStream? _runtimeLease;

    public int? OwnedProcessId => _process is { HasExited: false } process ? process.Id : null;

    public async Task<LlamaRouterProcessInfo> StartAsync(
        LlamaRouterStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                Directory.CreateDirectory(request.LogDirectory);
                RotateRouterLogs(request.LogDirectory);
                var timestamp = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                var outputLogPath = Path.Combine(request.LogDirectory, $"router-{timestamp}.stdout.log");
                var errorLogPath = Path.Combine(request.LogDirectory, $"router-{timestamp}.stderr.log");
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
                _standardOutputPump = PumpLogAsync(process.StandardOutput, outputLogPath);
                _standardErrorPump = PumpLogAsync(process.StandardError, errorLogPath);
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
                        health);
                }
                catch (Exception exception)
                {
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

            if (_standardOutputPump is not null)
            {
                await _standardOutputPump.ConfigureAwait(false);
            }

            if (_standardErrorPump is not null)
            {
                await _standardErrorPump.ConfigureAwait(false);
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

    private static async Task PumpLogAsync(StreamReader reader, string path)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };

            var writtenBytes = stream.Length;
            var truncated = false;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (truncated)
                {
                    continue;
                }

                var nextBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (writtenBytes + nextBytes > MaximumNativeLogBytes)
                {
                    await writer.WriteLineAsync("[Launcher] Native llama log truncated at 32 MiB.").ConfigureAwait(false);
                    truncated = true;
                    continue;
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                writtenBytes += nextBytes;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep draining redirected output so a log failure cannot block or crash llama supervision.
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
    }

    private static void PreparePrivateLogFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
        }

        PrivateFilePermissions.HardenFile(path);
    }

    private static void RotateRouterLogs(string logDirectory)
    {
        const int maximumLogFiles = 20;
        var files = Directory.EnumerateFiles(logDirectory, "router-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(maximumLogFiles)
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
