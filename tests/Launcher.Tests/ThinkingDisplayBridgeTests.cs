using System.Text;
using System.Text.Json.Nodes;
using Launcher.Runtime.Transport;
using Launcher.Models.Profiles;

namespace Launcher.Tests;

public sealed class ThinkingDisplayBridgeTests
{
    private static string Frame(string type, string payload) => $"event: {type}\ndata: {payload}\n\n";

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(81920)]
    public void FragmentedUtf8ThinkingIsMirroredAndOriginalEventsPreserved(int chunkSize)
    {
        var delta = Frame("response.reasoning_text.delta", """{"type":"response.reasoning_text.delta","item_id":"rs_1","delta":"思考内容🙂"}""");
        var text = Frame("response.output_text.delta", """{"type":"response.output_text.delta","delta":"答案"}""");
        var done = Frame("response.output_item.done", """{"type":"response.output_item.done","item":{"type":"reasoning","id":"rs_1","summary":[],"content":[{"type":"reasoning_text","text":"思考内容🙂"}]}}""");
        var terminal = Frame("response.completed", """{"type":"response.completed","response":{"output":[{"type":"reasoning","id":"rs_1","summary":[],"content":[{"type":"reasoning_text","text":"思考内容🙂"}]}]}}""");
        var bytes = Encoding.UTF8.GetBytes(delta + text + done + terminal);
        using var bridge = new ThinkingDisplayBridge();
        using var output = new MemoryStream();
        for (var index = 0; index < bytes.Length; index += chunkSize)
            output.Write(bridge.Observe(bytes.AsSpan(index, Math.Min(chunkSize, bytes.Length - index))));
        output.Write(bridge.Complete());
        var result = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(delta, result);
        Assert.Contains(text, result);
        var messages = result.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!).ToArray();
        var summary = Assert.Single(messages, message => message["type"]!.ToString() == "response.reasoning_summary_text.delta");
        Assert.Equal("思考内容🙂", summary["delta"]!.ToString());
        var item = messages.Single(message => message["type"]!.ToString() == "response.output_item.done")["item"]!;
        Assert.Equal(item["content"]![0]!["text"]!.ToString(), item["summary"]![0]!["text"]!.ToString());
        Assert.Equal("response.completed", messages.Last()["type"]!.ToString());
    }

    [Fact]
    public void NativeSummaryAndMalformedOrUnrelatedEventsArePreserved()
    {
        var native = Frame("response.reasoning_summary_text.delta", """{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":"真正的摘要"}""");
        var raw = Frame("response.reasoning_text.delta", """{"type":"response.reasoning_text.delta","item_id":"rs_1","delta":"原始内容"}""");
        const string extra = ": heartbeat\r\n\r\nevent: unknown\ndata: broken\n\n";
        using var bridge = new ThinkingDisplayBridge();
        Assert.Equal(native + raw + extra, Encoding.UTF8.GetString(bridge.Observe(Encoding.UTF8.GetBytes(native + raw + extra))));
    }

    [Fact]
    public void OversizedFrameAndUnterminatedTailAreNotLost()
    {
        var original = ":" + new string('a', 4 * 1024 * 1024 + 10) + "\n\ntrailing";
        using var bridge = new ThinkingDisplayBridge();
        var result = Encoding.UTF8.GetString(bridge.Observe(Encoding.UTF8.GetBytes(original))) + Encoding.UTF8.GetString(bridge.Complete());
        Assert.Equal(original, result);
    }

    [Theory]
    [InlineData("原始内容", true)]
    [InlineData("独立摘要", false)]
    public void OnlyIdenticalDisplayCopyIsRemovedFromNextModelInput(string summary, bool removed)
    {
        var body = Encoding.UTF8.GetBytes("""{"input":[{"type":"reasoning","summary":[{"type":"summary_text","text":"SUMMARY"}],"content":[{"type":"reasoning_text","text":"原始内容"}]}],"reasoning":{"effort":"xhigh"},"chat_template_kwargs":{"enable_thinking":true}}""".Replace("SUMMARY", summary, StringComparison.Ordinal));
        var result = JsonNode.Parse(ThinkingDisplayBridge.RemoveDisplayCopies(body))!;
        Assert.Equal(removed ? 0 : 1, result["input"]![0]!["summary"]!.AsArray().Count);
        Assert.Equal("原始内容", result["input"]![0]!["content"]![0]!["text"]!.ToString());
        Assert.Equal("xhigh", result["reasoning"]!["effort"]!.ToString());
        Assert.True(result["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        if (!removed) Assert.Equal(body, ThinkingDisplayBridge.RemoveDisplayCopies(body));
    }

    [Fact]
    public void DisplayPreferenceRoundTripsThroughModelDefaultsWithoutChangingThinking()
    {
        var original = new ModelProfile { Id = "model", Alias = "model", DisplayName = "Model", ModelRelativePath = "model.gguf", ShowThinkingProcess = true, ThinkingEnabled = false, SupportsThinkingSwitch = true };
        var defaults = ModelParameterDefaults.FromProfile(original);
        var restored = defaults.ApplyTo(original with { ShowThinkingProcess = false });
        Assert.True(restored.ShowThinkingProcess);
        Assert.False(restored.ThinkingEnabled);
    }
}
