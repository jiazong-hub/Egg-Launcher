using Launcher.Runtime.Detection;

namespace Launcher.Tests;

public sealed class LlamaDeviceMemoryParserTests
{
    [Fact]
    public void Parse_ReadsGpuCapacityAndFreeMemory()
    {
        var result = LlamaDeviceMemoryParser.Parse("""
            Available devices:
            CUDA0: NVIDIA GeForce RTX 3060 (8191 MiB, 7174 MiB free)
            """);

        var device = Assert.Single(result);
        Assert.Equal("CUDA", device.Backend);
        Assert.Equal("NVIDIA GeForce RTX 3060", device.Name);
        Assert.Equal(8191L * 1024 * 1024, device.TotalBytes);
        Assert.Equal(7174L * 1024 * 1024, device.FreeBytes);
    }

    [Fact]
    public void FindBestMatch_PrefersNamedAdapter()
    {
        const string output = """
            CUDA0: NVIDIA GeForce RTX 3060 (8191 MiB, 7000 MiB free)
            Vulkan1: AMD Radeon 780M (2048 MiB, 1500 MiB free)
            """;

        var result = LlamaDeviceMemoryParser.FindBestMatch(output, "NVIDIA GeForce RTX 3060");

        Assert.NotNull(result);
        Assert.Equal(8191L * 1024 * 1024, result.TotalBytes);
    }

    [Fact]
    public void Parse_IgnoresCpuMemoryEntries()
    {
        var result = LlamaDeviceMemoryParser.Parse(
            "CPU0: AMD Ryzen 7 7700 (32768 MiB, 16000 MiB free)");

        Assert.Empty(result);
    }
}
