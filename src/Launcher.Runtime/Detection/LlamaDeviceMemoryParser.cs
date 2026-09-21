using System.Text.RegularExpressions;

namespace Launcher.Runtime.Detection;

public static partial class LlamaDeviceMemoryParser
{
    private const long Mebibyte = 1024L * 1024L;

    public static IReadOnlyList<LlamaDeviceMemoryInfo> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var devices = new List<LlamaDeviceMemoryInfo>();
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DeviceMemoryRegex().Match(line);
            if (!match.Success
                || string.Equals(match.Groups["backend"].Value, "CPU", StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(match.Groups["total"].Value, out var totalMib)
                || totalMib <= 0)
            {
                continue;
            }

            long? freeBytes = null;
            if (long.TryParse(match.Groups["free"].Value, out var freeMib) && freeMib >= 0)
            {
                freeBytes = freeMib * Mebibyte;
            }

            devices.Add(new LlamaDeviceMemoryInfo(
                match.Groups["backend"].Value,
                match.Groups["name"].Value.Trim(),
                totalMib * Mebibyte,
                freeBytes));
        }

        return devices;
    }

    public static LlamaDeviceMemoryInfo? FindBestMatch(string? output, string? adapterName)
    {
        var devices = Parse(output);
        if (devices.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(adapterName))
        {
            var exact = devices.FirstOrDefault(device =>
                device.Name.Contains(adapterName, StringComparison.OrdinalIgnoreCase)
                || adapterName.Contains(device.Name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return devices.OrderByDescending(device => device.TotalBytes).First();
    }

    [GeneratedRegex(
        @"^(?<backend>[A-Za-z]+)\d*\s*:\s*(?<name>[^\r\n(]+?)\s*\(\s*(?<total>\d+)\s*MiB(?:\s*,\s*(?<free>\d+)\s*MiB\s+free)?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceMemoryRegex();
}

public sealed record LlamaDeviceMemoryInfo(
    string Backend,
    string Name,
    long TotalBytes,
    long? FreeBytes);
