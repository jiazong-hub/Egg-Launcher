using System.Globalization;
using System.Text;
using Launcher.Models.Profiles;

namespace Launcher.Scripts.RouterPreset;

public static class RouterPresetGenerator
{
    public static string Generate(
        ModelProfile profile,
        string runtimeRoot,
        bool loadOnStartup,
        string? chatTemplateOverridePath = null)
    {
        var errors = ModelProfileValidator.Validate(profile, runtimeRoot);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        var modelPath = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
        var builder = new StringBuilder();
        builder.AppendLine("version = 1");
        builder.AppendLine();
        builder.Append('[').Append(profile.Alias).AppendLine("]");
        Append(builder, "model", modelPath);
        Append(builder, "c", profile.ContextSize);
        Append(builder, "n-gpu-layers", profile.GpuLayers);

        if (!string.IsNullOrWhiteSpace(profile.Device))
        {
            Append(builder, "device", profile.Device);
        }

        Append(builder, "flash-attn", profile.FlashAttention);
        Append(builder, "cache-type-k", profile.CacheTypeK);
        Append(builder, "cache-type-v", profile.CacheTypeV);
        Append(builder, "parallel", profile.Parallel);
        Append(builder, profile.Jinja ? "jinja" : "no-jinja", "true");

        var chatTemplatePath = ResolveChatTemplatePath(
            profile,
            runtimeRoot,
            chatTemplateOverridePath);
        if (chatTemplatePath is not null)
        {
            Append(builder, "chat-template-file", chatTemplatePath);
        }

        if (profile.BatchSize is not null)
        {
            Append(builder, "batch-size", profile.BatchSize.Value);
        }

        if (profile.MicroBatchSize is not null)
        {
            Append(builder, "ubatch-size", profile.MicroBatchSize.Value);
        }

        Append(builder, "sleep-idle-seconds", profile.IdleSleepSeconds);

        Append(builder, "load-on-startup", loadOnStartup ? "true" : "false");

        foreach (var argument in profile.ExtraArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, argument.Key, argument.Value ?? "true");
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string? ResolveChatTemplatePath(
        ModelProfile profile,
        string runtimeRoot,
        string? chatTemplateOverridePath)
    {
        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(chatTemplateOverridePath))
        {
            candidate = chatTemplateOverridePath;
        }
        else if (!string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
        {
            candidate = Path.Combine(runtimeRoot, profile.ChatTemplateRelativePath);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (candidate.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidDataException("Chat Template 路径包含无效换行符。");
        }

        var fullPath = Path.GetFullPath(candidate);
        if (!string.Equals(Path.GetExtension(fullPath), ".jinja", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Chat Template 文件必须使用 .jinja 扩展名。");
        }

        return fullPath;
    }

    private static void Append(StringBuilder builder, string key, int value) =>
        Append(builder, key, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string key, string value) =>
        builder.Append(key).Append(" = ").AppendLine(value);
}
