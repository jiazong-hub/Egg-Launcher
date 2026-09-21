using Launcher.Runtime.Detection;

namespace Launcher.Tests;

public sealed class LlamaComputeBackendResolverTests
{
    [Theory]
    [InlineData("CUDA0: NVIDIA GeForce RTX 3060 (8191 MiB, 7174 MiB free)", "CUDA + CPU")]
    [InlineData("Vulkan0: AMD Radeon RX 7900 XTX (24576 MiB, 23000 MiB free)", "Vulkan + CPU")]
    [InlineData("ROCm0: AMD Radeon RX 7900 XTX (24576 MiB, 23000 MiB free)", "ROCm + CPU")]
    public void Resolve_UsesBackendReportedByLlama(string deviceOutput, string expected)
    {
        var result = LlamaComputeBackendResolver.Resolve(deviceOutput, "auto", selectedDevice: null);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("auto", "none")]
    [InlineData("auto", "CPU0")]
    public void Resolve_ReturnsCpuWhenGpuIsDisabled(string gpuLayers, string? selectedDevice)
    {
        var result = LlamaComputeBackendResolver.Resolve(
            "CUDA0: NVIDIA GeForce RTX 3060 (8191 MiB, 7174 MiB free)",
            gpuLayers,
            selectedDevice);

        Assert.Equal("CPU", result);
    }

    [Fact]
    public void Resolve_PrefersExplicitDeviceOverOtherAvailableBackends()
    {
        const string output = """
            CUDA0: NVIDIA GeForce RTX 3060 (8191 MiB, 7174 MiB free)
            Vulkan0: NVIDIA GeForce RTX 3060 (8191 MiB, 7174 MiB free)
            """;

        var result = LlamaComputeBackendResolver.Resolve(output, "all", "Vulkan0");

        Assert.Equal("Vulkan + CPU", result);
    }

    [Fact]
    public void Resolve_PreservesMultipleExplicitBackendsWithoutDuplicates()
    {
        var result = LlamaComputeBackendResolver.Resolve(
            deviceOutput: null,
            gpuLayers: "all",
            selectedDevice: "CUDA0,CUDA1");

        Assert.Equal("CUDA + CPU", result);
    }

    [Fact]
    public void Resolve_ReturnsCpuWhenLlamaReportsNoAccelerator()
    {
        var result = LlamaComputeBackendResolver.Resolve(
            "Available devices:",
            "auto",
            selectedDevice: null);

        Assert.Equal("CPU", result);
    }

    [Fact]
    public void Resolve_ReturnsUnavailableWhenDeviceProbeHasNoOutput()
    {
        var result = LlamaComputeBackendResolver.Resolve(
            deviceOutput: null,
            gpuLayers: "auto",
            selectedDevice: null);

        Assert.Equal("未返回", result);
    }
}
