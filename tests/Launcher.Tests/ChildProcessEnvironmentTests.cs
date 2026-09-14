using System.Diagnostics;
using Launcher.Runtime.Processes;

namespace Launcher.Tests;

public sealed class ChildProcessEnvironmentTests
{
    [Fact]
    public void RemoveSensitiveVariables_RemovesCredentialsAndPreservesRuntimeVariables()
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        startInfo.Environment["OPENAI_API_KEY"] = "secret";
        startInfo.Environment["AZURE_CLIENT_SECRET"] = "secret";
        startInfo.Environment["HF_TOKEN"] = "secret";
        startInfo.Environment["CUDA_VISIBLE_DEVICES"] = "0";
        startInfo.Environment["HTTP_PROXY"] = "http://127.0.0.1:3128";

        ChildProcessEnvironment.RemoveSensitiveVariables(startInfo);

        Assert.False(startInfo.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.False(startInfo.Environment.ContainsKey("AZURE_CLIENT_SECRET"));
        Assert.False(startInfo.Environment.ContainsKey("HF_TOKEN"));
        Assert.Equal("0", startInfo.Environment["CUDA_VISIBLE_DEVICES"]);
        Assert.Equal("http://127.0.0.1:3128", startInfo.Environment["HTTP_PROXY"]);
    }
}
