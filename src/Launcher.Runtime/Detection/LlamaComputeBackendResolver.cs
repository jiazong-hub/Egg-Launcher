using System.Text.RegularExpressions;

namespace Launcher.Runtime.Detection;

public static partial class LlamaComputeBackendResolver
{
    public static string Resolve(string? deviceOutput, string? gpuLayers, string? selectedDevice)
    {
        if (DisablesGpu(gpuLayers, selectedDevice))
        {
            return "CPU";
        }

        var selectedBackends = ParseSelectedBackends(selectedDevice);
        if (selectedBackends.Count > 0)
        {
            return FormatBackends(selectedBackends);
        }

        if (string.IsNullOrWhiteSpace(deviceOutput))
        {
            return "未返回";
        }

        var acceleratorBackends = ParseAvailableBackends(deviceOutput);
        if (acceleratorBackends.Count == 0)
        {
            return "CPU";
        }

        return FormatBackends(acceleratorBackends);
    }

    public static IReadOnlyList<string> ParseAvailableBackends(string? deviceOutput)
    {
        if (string.IsNullOrWhiteSpace(deviceOutput))
        {
            return [];
        }

        var backends = new List<string>();
        foreach (var line in deviceOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DeviceLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            AddBackend(backends, match.Groups["backend"].Value);
        }

        return backends;
    }

    private static IReadOnlyList<string> ParseSelectedBackends(string? selectedDevice)
    {
        if (string.IsNullOrWhiteSpace(selectedDevice))
        {
            return [];
        }

        var backends = new List<string>();
        foreach (var device in selectedDevice.Split(
                     [','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DeviceIdRegex().Match(device);
            if (match.Success)
            {
                AddBackend(backends, match.Groups["backend"].Value);
            }
        }

        return backends;
    }

    private static bool DisablesGpu(string? gpuLayers, string? selectedDevice)
    {
        var normalizedDevice = selectedDevice?.Trim();
        if (string.Equals(normalizedDevice, "none", StringComparison.OrdinalIgnoreCase)
            || DeviceIdRegex().Match(normalizedDevice ?? string.Empty) is { Success: true } match
                && string.Equals(match.Groups["backend"].Value, "CPU", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return int.TryParse(gpuLayers?.Trim(), out var layers) && layers == 0;
    }

    private static string FormatBackends(IEnumerable<string> backends) =>
        string.Join(" + ", backends.Append("CPU"));

    private static void AddBackend(ICollection<string> backends, string rawBackend)
    {
        var backend = NormalizeBackend(rawBackend);
        if (string.Equals(backend, "CPU", StringComparison.Ordinal)
            || backends.Contains(backend, StringComparer.Ordinal))
        {
            return;
        }

        backends.Add(backend);
    }

    private static string NormalizeBackend(string backend) => backend.ToUpperInvariant() switch
    {
        "CUDA" => "CUDA",
        "ROCM" => "ROCm",
        "VULKAN" => "Vulkan",
        "SYCL" => "SYCL",
        "METAL" => "Metal",
        "CANN" => "CANN",
        "OPENCL" => "OpenCL",
        "WEBGPU" => "WebGPU",
        "BLAS" => "BLAS",
        var value => value,
    };

    [GeneratedRegex(
        @"^(?<backend>[A-Za-z][A-Za-z0-9_-]*?)(?:\d+)?\s*:\s*.+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLineRegex();

    [GeneratedRegex(
        @"^(?<backend>[A-Za-z][A-Za-z0-9_-]*?)(?:\d+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdRegex();
}
