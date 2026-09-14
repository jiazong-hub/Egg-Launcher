using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Launcher.Runtime.Monitoring;

public sealed partial class WindowsPerformanceSampler : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _processBaselines = [];
    private readonly PdhGpuSampler? _gpuSampler;
    private ulong? _lastSystemIdle;
    private ulong? _lastSystemKernel;
    private ulong? _lastSystemUser;
    private bool _disposed;

    public WindowsPerformanceSampler()
    {
        if (OperatingSystem.IsWindows())
        {
            _gpuSampler = PdhGpuSampler.TryCreate();
        }
    }

    public WindowsPerformanceSnapshot Sample(string? runtimeRoot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var memory = ReadPhysicalMemory();
            var cpu = ReadSystemCpu();
            var processes = ReadLlamaProcesses(runtimeRoot);
            var llamaCpu = ReadLlamaCpu(processes);
            var llamaWorkingSet = processes.Sum(process => SafeWorkingSet(process));
            var pids = processes.Select(process => process.Id).ToHashSet();
            var gpu = _gpuSampler?.Sample(pids);
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return new WindowsPerformanceSnapshot(
                cpu,
                llamaCpu,
                memory.TotalBytes,
                Math.Max(0, memory.TotalBytes - memory.AvailableBytes),
                llamaWorkingSet,
                gpu?.Overall,
                gpu?.Compute,
                gpu?.ForLlama,
                gpu?.DedicatedBytes,
                gpu?.LlamaDedicatedBytes,
                pids.Count,
                _gpuSampler is null ? "Windows/驱动未提供 GPU Performance Counters。" : gpu?.Diagnostic);
        }
    }

    public static (long TotalBytes, long AvailableBytes) ReadPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0);
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((long)Math.Min(status.TotalPhysical, long.MaxValue), (long)Math.Min(status.AvailablePhysical, long.MaxValue))
            : (0, 0);
    }

    private double? ReadSystemCpu()
    {
        if (!OperatingSystem.IsWindows()
            || !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var currentIdle = ToUInt64(idle);
        var currentKernel = ToUInt64(kernel);
        var currentUser = ToUInt64(user);
        if (_lastSystemIdle is null)
        {
            _lastSystemIdle = currentIdle;
            _lastSystemKernel = currentKernel;
            _lastSystemUser = currentUser;
            return null;
        }

        var idleDelta = currentIdle - _lastSystemIdle.Value;
        var totalDelta = currentKernel - _lastSystemKernel!.Value + currentUser - _lastSystemUser!.Value;
        _lastSystemIdle = currentIdle;
        _lastSystemKernel = currentKernel;
        _lastSystemUser = currentUser;
        return totalDelta == 0 ? null : Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static Process[] ReadLlamaProcesses(string? runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return [];
        }

        var root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
            }
        }

        return matches.ToArray();
    }

    private double? ReadLlamaCpu(IReadOnlyList<Process> processes)
    {
        var now = DateTimeOffset.UtcNow;
        var total = 0d;
        var measured = false;
        var live = new HashSet<int>();
        foreach (var process in processes)
        {
            try
            {
                live.Add(process.Id);
                var current = process.TotalProcessorTime;
                if (_processBaselines.TryGetValue(process.Id, out var before))
                {
                    var elapsed = (now - before.At).TotalMilliseconds;
                    if (elapsed > 0)
                    {
                        total += (current - before.Cpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                        measured = true;
                    }
                }

                _processBaselines[process.Id] = (current, now);
            }
            catch (InvalidOperationException)
            {
            }
        }

        foreach (var pid in _processBaselines.Keys.Where(pid => !live.Contains(pid)).ToArray())
        {
            _processBaselines.Remove(pid);
        }

        return measured ? Math.Clamp(total, 0, 100) : null;
    }

    private static long SafeWorkingSet(Process process)
    {
        try { return process.WorkingSet64; }
        catch (InvalidOperationException) { return 0; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _gpuSampler?.Dispose();
            _disposed = true;
        }
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private sealed class PdhGpuSampler : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private readonly nint _query;
        private readonly nint _engine;
        private readonly nint _adapterMemory;
        private readonly nint _processMemory;

        private PdhGpuSampler(nint query, nint engine, nint adapterMemory, nint processMemory)
        {
            _query = query;
            _engine = engine;
            _adapterMemory = adapterMemory;
            _processMemory = processMemory;
            _ = PdhCollectQueryData(_query);
        }

        public static PdhGpuSampler? TryCreate()
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;
            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var engine) != 0
                || PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out var adapter) != 0
                || PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out var process) != 0)
            {
                _ = PdhCloseQuery(query);
                return null;
            }

            return new PdhGpuSampler(query, engine, adapter, process);
        }

        public (double? Overall, double? Compute, double? ForLlama, long? DedicatedBytes, long? LlamaDedicatedBytes, string? Diagnostic) Sample(IReadOnlySet<int> llamaPids)
        {
            if (PdhCollectQueryData(_query) != 0)
            {
                return (null, null, null, null, null, "GPU 计数器本次采样失败。");
            }

            var engines = ReadArray(_engine);
            var groups = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var llamaGroups = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var computeGroups = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in engines)
            {
                var match = EngineNameRegex().Match(item.Name);
                if (!match.Success) continue;
                var key = match.Groups["engine"].Value;
                groups[key] = Math.Min(100, groups.GetValueOrDefault(key) + Math.Max(0, item.Value));
                var type = match.Groups["type"].Value;
                if (type.Contains("Compute", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("CUDA", StringComparison.OrdinalIgnoreCase))
                {
                    computeGroups[key] = groups[key];
                }

                if (int.TryParse(match.Groups["pid"].Value, out var pid) && llamaPids.Contains(pid))
                {
                    llamaGroups[key] = Math.Min(100, llamaGroups.GetValueOrDefault(key) + Math.Max(0, item.Value));
                }
            }

            var dedicated = ReadArray(_adapterMemory).Sum(item => Math.Max(0, item.Value));
            var llamaDedicated = ReadArray(_processMemory)
                .Where(item => ProcessNameRegex().Match(item.Name) is { Success: true } match
                    && int.TryParse(match.Groups["pid"].Value, out var pid)
                    && llamaPids.Contains(pid))
                .Sum(item => Math.Max(0, item.Value));
            return (
                groups.Count > 0 ? Math.Clamp(groups.Values.Max(), 0, 100) : null,
                computeGroups.Count > 0 ? Math.Clamp(computeGroups.Values.Max(), 0, 100) : null,
                llamaGroups.Count > 0 ? Math.Clamp(llamaGroups.Values.Max(), 0, 100) : null,
                dedicated > 0 ? (long)dedicated : null,
                llamaDedicated > 0 ? (long)llamaDedicated : null,
                null);
        }

        private static IReadOnlyList<(string Name, double Value)> ReadArray(nint counter)
        {
            uint bytes = 0;
            uint count = 0;
            _ = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bytes, ref count, nint.Zero);
            if (bytes == 0 || count == 0 || bytes > 16 * 1024 * 1024) return [];
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bytes, ref count, buffer) != 0) return [];
                var size = Marshal.SizeOf<PdhFmtCounterValueItem>();
                var values = new List<(string, double)>((int)count);
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + index * size);
                    if (item.Value.Status <= 1 && item.Name != nint.Zero)
                    {
                        values.Add((Marshal.PtrToStringUni(item.Name) ?? string.Empty, item.Value.DoubleValue));
                    }
                }

                return values;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose() => _ = PdhCloseQuery(_query);

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue { public uint Status; public double DoubleValue; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValueItem { public nint Name; public PdhFmtCounterValue Value; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, nuint userData, out nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(nint query, string fullCounterPath, nuint userData, out nint counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(nint query);
    }

    [GeneratedRegex(@"^pid_(?<pid>\d+)_(?<engine>luid_.*?_phys_\d+_eng_\d+_engtype_(?<type>.+?))(?:_\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EngineNameRegex();

    [GeneratedRegex(@"^pid_(?<pid>\d+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcessNameRegex();
}
