using System.Text;
using Launcher.Models.Scanning;

namespace Launcher.Models.Profiles;

public static class ModelProfileFactory
{
    public static ModelProfile RestoreModelDefaults(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.DefaultParameters?.ApplyTo(profile) ?? profile;
    }

    public static ModelProfile SaveCurrentParametersAsDefault(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { DefaultParameters = ModelParameterDefaults.FromProfile(profile) };
    }

    public static ModelProfile CreateDefault(
        GgufModelCandidate candidate,
        string runtimeRoot,
        IEnumerable<ModelProfile>? existingProfiles = null,
        bool detectModelType = true)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var root = Path.GetFullPath(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var modelPath = Path.GetFullPath(candidate.PrimaryPath);
        if (!modelPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("扫描到的模型不在 Runtime Root 内。", nameof(candidate));
        }

        var existing = (existingProfiles ?? Array.Empty<ModelProfile>()).ToArray();
        var baseIdentifier = MakeIdentifier(candidate.DisplayName);
        var identifier = baseIdentifier;
        for (var suffix = 2;
             existing.Any(profile => string.Equals(profile.Id, identifier, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(profile.Alias, identifier, StringComparison.OrdinalIgnoreCase));
             suffix++)
        {
            identifier = $"{baseIdentifier}-{suffix}";
        }

        var metadata = detectModelType ? TryReadMetadata(modelPath) : null;
        var detectedModelType = metadata?.DetectModelType() ?? ModelType.Unknown;
        var (modelType, modelTypeSource) = detectModelType
            ? (detectedModelType, ModelTypeSource.Detected)
            : (ModelType.Unknown, ModelTypeSource.Legacy);
        return new ModelProfile
        {
            Id = identifier,
            DisplayName = candidate.DisplayName,
            ModelRelativePath = Path.GetRelativePath(root, modelPath),
            Alias = identifier,
            SourceKind = string.IsNullOrWhiteSpace(candidate.RemoteModelId)
                ? ModelSourceKind.LocalFile
                : ModelSourceKind.LlamaCache,
            RemoteModelId = candidate.RemoteModelId,
            RemoteRepositoryId = candidate.RemoteRepositoryId,
            RemoteQuantization = candidate.RemoteQuantization,
            KnownSizeBytes = candidate.TotalSizeBytes > 0 ? candidate.TotalSizeBytes : null,
            KnownShardCount = candidate.ShardCount > 0 ? candidate.ShardCount : null,
            ModelType = modelType,
            ModelTypeSource = modelTypeSource,
            MtpCapabilityStatus = metadata?.HasEmbeddedMtp == true
                ? MtpCapabilityStatus.EmbeddedCandidate
                : MtpCapabilityStatus.Unknown,
            VisionCapabilityStatus = metadata?.HasVisionEncoder == true
                ? VisionCapabilityStatus.BuiltInCandidate
                : VisionCapabilityStatus.Unknown,
            ContextSize = 16_384,
            CompactionSafetyReserve = ModelProfile.DefaultCompactionSafetyReserve,
        };
    }

    public static ModelProfile RecreateForModelType(ModelProfile profile, ModelType modelType)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (modelType == ModelType.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(modelType));
        }

        return new ModelProfile
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            ModelRelativePath = profile.ModelRelativePath,
            SourceKind = profile.SourceKind,
            RemoteModelId = profile.RemoteModelId,
            RemoteRepositoryId = profile.RemoteRepositoryId,
            RemoteQuantization = profile.RemoteQuantization,
            KnownSizeBytes = profile.KnownSizeBytes,
            KnownShardCount = profile.KnownShardCount,
            Alias = profile.Alias,
            ShowInModePage = profile.ShowInModePage,
            DisplayOrder = profile.DisplayOrder,
            Host = profile.Host,
            Port = profile.Port,
            ModelType = modelType,
            ModelTypeSource = ModelTypeSource.UserSelected,
            ContextSize = 16_384,
            CompactionSafetyReserve = ModelProfile.DefaultCompactionSafetyReserve,
            ChatTemplateRelativePath = profile.ChatTemplateRelativePath,
        };
    }

    private static GgufModelMetadata? TryReadMetadata(string modelPath)
    {
        try
        {
            return GgufContextMetadataReader.ReadMetadata(modelPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or NotSupportedException
                                          or OverflowException)
        {
            return null;
        }
    }

    private static string MakeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value)
        {
            var normalized = char.ToLowerInvariant(character);
            if (char.IsAsciiLetterOrDigit(normalized) || normalized is '.' or '_')
            {
                builder.Append(normalized);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        var identifier = builder.ToString().Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(identifier) ? "local-model" : identifier;
    }
}
