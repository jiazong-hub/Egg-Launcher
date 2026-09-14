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
            "load-on-startup", "sleep-idle-seconds", "chat-template-file",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Validate(ModelProfile profile, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var errors = new List<string>();

        if (profile.SchemaVersion != ModelProfile.CurrentSchemaVersion)
        {
            errors.Add($"不支持的 Profile 版本：{profile.SchemaVersion}。");
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

        if (!profile.Jinja && !string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
        {
            errors.Add("使用 Chat Template 文件时必须启用 Jinja。");
        }

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
