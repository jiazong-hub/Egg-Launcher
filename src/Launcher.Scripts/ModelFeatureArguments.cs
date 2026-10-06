using System.Globalization;
using Launcher.Models.Profiles;

namespace Launcher.Scripts;

internal static class ModelFeatureArguments
{
    public static IReadOnlyList<KeyValuePair<string, string?>> Generate(ModelProfile profile, Func<string, string> resolvePath)
    {
        var arguments = new List<KeyValuePair<string, string?>>();
        AppendMtp(arguments, profile, resolvePath);
        AppendVision(arguments, profile, resolvePath);
        return arguments;
    }

    private static void AppendMtp(List<KeyValuePair<string, string?>> builder, ModelProfile profile, Func<string, string> resolvePath)
    {
        if (!profile.MtpEnabled)
        {
            return;
        }

        Append(builder, "spec-type", "draft-mtp");
        if (profile.MtpSource == MtpSourceKind.External)
        {
            var draftPath = resolvePath(profile.MtpDraftModelRelativePath
                    ?? throw new InvalidDataException("外部 MTP 配置缺少辅助模型路径。"));
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
                null);
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

    private static void AppendVision(List<KeyValuePair<string, string?>> builder, ModelProfile profile, Func<string, string> resolvePath)
    {
        if (!profile.VisionEnabled)
        {
            Append(builder, "no-mmproj", null);
            return;
        }

        if (profile.VisionSource == VisionSourceKind.External)
        {
            var projectorPath = resolvePath(profile.VisionProjectorRelativePath
                    ?? throw new InvalidDataException("外置视觉模块配置缺少 mmproj 路径。"));
            Append(builder, "mmproj", projectorPath);
        }

        if (profile.VisionProjectorOffload is bool offload)
        {
            Append(builder, offload ? "mmproj-offload" : "no-mmproj-offload", null);
        }

        AppendOptional(builder, "mmproj-device", profile.VisionProjectorDevice);
        AppendOptional(builder, "image-min-tokens", profile.VisionImageMinTokens);
        AppendOptional(builder, "image-max-tokens", profile.VisionImageMaxTokens);
        AppendOptional(builder, "mtmd-batch-max-tokens", profile.VisionBatchMaxTokens);
    }

    private static void AppendOptional(List<KeyValuePair<string, string?>> builder, string key, int? value)
    {
        if (value is int number)
        {
            Append(builder, key, number.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendOptional(List<KeyValuePair<string, string?>> builder, string key, double? value)
    {
        if (value is double number)
        {
            Append(builder, key, number.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendOptional(List<KeyValuePair<string, string?>> builder, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Append(builder, key, value.Trim());
        }
    }

    private static void Append(List<KeyValuePair<string, string?>> builder, string key, string? value) =>
        builder.Add(new(key, value));
}
