using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.ChatGPT.Catalog;
using Launcher.Models.Profiles;
using Launcher.Orchestration.Models;
using Launcher.Runtime.Router;
using Launcher.Runtime.Transport;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class ThinkingSettingsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChatTemplates", name));

    [Theory]
    [InlineData("qwen38-27b.jinja")]
    [InlineData("unsloth-qwen38-27b.jinja")]
    public void ActualCompatibleTemplatePreservesNativeLevelsAndThinkingSwitch(string file)
    {
        var original = Fixture(file);
        var analysis = ChatTemplateRules.Analyze(original);
        var source = ReasoningTemplateDescriptor.Read(original);
        var compatible = ReasoningTemplateDescriptor.Read(analysis.RepairedTemplate!);
        Assert.Equal(["xhigh", "medium", "low"], compatible.Levels);
        Assert.Equal(source.Levels, compatible.Levels);
        Assert.Equal("xhigh", compatible.DefaultLevel);
        Assert.True(compatible.HasThinkingControl);
        Assert.True(compatible.DefaultThinking);
        Assert.DoesNotContain("high", compatible.Levels);
    }

    [Fact]
    public void TemplateCommentsAndDynamicListsDoNotInventLevels()
    {
        Assert.Empty(ReasoningTemplateDescriptor.Read("{# if reasoning_effort not in ('high', 'low') #}").Levels);
        Assert.Empty(ReasoningTemplateDescriptor.Read("{% if reasoning_effort not in (vendor_levels, 'low') %}").Levels);
    }

    [Fact]
    public async Task NativeProbeVerifiesLevelsDefaultAliasesAndResponsesInsteadOfDependingOnPropsList()
    {
        var handler = new NativeStub();
        using var http = new HttpClient(handler);
        var result = await new LlamaReasoningNativeProbe(http, new NativePromptEvidence(handler.PromptDirectory)).ProbeAsync(new Uri("http://127.0.0.1:1234/"), "model",
            Fixture("unsloth-qwen38-27b.jinja"), CancellationToken.None);
        Assert.Equal(LlamaReasoningCapabilityStatus.Verified, result.Status);
        Assert.Equal(["xhigh", "medium", "low"], result.SupportedLevels);
        Assert.Equal("xhigh", result.DefaultLevel);
        Assert.True(result.SupportsThinkingSwitch);
        Assert.True(result.ResponsesVerified);
        Assert.Equal("xhigh", result.Aliases["high"]);
    }

    [Fact]
    public async Task RuntimeIgnoringEffortDoesNotPassDetection()
    {
        var handler = new NativeStub { IgnoreEffort = true };
        using var http = new HttpClient(handler);
        var result = await new LlamaReasoningNativeProbe(http, new NativePromptEvidence(handler.PromptDirectory)).ProbeAsync(new Uri("http://127.0.0.1:1234/"), "model",
            Fixture("qwen38-27b.jinja"), CancellationToken.None);
        Assert.Equal(LlamaReasoningCapabilityStatus.Unknown, result.Status);
        Assert.Empty(result.SupportedLevels);
        Assert.False(result.ResponsesVerified);
    }

    [Fact]
    public async Task ResponsesIgnoringParametersDoesNotPassEvenWhenTemplateRenderSucceeds()
    {
        var handler = new NativeStub { IgnoreResponses = true };
        using var http = new HttpClient(handler);
        var result = await new LlamaReasoningNativeProbe(http, new NativePromptEvidence(handler.PromptDirectory)).ProbeAsync(new Uri("http://127.0.0.1:1234/"), "model",
            Fixture("qwen38-27b.jinja"), CancellationToken.None);
        Assert.Equal(LlamaReasoningCapabilityStatus.Verified, result.Status);
        Assert.False(result.ResponsesVerified);
        Assert.Null(result.SupportsThinkingSwitch);
    }

    [Fact]
    public async Task LegacyVerifiedCapabilityIsLoadedButRequiresNewDetectionWithoutOverwritingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "ThinkingSettingsTests", Guid.NewGuid().ToString("N"));
        var directory = JsonModelProfileStore.GetProfilesDirectory(root);
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "model.json");
            var profile = new ModelProfile
            {
                Id = "model",
                Alias = "model",
                DisplayName = "Model",
                ModelRelativePath = "model.gguf",
                SchemaVersion = 12,
                ContextSize = 16384,
                CompactionSafetyReserve = 2048,
                ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
                SupportedReasoningLevels = ["medium", "high"],
                DefaultReasoningLevel = "medium",
                ExposeReasoningEffortInChatGpt = true,
            };
            var original = JsonSerializer.Serialize(profile);
            await File.WriteAllTextAsync(path, original);
            var result = await new JsonModelProfileStore().LoadAsync(root);
            var migrated = Assert.Single(result.Profiles);
            Assert.Equal(ReasoningCapabilityStatus.Unknown, migrated.ReasoningCapabilityStatus);
            Assert.Null(migrated.ThinkingEnabled);
            Assert.False(migrated.ExposeReasoningEffortInChatGpt);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExplicitThinkingSwitchWinsWithoutMappingPositiveEffortOrChangingMessages()
    {
        var body = Encoding.UTF8.GetBytes("""{"model":"model","input":[{"role":"user","content":"original"}],"reasoning":{"effort":"xhigh","summary":"auto"},"chat_template_kwargs":{"other":17}}""");
        foreach (var enabled in new[] { true, false })
        {
            var changed = JsonNode.Parse(ThinkingRequestPolicy.Apply(body, enabled))!;
            Assert.Equal("xhigh", changed["reasoning"]!["effort"]!.GetValue<string>());
            Assert.Equal("original", changed["input"]![0]!["content"]!.GetValue<string>());
            Assert.Equal(enabled, changed["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
            Assert.Equal(17, changed["chat_template_kwargs"]!["other"]!.GetValue<int>());
        }
        var placeholder = JsonNode.Parse(ThinkingRequestPolicy.Apply(Encoding.UTF8.GetBytes("""{"reasoning":{"effort":"none","summary":"auto"}}"""), true))!;
        Assert.Null(placeholder["reasoning"]!["effort"]);
        Assert.Equal("auto", placeholder["reasoning"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task SavingThinkingOffSuppressesCatalogLevelsButPreservesPreferenceAndDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "ThinkingSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ModelProfile
            {
                Id = "model",
                Alias = "model",
                DisplayName = "Model",
                ModelRelativePath = "model.gguf",
                ContextSize = 16384,
                CompactionSafetyReserve = 2048,
                ThinkingEnabled = false,
                SupportsThinkingSwitch = true,
                ReasoningCapabilityStatus = ReasoningCapabilityStatus.Verified,
                ReasoningResponsesVerified = true,
                ReasoningClientCompatible = true,
                SupportedReasoningLevels = ["low", "medium", "xhigh", "vendor_native"],
                DefaultReasoningLevel = "xhigh",
                ExposeReasoningEffortInChatGpt = true,
            };
            var catalog = Path.Combine(root, "catalog.json");
            await LocalModelConfigurationWriter.WriteAsync(profile, root, Path.Combine(root, "preset.ini"), catalog);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(catalog)))
                Assert.Empty(document.RootElement.GetProperty("models")[0].GetProperty("supported_reasoning_levels").EnumerateArray());
            var defaults = ModelParameterDefaults.FromProfile(profile);
            Assert.False(defaults.ThinkingEnabled);
            Assert.True(defaults.ExposeReasoningEffortInChatGpt);
            Assert.False(defaults.ApplyTo(profile with { ThinkingEnabled = true }).ThinkingEnabled);
            Assert.Contains("reasoning = off", RouterPresetGenerator.Generate(profile, root, false));
            Assert.Contains("--reasoning \"off\"", BatchScriptGenerator.Generate(profile, root));
            Assert.Contains("reasoning-budget = 0", RouterPresetGenerator.Generate(profile, root, false));
            await LocalModelConfigurationWriter.WriteAsync(profile with { ThinkingEnabled = true }, root, Path.Combine(root, "preset.ini"), catalog);
            using var enabledDocument = JsonDocument.Parse(await File.ReadAllTextAsync(catalog));
            Assert.Equal(["low", "medium", "xhigh", "vendor_native"], enabledDocument.RootElement.GetProperty("models")[0]
                .GetProperty("supported_reasoning_levels").EnumerateArray().Select(item => item.GetProperty("effort").GetString()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualTokenCountsRequireActualPromptEvidence(bool ignoreKnownEffort)
    {
        var handler = new NativeStub { EqualTokenCounts = true, IgnoreKnownEffortOnly = ignoreKnownEffort };
        using var http = new HttpClient(handler);
        var result = await new LlamaReasoningNativeProbe(http, new NativePromptEvidence(handler.PromptDirectory)).ProbeAsync(
            new Uri("http://127.0.0.1:1234/"), "model", Fixture("qwen38-27b.jinja"), CancellationToken.None);
        Assert.Equal(LlamaReasoningCapabilityStatus.Verified, result.Status);
        Assert.Equal(!ignoreKnownEffort, result.ResponsesVerified);
        Assert.True(result.SupportsThinkingSwitch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNativeEvidenceCannotConfirmResponses(bool attachEmptyRecorder)
    {
        var handler = new NativeStub { RecordPrompt = false };
        using var http = new HttpClient(handler);
        var result = await new LlamaReasoningNativeProbe(http, attachEmptyRecorder ? new NativePromptEvidence(handler.PromptDirectory) : null).ProbeAsync(
            new Uri("http://127.0.0.1:1234/"), "model", Fixture("qwen38-27b.jinja"), CancellationToken.None);
        Assert.Equal(LlamaReasoningCapabilityStatus.Verified, result.Status);
        Assert.False(result.ResponsesVerified);
        Assert.Null(result.SupportsThinkingSwitch);
    }

    [Fact]
    public void TemplateBindingsAndBooleanConditionsRetainExactNativeValues()
    {
        var template = "{% set effort = reasoning_effort | default('Vendor_Default') %}{% set level = effort %}{% if level not in ('low', 'Vendor_Default') %}{{raise_exception('Unexpected reasoning effort')}}{% endif %}{% if enable_thinking %}on{% else %}off{% endif %}";
        var descriptor = ReasoningTemplateDescriptor.Read(template);
        Assert.Equal(["low", "Vendor_Default"], descriptor.Levels);
        Assert.Equal("Vendor_Default", descriptor.DefaultLevel);
        Assert.True(descriptor.HasThinkingControl);
    }

    [Fact]
    public void MissingTemplateReturnsReadableFailureWithoutChangingOriginalProfile()
    {
        var profile = new ModelProfile
        {
            Id = "missing",
            DisplayName = "Missing",
            Alias = "missing",
            ModelRelativePath = "missing.gguf",
            ChatTemplateRelativePath = "missing.jinja",
            ThinkingEnabled = true,
            SupportsThinkingSwitch = true,
            ReasoningCapabilityCheckedAtUtc = DateTimeOffset.UtcNow
        };
        var check = ReasoningValidationState.Check(profile, Path.GetTempPath());
        Assert.Equal(ReasoningValidationStateKind.Unreadable, check.State);
        var reset = check.ApplyTo(profile);
        Assert.Null(reset.ThinkingEnabled);
        Assert.True(profile.ThinkingEnabled);
        Assert.Equal("missing.jinja", reset.ChatTemplateRelativePath);
        Assert.Contains("读取失败", reset.ReasoningValidationDetails);
    }

    [Fact]
    public void ClientProtocolCompatibilityUsesActualSchemaRatherThanKnownNames()
    {
        using var strings = JsonDocument.Parse("""{"definitions":{"ReasoningEffort":{"type":"string","minLength":1}}}""");
        using var restricted = JsonDocument.Parse("""{"definitions":{"ReasoningEffort":{"type":"string","enum":["low","high"]}}}""");
        using var unknown = JsonDocument.Parse("{}");
        Assert.True(CodexReasoningCompatibility.Accepts(strings.RootElement, ["Vendor_Default"]));
        Assert.False(CodexReasoningCompatibility.Accepts(restricted.RootElement, ["Vendor_Default"]));
        Assert.Null(CodexReasoningCompatibility.Accepts(unknown.RootElement, ["Vendor_Default"]));
        using var catalog = JsonDocument.Parse(LocalModelCatalogBuilder.BuildJson(new LocalModelCatalogOptions
        { Slug = "model", DisplayName = "Model", ContextWindow = 8192, SupportedReasoningLevels = ["Vendor_Default"], DefaultReasoningLevel = "Vendor_Default" }));
        Assert.Equal("Vendor_Default", catalog.RootElement.GetProperty("models")[0].GetProperty("default_reasoning_level").GetString());
    }

    [Theory]
    [InlineData("{\"definitions\":{\"ReasoningEffort\":{\"type\":\"string\",\"maxLength\":3}}}", false)]
    [InlineData("{\"definitions\":{\"ReasoningEffort\":{\"type\":\"string\",\"pattern\":\"^low$\"}}}", null)]
    [InlineData("{\"definitions\":{\"ReasoningEffort\":{\"type\":[\"string\",\"null\"]}}}", null)]
    [InlineData("{\"definitions\":{\"ReasoningEffort\":{\"type\":\"string\",\"minLength\":\"unknown\"}}}", null)]
    [InlineData("{\"definitions\":null}", null)]
    public void UnknownSchemaConstraintsAreNotAssumedCompatible(string json, bool? expected)
    {
        using var schema = JsonDocument.Parse(json);
        Assert.Equal(expected, CodexReasoningCompatibility.Accepts(schema.RootElement, ["Vendor_Default"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePromptLogComparisonPreservesAllContent(bool corrupt)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PromptEvidenceTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            const string prompt = "line one\nline two\r\n";
            var expected = OperatingSystem.IsWindows() ? prompt.Replace("\n", "\r\n") : prompt;
            var verified = await new NativePromptEvidence(directory).MatchesAsync(prompt, async () =>
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "prompt.txt"), expected + (corrupt ? " " : ""));
                return true;
            }, default);
            Assert.Equal(!corrupt, verified);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class NativeStub : HttpMessageHandler
    {
        public string PromptDirectory { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ThinkingEvidenceTests", Guid.NewGuid().ToString("N"))).FullName;
        public bool IgnoreEffort { get; init; }
        public bool IgnoreResponses { get; init; }
        public bool IgnoreKnownEffortOnly { get; init; }
        public bool EqualTokenCounts { get; init; }
        public bool RecordPrompt { get; init; } = true;

        protected override void Dispose(bool disposing)
        {
            if (disposing) Directory.Delete(PromptDirectory, true);
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if (path == "/tokenize") return Json(new { tokens = Enumerable.Repeat(0, body["content"]!.GetValue<string>().Length).ToArray() });
            var responses = path == "/v1/responses";
            var effort = responses ? body["reasoning"]?["effort"]?.GetValue<string>() : body["reasoning_effort"]?.GetValue<string>();
            var thinking = body["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() ?? true;
            if (IgnoreEffort || responses && IgnoreResponses) effort = "xhigh";
            if (responses && IgnoreResponses) thinking = true;
            if (thinking && effort is not (null or "low" or "medium" or "xhigh" or "high"))
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("Jinja exception: Unexpected reasoning effort") };
            if (responses && IgnoreKnownEffortOnly) effort = "xhigh";
            if (effort == "high") effort = "xhigh";
            var prompt = thinking ? "ON: " + (effort ?? "xhigh") + ": Reply with OK." : "OFF: Reply with OK.";
            if (responses && RecordPrompt) await File.WriteAllTextAsync(Path.Combine(PromptDirectory, Guid.NewGuid().ToString("N") + ".txt"), prompt, cancellationToken);
            return responses ? Json(new { usage = new { input_tokens = EqualTokenCounts ? 10 : prompt.Length } }) : Json(new { prompt });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
