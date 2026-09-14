using System.Text;
using Launcher.Models.Profiles;
using Launcher.Scripts.Templates;

namespace Launcher.Tests;

public sealed class CodexChatTemplateCompatibilityTests
{
    private const string LocalQwenFixture = @"D:\llama.cpp\models\Qwen3.8-9B-Coder-Q4_K_M.gguf";

    private const string StrictTemplate = """
{%- for message in messages %}
    {%- set content = message.content|string|trim %}
    {%- if message.role == "system" %}
        {%- if not loop.first %}
            {{- raise_exception('System message must be at the beginning.') }}
        {%- endif %}
    {%- elif message.role == "user" %}
        {{- '<|im_start|>user\n' + content + '<|im_end|>\n' }}
    {%- endif %}
{%- endfor %}
""";

    [Fact]
    public async Task EnsureAsync_PatchesStrictEmbeddedQwenTemplateAndKeepsRequestLayerTransparent()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var modelPath = Path.Combine(root, "models", "Qwen.gguf");
            WriteGguf(modelPath, StrictTemplate);
            var profile = CreateProfile();

            var result = await CodexChatTemplateCompatibility.EnsureAsync(profile, root);

            Assert.True(result.TemplateGenerated);
            Assert.NotNull(result.Profile.ChatTemplateRelativePath);
            var templatePath = Path.Combine(root, result.Profile.ChatTemplateRelativePath);
            var generated = await File.ReadAllTextAsync(templatePath);
            Assert.StartsWith(CodexChatTemplateCompatibility.OwnershipMarker, generated, StringComparison.Ordinal);
            Assert.DoesNotContain("raise_exception('System message must be at the beginning.')", generated, StringComparison.Ordinal);
            Assert.Contains("'<|im_start|>system\\n' + content", generated, StringComparison.Ordinal);
            Assert.Contains("'<|im_start|>user\\n' + content", generated, StringComparison.Ordinal);

            var second = await CodexChatTemplateCompatibility.EnsureAsync(result.Profile, root);
            Assert.False(second.TemplateGenerated);
            Assert.Equal(result.Profile, second.Profile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_DoesNotOverwriteUnownedTemplateFile()
    {
        var root = CreateRuntimeRoot();
        try
        {
            WriteGguf(Path.Combine(root, "models", "Qwen.gguf"), StrictTemplate);
            var target = Path.Combine(root, "scripts", "templates", "qwen.codex-compatible.jinja");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "user-owned template");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CodexChatTemplateCompatibility.EnsureAsync(CreateProfile(), root));

            Assert.Contains("拒绝覆盖", exception.Message, StringComparison.Ordinal);
            Assert.Equal("user-owned template", await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_LeavesNonStrictModelOnItsEmbeddedTemplate()
    {
        var root = CreateRuntimeRoot();
        try
        {
            WriteGguf(
                Path.Combine(root, "models", "Qwen.gguf"),
                "{{ '<|im_start|>user\\n' + messages[0].content + '<|im_end|>' }}");

            var result = await CodexChatTemplateCompatibility.EnsureAsync(CreateProfile(), root);

            Assert.False(result.TemplateGenerated);
            Assert.Null(result.Profile.ChatTemplateRelativePath);
            Assert.False(Directory.Exists(Path.Combine(root, "scripts", "templates")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureAsync_ReplacesOnlyLauncherOwnedTemplateWhenModelMetadataChanges()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var modelPath = Path.Combine(root, "models", "Qwen.gguf");
            WriteGguf(modelPath, StrictTemplate);
            var profile = CreateProfile();
            var first = await CodexChatTemplateCompatibility.EnsureAsync(profile, root);
            var templatePath = Path.Combine(root, first.Profile.ChatTemplateRelativePath!);

            WriteGguf(modelPath, StrictTemplate + "\n{# model-template-updated #}\n");
            var updated = await CodexChatTemplateCompatibility.EnsureAsync(profile, root);

            Assert.True(updated.TemplateGenerated);
            Assert.Contains(
                "model-template-updated",
                await File.ReadAllTextAsync(templatePath),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryCreateCompatibleTemplate_RejectsAmbiguousRepeatedGuard()
    {
        Assert.False(
            CodexChatTemplateCompatibility.TryCreateCompatibleTemplate(
                StrictTemplate + StrictTemplate,
                out var compatible));
        Assert.Empty(compatible);
    }

    [Fact]
    public void ReadEmbeddedChatTemplate_WhenLocalFixtureExists_ParsesWithoutLoadingModel()
    {
        if (!File.Exists(LocalQwenFixture))
        {
            return;
        }

        var template = CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(LocalQwenFixture);

        Assert.NotNull(template);
        Assert.Contains("System message must be at the beginning.", template, StringComparison.Ordinal);
        Assert.True(CodexChatTemplateCompatibility.TryCreateCompatibleTemplate(template, out var compatible));
        Assert.DoesNotContain("System message must be at the beginning.", compatible, StringComparison.Ordinal);
    }

    private static ModelProfile CreateProfile() => new()
    {
        Id = "qwen",
        DisplayName = "Qwen",
        ModelRelativePath = Path.Combine("models", "Qwen.gguf"),
        Alias = "qwen",
        ContextSize = 16_384,
    };

    private static string CreateRuntimeRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ChatGPTLocalLauncher.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "models"));
        return path;
    }

    private static void WriteGguf(string path, string chatTemplate)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write(0ul);
        writer.Write(2ul);
        WriteString(writer, "general.name");
        writer.Write(8u);
        WriteString(writer, "Test Model");
        WriteString(writer, "tokenizer.chat_template");
        writer.Write(8u);
        WriteString(writer, chatTemplate);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
