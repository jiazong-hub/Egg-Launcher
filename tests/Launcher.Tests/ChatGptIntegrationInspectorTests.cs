using Launcher.ChatGPT.Discovery;

namespace Launcher.Tests;

public sealed class ChatGptIntegrationInspectorTests
{
    [Fact]
    public async Task InspectAsync_ReturnsKeyNamesWithoutReturningValues()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "config.toml"),
                "model = \"official-model\"\nopenai_base_url = \"https://example.invalid\"\n[projects]\nsecret = \"do-not-return\"\n");
            await File.WriteAllTextAsync(Path.Combine(root, "auth.json"), "{\"token\":\"secret\"}");
            await File.WriteAllTextAsync(Path.Combine(root, "thread_history_1.sqlite"), string.Empty);

            var inspector = new ChatGptIntegrationInspector(root);
            var result = await inspector.InspectAsync();

            Assert.True(result.ConfigExists);
            Assert.True(result.AuthenticationStateExists);
            Assert.True(result.HistoryStateExists);
            Assert.Equal(new[] { "model", "openai_base_url" }, result.ManagedKeysPresent);
            Assert.DoesNotContain(result.TopLevelConfigKeys, key => key.Contains("secret", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

