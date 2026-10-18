using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Models.Profiles;

/// <summary>A complete parameter document, distinct from the trusted model registration.</summary>
public sealed record ModelConfigurationDocument
{
    public int FormatVersion { get; init; } = 1;
    public required string ModelId { get; init; }
    public required string ModelRelativePath { get; init; }
    public required ModelProfile Parameters { get; init; }
}

public sealed record ModelConfigurationReferences(string? Template, string? MtpFile, string? VisionFile);

public static class ModelConfigurationFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public const string TypeMismatchPrefix = "配置模型类型不匹配 / Model type mismatch.";

    public static bool IsTypeMismatch(string? error) => error?.StartsWith(TypeMismatchPrefix, StringComparison.Ordinal) == true;

    /// <summary>A new unsaved draft; an incompatible document is never used as editable parameters.</summary>
    public static ModelProfile CreateUnloadedDraft(ModelProfile registration) => Bind(registration, new ModelProfile
    {
        Id = registration.Id,
        Alias = registration.Alias,
        DisplayName = registration.DisplayName,
        ModelRelativePath = registration.ModelRelativePath,
        ModelType = registration.ModelType,
        ContextSize = 16_384,
        CompactionSafetyReserve = ModelProfile.DefaultCompactionSafetyReserve,
    }) with
    { ConfigurationError = registration.ConfigurationError, ConfigurationContentDigest = null };

    public static string GetDirectory(ModelProfile model, string root)
    {
        if (string.IsNullOrWhiteSpace(model.Id) || model.Id is "." or ".."
            || model.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            || model.Id.EndsWith('.'))
            throw new InvalidDataException("模型配置目录标识无效 / Invalid model configuration directory ID.");
        return Path.Combine(JsonModelProfileStore.GetProfilesDirectory(root), "configurations", model.Id);
    }

    public static string InitialPath(ModelProfile model, string root) => Path.Combine(GetDirectory(model, root), "未命名.json");

    /// <summary>Configuration files live directly in the current model's directory, never in sibling directories.</summary>
    public static string ValidatePath(string path, ModelProfile model, string root)
    {
        var directory = GetDirectory(model, root);
        if (!Path.IsPathFullyQualified(path)) throw OutsideDirectory(directory);
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(name), ".json", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
            throw OutsideDirectory(directory);

        // Do not allow links below the configured Runtime root to redirect a model's
        // configuration directory or a JSON file to another location.
        var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = full; !string.Equals(current, runtime, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw OutsideDirectory(directory);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static InvalidDataException OutsideDirectory(string directory) => new(
        "配置文件只能保存和载入当前模型配置目录中的 JSON 文件（不含子目录） / "
        + "Save and load JSON files only directly in this model's configuration directory:" + Environment.NewLine + directory);

    public static ModelProfile Bind(ModelProfile model, ModelProfile parameters) => parameters with
    {
        Id = model.Id,
        Alias = model.Alias,
        DisplayName = model.DisplayName,
        ModelRelativePath = model.ModelRelativePath,
        ModelType = model.ModelType,
        ModelTypeSource = model.ModelTypeSource,
        SourceKind = model.SourceKind,
        RemoteModelId = model.RemoteModelId,
        RemoteRepositoryId = model.RemoteRepositoryId,
        RemoteQuantization = model.RemoteQuantization,
        KnownSizeBytes = model.KnownSizeBytes,
        KnownShardCount = model.KnownShardCount,
        ShowInModePage = model.ShowInModePage,
        DisplayOrder = model.DisplayOrder,
        Host = model.Host,
        Port = model.Port,
        ConfigurationInitialized = model.ConfigurationInitialized,
        PreferredConfigurationPath = model.PreferredConfigurationPath,
        ResourceCatalogInitialized = model.ResourceCatalogInitialized,
        ConfigurationError = null,
    };

    public static Task<ModelProfile> ReadAsync(string path, ModelProfile model, string root, CancellationToken token = default) =>
        ReadCoreAsync(path, model, root, token, inspectReferences: false);

    /// <summary>Inspect references across saved types; never returns runnable or editable parameters.</summary>
    public static async Task<ModelConfigurationReferences> ReadReferencesAsync(string path, ModelProfile model, string root, CancellationToken token = default)
    {
        var profile = await ReadCoreAsync(path, model, root, token, inspectReferences: true).ConfigureAwait(false);
        return new(profile.ChatTemplateRelativePath,
            profile.MtpSource == MtpSourceKind.External ? profile.MtpDraftModelRelativePath : null,
            profile.VisionSource == VisionSourceKind.External ? profile.VisionProjectorRelativePath : null);
    }

    private static async Task<ModelProfile> ReadCoreAsync(string path, ModelProfile model, string root, CancellationToken token, bool inspectReferences)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("没有预选配置文件 / No configuration selected.");
        path = ValidatePath(path, model, root);
        await using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置文件过大 / Configuration is too large.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        using var json = JsonDocument.Parse(bytes);
        if (!json.RootElement.TryGetProperty("FormatVersion", out _)
            || !json.RootElement.TryGetProperty("Parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("配置文件缺少必要字段 / Incomplete configuration document.");
        foreach (var field in new[] { "ContextSize", "CompactionSafetyReserve", "GpuLayers", "FlashAttention", "CacheTypeK", "CacheTypeV", "Parallel", "Jinja", "ContextShiftEnabled", "IdleSleepSeconds", "ExtraArguments", "VisionEnabled", "MtpEnabled" })
            if (!parameters.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException($"配置参数缺少必要字段 / Missing configuration field: {field}");
        var document = JsonSerializer.Deserialize<ModelConfigurationDocument>(bytes, Options)
            ?? throw new InvalidDataException("配置文件为空 / Empty configuration.");
        if (document.FormatVersion != 1 || document.Parameters is null)
            throw new InvalidDataException("不支持的配置格式 / Unsupported configuration format.");
        if (document.ModelId != model.Id
            || !string.Equals(document.ModelRelativePath, model.ModelRelativePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("该配置文件不适用于当前模型 / Configuration does not belong to this model.");
        if (!Enum.IsDefined(document.Parameters.ModelType)) throw new InvalidDataException("配置模型类型无效 / Invalid configuration model type.");
        if (!inspectReferences && document.Parameters.ModelType != model.ModelType)
            throw new InvalidDataException(TypeMismatchPrefix + Environment.NewLine
                + $"配置类型：{document.Parameters.ModelType}；当前模型类型：{model.ModelType}。 / Configuration: {document.Parameters.ModelType}; current model: {model.ModelType}. "
                + (document.Parameters.ModelType == ModelType.Unknown
                    ? "此配置未指定类型，请为当前类型保存新配置。 / Save a new configuration for the current type."
                    : $"请改回 {document.Parameters.ModelType} 后载入。 / Switch back to {document.Parameters.ModelType} to load it."));
        var result = Bind(inspectReferences ? model with { ModelType = document.Parameters.ModelType } : model, document.Parameters);
        // Loading must allow repairing missing/stale resources in the editor. Preserve requested
        // switches, but validate paths and parameter structure without trusting stored proof flags.
        var structural = result with
        {
            // Keep an unavailable old template reference editable, without reading that file.
            // Saving and startup still require the full template directory validation.
            ChatTemplateRelativePath = null,
            VisionEnabled = false,
            VisionCapabilityStatus = VisionCapabilityStatus.Unknown,
            MtpEnabled = false,
            MtpCapabilityStatus = MtpCapabilityStatus.Unknown,
            ThinkingEnabled = null,
            ShowThinkingProcess = false,
            ExposeReasoningEffortInChatGpt = false,
            SupportsThinkingSwitch = null,
            ReasoningCapabilityStatus = ReasoningCapabilityStatus.Unknown,
            SupportedReasoningLevels = [],
            DefaultReasoningLevel = null,
            PreferredReasoningLevel = null,
        };
        var errors = ModelProfileValidator.Validate(structural, root, validateConversationLimits: false);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return result with { ConfigurationContentDigest = Convert.ToHexString(SHA256.HashData(bytes)) };
    }

    public static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static Task WriteAsync(string path, ModelProfile model, string root, CancellationToken token = default,
        bool requireExisting = false, string? expectedDigest = null) =>
        WriteCoreAsync(path, model, root, token, requireExisting, expectedDigest, preserveUnavailableTemplate: false);

    // Initial migration retains old references for repair; it never opens or uses the referenced template.
    internal static Task WriteInitialAsync(string path, ModelProfile model, string root, CancellationToken token) =>
        WriteCoreAsync(path, model, root, token, false, null, preserveUnavailableTemplate: true);

    private static async Task WriteCoreAsync(string path, ModelProfile model, string root, CancellationToken token,
        bool requireExisting, string? expectedDigest, bool preserveUnavailableTemplate)
    {
        path = ValidatePath(path, model, root);
        var errors = ModelProfileValidator.Validate(preserveUnavailableTemplate
            ? model with { ChatTemplateRelativePath = null, DefaultParameters = null } : model, root);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        if (requireExisting && !File.Exists(path)) throw new FileNotFoundException("当前配置已不存在，请重新载入或另存 / Reload or save as a new configuration.", path);
        if (expectedDigest is not null && Digest(path) != expectedDigest)
            throw new IOException("配置已被外部修改，请重新载入或另存 / Configuration changed outside the editor.");
        var clean = model with { ConfigurationInitialized = false, PreferredConfigurationPath = null, ConfigurationError = null, ResourceCatalogInitialized = false };
        var document = new ModelConfigurationDocument { ModelId = model.Id, ModelRelativePath = model.ModelRelativePath, Parameters = clean };
        await WriteAtomicallyAsync(path, JsonSerializer.Serialize(document, Options), token).ConfigureAwait(false);
    }

    public static async Task WriteAtomicallyAsync(string path, string content, CancellationToken token = default)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
