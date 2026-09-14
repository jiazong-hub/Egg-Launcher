using System.Text.Json;
using Launcher.Runtime.Processes;
using Microsoft.Win32;

namespace Launcher.Runtime.Monitoring;

public sealed class WindowsHardwareInventoryReader(IProcessRunner processRunner)
{
    private const string InventoryCommand =
        "$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" +
        "$o=[ordered]@{Memory=@(Get-CimInstance Win32_PhysicalMemory|ForEach-Object{[ordered]@{Manufacturer=$_.Manufacturer;PartNumber=$_.PartNumber;Capacity=[long]$_.Capacity;MemoryType=[int]$_.SMBIOSMemoryType;Speed=[int]($_.ConfiguredClockSpeed)}});" +
        "Gpu=@(Get-CimInstance Win32_VideoController|ForEach-Object{[ordered]@{Name=$_.Name;Manufacturer=$_.AdapterCompatibility;AdapterRam=[long]$_.AdapterRAM}})};" +
        "$o|ConvertTo-Json -Compress -Depth 4";

    public async Task<HardwareInventory> ReadAsync(CancellationToken cancellationToken = default)
    {
        var cpuName = ReadCpuName();
        if (!OperatingSystem.IsWindows())
        {
            return new HardwareInventory(cpuName, Environment.ProcessorCount, 0, [], [], "仅 Windows 支持硬件详情。");
        }

        var installedMemory = WindowsPerformanceSampler.ReadPhysicalMemory().TotalBytes;
        try
        {
            var result = await processRunner.RunAsync(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", InventoryCommand],
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return new HardwareInventory(cpuName, Environment.ProcessorCount, installedMemory, [], [],
                    result.TimedOut ? "读取硬件模块信息超时。" : "Windows 未提供硬件模块详情。");
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
            var memory = ReadMemory(document.RootElement);
            var graphics = ReadGraphics(document.RootElement);
            return new HardwareInventory(cpuName, Environment.ProcessorCount, installedMemory, memory, graphics, null);
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return new HardwareInventory(cpuName, Environment.ProcessorCount, installedMemory, [], [],
                $"Windows 硬件清单不可用：{exception.Message}");
        }
    }

    private static string ReadCpuName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知 CPU";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() is { Length: > 0 } name
                ? name
                : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知 CPU";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知 CPU";
        }
    }

    private static MemoryModuleInfo[] ReadMemory(JsonElement root)
    {
        if (!root.TryGetProperty("Memory", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray().Take(64).Select(value => new MemoryModuleInfo(
            ReadText(value, "Manufacturer", "未知品牌"),
            ReadText(value, "PartNumber", "未知型号"),
            ReadLong(value, "Capacity") ?? 0,
            MemoryTypeName(ReadInt(value, "MemoryType")),
            ReadInt(value, "Speed") is > 0 ? ReadInt(value, "Speed") : null)).ToArray();
    }

    private static GraphicsAdapterInfo[] ReadGraphics(JsonElement root)
    {
        if (!root.TryGetProperty("Gpu", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray().Take(16).Select(value => new GraphicsAdapterInfo(
            ReadText(value, "Name", "未知显卡"),
            ReadText(value, "Manufacturer", "未知厂商"),
            ReadLong(value, "AdapterRam") is > 0 and var ram ? ram : null)).ToArray();
    }

    public static string MemoryTypeName(int? type) => type switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "代数未知",
    };

    private static string ReadText(JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()?.Trim() is { Length: > 0 } text ? text : fallback
            : fallback;

    private static int? ReadInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var node) && node.TryGetInt32(out var number) ? number : null;

    private static long? ReadLong(JsonElement value, string name) =>
        value.TryGetProperty(name, out var node) && node.TryGetInt64(out var number) ? number : null;
}
