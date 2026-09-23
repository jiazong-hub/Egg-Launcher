using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Launcher.Models.Profiles;

public static partial class ModelProfileValidator
{
    private static readonly IReadOnlySet<string> RouterControlledOrProfileKeys = new HashSet<string>(
        [
            "host", "port", "api-key", "alias", "hf-repo", "models-dir", "models-preset", "models-max",
            "models-autoload", "model", "c", "ctx-size", "n-gpu-layers", "device", "flash-attn",
            "cache-type-k", "cache-type-v", "parallel", "jinja", "no-jinja", "batch-size", "ubatch-size",
            "load-on-startup", "sleep-idle-seconds", "chat-template-file", "spec-type", "spec-draft-model",
            "spec-draft-n-max", "spec-draft-n-min", "spec-draft-p-min", "spec-draft-p-split",
            "spec-draft-backend-sampling", "no-spec-draft-backend-sampling", "spec-draft-ngl",
            "spec-draft-device", "spec-draft-type-k", "spec-draft-type-v", "spec-draft-threads",
            "spec-draft-threads-batch", "mmproj", "no-mmproj", "mmproj-offload", "no-mmproj-offload",
            "mmproj-device", "image-min-tokens", "image-max-tokens", "mtmd-batch-max-tokens",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> SupportedReasoningLevels = new HashSet<string>(
        ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "persistent"],
        StringComparer.Ordinal);

    public static IReadOnlyList<string> Validate(ModelProfile profile, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var errors = new List<string>();

        if (profile.SchemaVersion != ModelProfile.CurrentSchemaVersion)
        {
            errors.Add($"不支持的 Profile 版本：{profile.SchemaVersion}。");
        }

        if (!Enum.IsDefined(profile.ModelType) || !Enum.IsDefined(profile.ModelTypeSource))
        {
            errors.Add("模型类型或类型来源无效。");
        }

        if (!Enum.IsDefined(profile.ReasoningCapabilityStatus))
        {
            errors.Add("思考强度能力状态无效。");
        }

        if (!Enum.IsDefined(profile.MtpSource) || !Enum.IsDefined(profile.MtpCapabilityStatus))
        {
            errors.Add("MTP 来源或能力状态无效。");
        }

        if (!Enum.IsDefined(profile.VisionSource) || !Enum.IsDefined(profile.VisionCapabilityStatus))
        {
            errors.Add("视觉模块来源或能力状态无效。");
        }

        if (string.IsNullOrWhiteSpace(profile.Id) || !SafeIdentifierRegex().IsMatch(profile.Id))
        {
            errors.Add("Profile ID 只能包含字母、数字、点、下划线和短横线。");
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            errors.Add("模型显示名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(profile.Alias) || !SafeAliasRegex().IsMatch(profile.Alias))
        {
            errors.Add("模型 Alias 包含不支持的字符。");
        }

        if (!IsLoopbackHost(profile.Host))
        {
            errors.Add("V1 Profile Host 必须是回环地址。");
        }

        if (profile.Port is < 1 or > 65535)
        {
            errors.Add("Profile Port 必须在 1 到 65535 之间。");
        }

        ValidateModelPath(profile.ModelRelativePath, runtimeRoot, errors);
        ValidateSource(profile, runtimeRoot, errors);
        ValidateChatTemplatePath(profile.ChatTemplateRelativePath, runtimeRoot, errors);

        if (profile.ContextSize < 0)
        {
            errors.Add("Context 必须为 0 或正整数。");
        }

        if (profile.CompactionSafetyReserve < 1_024)
        {
            errors.Add("压缩安全余量不能小于 1,024 tokens。");
        }

        if (profile.ContextSize > 0
            && profile.CompactionSafetyReserve >= profile.ContextSize)
        {
            errors.Add("压缩安全余量必须小于 Context；它用于决定 Codex 在下一次推理前何时压缩，而不是输出 token 上限。");
        }

        if (!IsValidGpuLayers(profile.GpuLayers))
        {
            errors.Add("GPU Layers 必须是 auto、all 或非负整数。");
        }

        if (profile.FlashAttention is not ("auto" or "on" or "off"))
        {
            errors.Add("Flash Attention 必须是 auto、on 或 off。");
        }

        if (!IsSafeCacheType(profile.CacheTypeK))
        {
            errors.Add($"K Cache 类型格式无效：{profile.CacheTypeK}。");
        }

        if (!IsSafeCacheType(profile.CacheTypeV))
        {
            errors.Add($"V Cache 类型格式无效：{profile.CacheTypeV}。");
        }

        if (profile.Parallel is 0 or < -1)
        {
            errors.Add("Parallel 必须是 -1（自动）或正整数。");
        }

        if (profile.BatchSize is <= 0)
        {
            errors.Add("Batch Size 必须是正整数。");
        }

        if (profile.MicroBatchSize is <= 0)
        {
            errors.Add("Micro Batch Size 必须是正整数。");
        }

        if (profile.IdleSleepSeconds is 0 or < -1)
        {
            errors.Add("空闲休眠秒数必须为 -1（禁用）或正整数。");
        }

        if (profile.BatchSize is not null
            && profile.MicroBatchSize is not null
            && profile.MicroBatchSize > profile.BatchSize)
        {
            errors.Add("Micro Batch Size 不应大于 Batch Size。");
        }

        if (profile.Device?.IndexOfAny(['\r', '\n']) >= 0)
        {
            errors.Add("Device 不能包含换行符。");
        }

        if (profile.MoeExpertPlacement is not null && !Enum.IsDefined(profile.MoeExpertPlacement.Value))
        {
            errors.Add("MoE 专家放置方式无效。");
        }

        if (profile.ModelType != ModelType.MoE
            && (profile.MoeExpertPlacement is not null || profile.CpuMoeLayers is not null))
        {
            errors.Add("只有 MoE 模型可以设置专家放置参数。");
        }

        if (profile.CpuMoeLayers is <= 0)
        {
            errors.Add("CPU 专家层数必须为正整数。");
        }

        if (profile.MoeExpertPlacement == MoeExpertPlacement.CpuFirstLayers
            && profile.CpuMoeLayers is null)
        {
            errors.Add("选择前 N 层专家放在 CPU 时必须填写层数。");
        }

        if (profile.MoeExpertPlacement is not null
            && (profile.ExtraArguments.ContainsKey("cpu-moe")
                || profile.ExtraArguments.ContainsKey("n-cpu-moe")))
        {
            errors.Add("MoE 专家放置不能同时使用结构化设置和旧版高级参数。");
        }

        if (!profile.Jinja && !string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
        {
            errors.Add("使用 Chat Template 文件时必须启用 Jinja。");
        }

        ValidateReasoningCapability(profile, errors);
        ValidateMtp(profile, runtimeRoot, errors);
        ValidateVision(profile, runtimeRoot, errors);
        ValidateContextCheckpoints(profile, errors);

        foreach (var argument in profile.ExtraArguments)
        {
            if (!PresetKeyRegex().IsMatch(argument.Key))
            {
                errors.Add($"高级参数键无效：{argument.Key}。");
            }
            else if (RouterControlledOrProfileKeys.Contains(argument.Key))
            {
                errors.Add($"高级参数不能覆盖受管理参数：{argument.Key}。");
            }

            if (argument.Value?.IndexOfAny(['\r', '\n']) >= 0)
            {
                errors.Add($"高级参数值不能包含换行符：{argument.Key}。");
            }
        }

        if (profile.DefaultParameters is not null)
        {
            var defaultCandidate = profile.DefaultParameters.ApplyTo(profile) with { DefaultParameters = null };
            foreach (var defaultError in Validate(defaultCandidate, runtimeRoot))
            {
                errors.Add($"此模型的默认参数无效：{defaultError}");
            }
        }

        return errors;
    }

    private static void ValidateContextCheckpoints(ModelProfile profile, ICollection<string> errors)
    {
        var checkpointArguments = profile.ExtraArguments
            .Where(argument => argument.Key.Equals("ctx-checkpoints", StringComparison.OrdinalIgnoreCase)
                               || argument.Key.Equals("swa-checkpoints", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (checkpointArguments.Length > 1)
        {
            errors.Add("上下文检查点只能使用 --ctx-checkpoints 或其别名 --swa-checkpoints 其中之一。");
        }

        foreach (var argument in checkpointArguments)
        {
            if (argument.Value is null
                || !int.TryParse(argument.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count < 0)
            {
                errors.Add($"高级参数 --{argument.Key} 必须是非负整数。");
            }
        }
    }

    private static void ValidateReasoningCapability(ModelProfile profile, ICollection<string> errors)
    {
        var levels = profile.SupportedReasoningLevels;
        if (levels is null)
        {
            errors.Add("思考强度档位列表不能为空引用。");
            return;
        }

        if (levels.Count > SupportedReasoningLevels.Count
            || levels.Any(level => string.IsNullOrWhiteSpace(level) || !SupportedReasoningLevels.Contains(level))
            || levels.Distinct(StringComparer.Ordinal).Count() != levels.Count)
        {
            errors.Add("思考强度档位列表包含未知、重复或格式无效的值。");
        }

        if (profile.ReasoningCapabilityStatus == ReasoningCapabilityStatus.Verified)
        {
            if (levels.Count == 0)
            {
                errors.Add("已验证的思考强度能力必须包含明确档位。");
            }

            if (profile.DefaultReasoningLevel is not null
                && !levels.Contains(profile.DefaultReasoningLevel, StringComparer.Ordinal))
            {
                errors.Add("默认思考强度必须属于已验证档位。");
            }
        }
        else if (levels.Count > 0 || profile.DefaultReasoningLevel is not null)
        {
            errors.Add("未验证的思考强度能力不能保存档位或默认值。");
        }

        if (profile.ExposeReasoningEffortInChatGpt
            && profile.ReasoningCapabilityStatus != ReasoningCapabilityStatus.Verified)
        {
            errors.Add("只有档位已经明确验证时，才能向 ChatGPT 开放思考强度调节。");
        }

        if (profile.ReasoningCapabilitySignature?.Length > 128
            || profile.ReasoningCapabilitySignature?.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add("思考强度能力签名无效。");
        }
    }

    private static void ValidateMtp(
        ModelProfile profile,
        string runtimeRoot,
        ICollection<string> errors)
    {
        if (profile.MtpDraftMaxTokens is <= 0)
        {
            errors.Add("MTP N Max 必须是正整数。");
        }

        if (profile.MtpDraftMinTokens is < 0)
        {
            errors.Add("MTP N Min 必须是非负整数。");
        }

        if (profile.MtpDraftMaxTokens is int maximum
            && profile.MtpDraftMinTokens is int minimum
            && minimum > maximum)
        {
            errors.Add("MTP N Min 不能大于 N Max。");
        }

        if (!IsUnitInterval(profile.MtpDraftMinimumProbability))
        {
            errors.Add("MTP P Min 必须在 0 到 1 之间。");
        }

        if (!IsUnitInterval(profile.MtpDraftSplitProbability))
        {
            errors.Add("MTP P Split 必须在 0 到 1 之间。");
        }

        if (profile.MtpDraftThreads is <= 0 || profile.MtpDraftBatchThreads is <= 0)
        {
            errors.Add("MTP 线程数必须是正整数。");
        }

        if (profile.MtpDraftGpuLayers is not null
            && (!int.TryParse(
                    profile.MtpDraftGpuLayers,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var mtpGpuLayers)
                || mtpGpuLayers < 0))
        {
            errors.Add("MTP GPU Layers 必须是非负整数，或留空使用自动值。");
        }

        if (profile.MtpDraftCacheTypeK is not null && !IsSafeCacheType(profile.MtpDraftCacheTypeK))
        {
            errors.Add("MTP K Cache 类型格式无效。");
        }

        if (profile.MtpDraftCacheTypeV is not null && !IsSafeCacheType(profile.MtpDraftCacheTypeV))
        {
            errors.Add("MTP V Cache 类型格式无效。");
        }

        if (profile.MtpDraftDevice?.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add("MTP Device 不能包含控制字符。");
        }

        ValidateMtpCompanionPath(profile, runtimeRoot, errors);

        if (profile.MtpEnabled && profile.MtpSource == MtpSourceKind.External)
        {
            if (string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath))
            {
                errors.Add("启用外部 MTP 时必须先在模型管理中加载匹配的 MTP 辅助 GGUF 文件。");
            }

            if (profile.MtpCapabilityStatus != MtpCapabilityStatus.Verified)
            {
                errors.Add("外部 MTP 只有通过 llama.cpp 联合加载验证后才能启用。");
            }
        }

        if (profile.MtpEnabled
            && profile.MtpSource == MtpSourceKind.Embedded
            && profile.MtpCapabilityStatus is not (MtpCapabilityStatus.EmbeddedCandidate or MtpCapabilityStatus.Verified))
        {
            errors.Add("尚未从主模型取得可用的内置 MTP 依据，不能启用 MTP。");
        }

        if (profile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
            && (string.IsNullOrWhiteSpace(profile.MtpValidationSignature)
                || profile.MtpValidatedAtUtc is null))
        {
            errors.Add("MTP 已验证状态缺少有效的签名或验证时间。");
        }

        if (profile.MtpValidationSignature?.Length > 128
            || profile.MtpValidationSignature?.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add("MTP 验证签名无效。");
        }
    }

    private static void ValidateMtpCompanionPath(
        ModelProfile profile,
        string runtimeRoot,
        ICollection<string> errors)
    {
        var companionPath = profile.MtpDraftModelRelativePath;
        if (string.IsNullOrWhiteSpace(companionPath))
        {
            return;
        }

        if (Path.IsPathRooted(companionPath)
            || companionPath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0
            || !string.Equals(Path.GetExtension(companionPath), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("MTP 辅助模型必须是 Runtime models 目录内的相对 GGUF 路径。");
            return;
        }

        try
        {
            var root = Path.GetFullPath(runtimeRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var modelsRoot = Path.GetFullPath(Path.Combine(root, "models")).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var companionFullPath = Path.GetFullPath(Path.Combine(root, companionPath));
            var mainFullPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
            if (!companionFullPath.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("MTP 辅助模型必须位于 Runtime 的 models 目录内。");
            }
            else if (string.Equals(companionFullPath, mainFullPath, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("MTP 辅助模型不能与主模型使用同一个文件。");
            }
            else if (profile.MtpEnabled
                     && profile.MtpSource == MtpSourceKind.External
                     && !File.Exists(companionFullPath))
            {
                errors.Add("所选 MTP 辅助模型文件不存在。");
            }
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            errors.Add("MTP 辅助模型路径格式无效。");
        }
    }

    private static bool IsUnitInterval(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value is >= 0 and <= 1;

    private static void ValidateVision(ModelProfile profile, string runtimeRoot, ICollection<string> errors)
    {
        if (profile.VisionImageMinTokens is <= 0
            || profile.VisionImageMaxTokens is <= 0
            || profile.VisionBatchMaxTokens is <= 0)
        {
            errors.Add("视觉模块 Token 参数必须是正整数。");
        }

        if (profile.VisionImageMinTokens is int minimum
            && profile.VisionImageMaxTokens is int maximum
            && minimum > maximum)
        {
            errors.Add("视觉模块 Image Min Tokens 不能大于 Image Max Tokens。");
        }

        if (profile.VisionProjectorDevice?.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add("视觉模块 Device 不能包含控制字符。");
        }

        ValidateVisionProjectorPath(profile, runtimeRoot, errors);
        if (profile.VisionEnabled && profile.VisionCapabilityStatus != VisionCapabilityStatus.Verified)
        {
            errors.Add("视觉模块只有通过 llama.cpp 图片请求验证后才能启用。");
        }

        if (profile.VisionEnabled
            && profile.VisionSource == VisionSourceKind.External
            && string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath))
        {
            errors.Add("启用外置视觉模块时必须先关联匹配的 mmproj GGUF。");
        }

        if (profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
            && (string.IsNullOrWhiteSpace(profile.VisionValidationSignature)
                || profile.VisionValidatedAtUtc is null))
        {
            errors.Add("视觉模块已验证状态缺少有效的签名或验证时间。");
        }

        if (profile.VisionValidationSignature?.Length > 128
            || profile.VisionValidationSignature?.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add("视觉模块验证签名无效。");
        }
    }

    private static void ValidateVisionProjectorPath(
        ModelProfile profile,
        string runtimeRoot,
        ICollection<string> errors)
    {
        var relativePath = profile.VisionProjectorRelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        if (Path.IsPathRooted(relativePath)
            || relativePath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0
            || !string.Equals(Path.GetExtension(relativePath), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("视觉投影文件必须是 Runtime models 目录内的相对 GGUF 路径。");
            return;
        }

        try
        {
            var root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var modelsRoot = Path.GetFullPath(Path.Combine(root, "models")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            var projector = Path.GetFullPath(Path.Combine(root, relativePath));
            var main = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
            if (!projector.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("视觉投影文件必须位于 Runtime 的 models 目录内。");
            }
            else if (string.Equals(projector, main, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("视觉投影文件不能与主模型使用同一个文件。");
            }
            else if (profile.VisionSource == VisionSourceKind.External
                     && profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
                     && !File.Exists(projector))
            {
                errors.Add("已验证的视觉投影文件不存在。");
            }
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            errors.Add("视觉投影文件路径格式无效。");
        }
    }

    private static void ValidateSource(
        ModelProfile profile,
        string runtimeRoot,
        ICollection<string> errors)
    {
        if (!Enum.IsDefined(profile.SourceKind))
        {
            errors.Add("模型来源类型无效。");
        }

        foreach (var (name, value) in new[]
                 {
                     ("远程模型 ID", profile.RemoteModelId),
                     ("远程仓库 ID", profile.RemoteRepositoryId),
                     ("远程量化", profile.RemoteQuantization),
                 })
        {
            if (value?.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                errors.Add($"{name}不能包含控制字符。");
            }
        }

        if (profile.RemoteModelId?.Length > 512
            || profile.RemoteRepositoryId?.Length > 256
            || profile.RemoteQuantization?.Length > 128)
        {
            errors.Add("远程模型来源字段过长。");
        }

        if (profile.KnownSizeBytes is < 0 || profile.KnownShardCount is < 1)
        {
            errors.Add("已知模型大小或分片数无效。");
        }

        if (profile.SourceKind != ModelSourceKind.LlamaCache)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.RemoteModelId)
            || string.IsNullOrWhiteSpace(profile.RemoteRepositoryId)
            || !RemoteRepositoryRegex().IsMatch(profile.RemoteRepositoryId)
            || !profile.RemoteModelId.StartsWith(profile.RemoteRepositoryId + ":", StringComparison.Ordinal))
        {
            errors.Add("llama 缓存模型必须记录有效的仓库 ID 与下载 ID。");
        }

        try
        {
            var root = Path.GetFullPath(runtimeRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cacheRoot = Path.GetFullPath(Path.Combine(root, "models"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var modelPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
            if (!modelPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("llama 缓存模型必须位于 Runtime 的 models 目录内。");
            }
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            errors.Add("llama 缓存模型路径无效。");
        }
    }

    private static void ValidateChatTemplatePath(
        string? templatePath,
        string runtimeRoot,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
        {
            return;
        }

        if (templatePath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
        {
            errors.Add("Chat Template 路径包含不支持的字符。");
            return;
        }

        if (Path.IsPathRooted(templatePath))
        {
            errors.Add("Chat Template 路径必须相对 Runtime Root 保存。");
            return;
        }

        try
        {
            if (!string.Equals(Path.GetExtension(templatePath), ".jinja", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Chat Template 必须是 .jinja 文件。");
            }

            var root = Path.GetFullPath(runtimeRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var templateFullPath = Path.GetFullPath(Path.Combine(root, templatePath));
            if (!templateFullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Chat Template 相对路径不能逃逸 Runtime Root。");
            }
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            errors.Add("Chat Template 路径格式无效。");
        }
    }

    private static void ValidateModelPath(string modelPath, string runtimeRoot, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            errors.Add("模型路径不能为空。");
            return;
        }

        if (modelPath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
        {
            errors.Add("模型路径包含不支持的字符。");
            return;
        }

        if (Path.IsPathRooted(modelPath))
        {
            errors.Add("Profile 中的模型路径必须相对 Runtime Root 保存。");
            return;
        }

        try
        {
            if (!string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("模型路径必须指向 GGUF 文件。");
            }

            var root = Path.GetFullPath(runtimeRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var modelFullPath = Path.GetFullPath(Path.Combine(root, modelPath));
            if (!modelFullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("模型相对路径不能逃逸 Runtime Root。");
            }
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            errors.Add("模型路径格式无效。");
        }
    }

    private static bool IsInvalidPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or IOException;

    private static bool IsValidGpuLayers(string value) =>
        value is "auto" or "all"
        || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0;

    private static bool IsSafeCacheType(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SafeCacheTypeRegex().IsMatch(value);

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierRegex();

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAliasRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PresetKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCacheTypeRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteRepositoryRegex();
}
