using Launcher.Runtime.Monitoring;

namespace Launcher.Tests;

public sealed class WindowsHardwareInventoryReaderTests
{
    [Theory]
    [InlineData(24, "DDR3")]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(999, "代数未知")]
    public void MemoryTypeName_MapsSmbiosValues(int value, string expected) =>
        Assert.Equal(expected, WindowsHardwareInventoryReader.MemoryTypeName(value));
}
