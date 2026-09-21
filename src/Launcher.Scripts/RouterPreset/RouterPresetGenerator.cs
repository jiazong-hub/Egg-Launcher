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

        if (profile.ModelType == ModelType.MoE)
        {
            if (profile.MoeExpertPlacement == MoeExpertPlacement.CpuAll)
            {
                Append(builder, "cpu-moe", "true");
            }
            else if (profile.MoeExpertPlacement == MoeExpertPlacement.CpuFirstLayers
                     && profile.CpuMoeLayers is int cpuMoeLayers)
            {
                Append(builder, "n-cpu-moe", cpuMoeLayers);
            }
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

        AppendMtp(builder, profile, runtimeRoot);
        AppendVision(builder, profile, runtimeRoot);

        Append(builder, "sleep-idle-seconds", profile.IdleSleepSeconds);

        Append(builder, "load-on-startup", loadOnStartup ? "true" : "false");

        foreach (var argument in profile.ExtraArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, argument.Key, argument.Value ?? "true");
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendMtp(StringBuilder builder, ModelProfile profile, string runtimeRoot)
    {
        if (!profile.MtpEnabled)
        {
            return;
        }

        Append(builder, "spec-type", "draft-mtp");
        if (profile.MtpSource == MtpSourceKind.External)
        {
            var draftPath = Path.GetFullPath(Path.Combine(
                runtimeRoot,
                profile.MtpDraftModelRelativePath
                    ?? throw new InvalidDataException("外部 MTP 配置缺少辅助模型路径。")));
            Append(builder, "spec-draft-model", draftPath);
        }

        AppendOptional(builder, "spec-draft-n-max", profile.MtpDraftMaxTokens);
        AppendOptional(builder, "spec-draft-n-min", profile.MtpDraftMinTokens);
        AppendOptional(builder, "spec-draft-p-min", profile.MtpDraftMinimumProbability);
        AppendOptional(builder, "spec-draft-p-split", profile.MtpDraftSplitProbability);
        if (profile.MtpBackendSampling is bool backendSampling)
        {
            Append(
                builder,
                backendSampling ? "spec-draft-backend-sampling" : "no-spec-draft-backend-sampling",
                "true");
        }

        if (profile.MtpSource != MtpSourceKind.External)
        {
            return;
        }

        AppendOptional(builder, "spec-draft-ngl", profile.MtpDraftGpuLayers);
        AppendOptional(builder, "spec-draft-device", profile.MtpDraftDevice);
        AppendOptional(builder, "spec-draft-type-k", profile.MtpDraftCacheTypeK);
        AppendOptional(builder, "spec-draft-type-v", profile.MtpDraftCacheTypeV);
        AppendOptional(builder, "spec-draft-threads", profile.MtpDraftThreads);
        AppendOptional(builder, "spec-draft-threads-batch", profile.MtpDraftBatchThreads);
    }

    private static void AppendVision(StringBuilder builder, ModelProfile profile, string runtimeRoot)
    {
        if (!profile.VisionEnabled)
        {
            Append(builder, "no-mmproj", "true");
            return;
        }

        if (profile.VisionSource == VisionSourceKind.External)
        {
            var projectorPath = Path.GetFullPath(Path.Combine(
                runtimeRoot,
                profile.VisionProjectorRelativePath
                    ?? throw new InvalidDataException("外置视觉模块配置缺少 mmproj 路径。")));
            Append(builder, "mmproj", projectorPath);
        }

        if (profile.VisionProjectorOffload is bool offload)
        {
            Append(builder, offload ? "mmproj-offload" : "no-mmproj-offload", "true");
        }

        AppendOptional(builder, "mmproj-device", profile.VisionProjectorDevice);
        AppendOptional(builder, "image-min-tokens", profile.VisionImageMinTokens);
        AppendOptional(builder, "image-max-tokens", profile.VisionImageMaxTokens);
        AppendOptional(builder, "mtmd-batch-max-tokens", profile.VisionBatchMaxTokens);
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

    private static void AppendOptional(StringBuilder builder, string key, int? value)
    {
        if (value is int number)
        {
            Append(builder, key, number);
        }
    }

    private static void AppendOptional(StringBuilder builder, string key, double? value)
    {
        if (value is double number)
        {
            Append(builder, key, number.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendOptional(StringBuilder builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Append(builder, key, value.Trim());
        }
    }

    private static void Append(StringBuilder builder, string key, string value) =>
        builder.Append(key).Append(" = ").AppendLine(value);
}
