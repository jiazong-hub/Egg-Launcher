using System.Text.Json;
using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public enum ModelResourceKind { Template, Vision, Mtp, Reasoning }

public sealed record ModelResourceRecord(ModelResourceKind Kind, ModelProfile Evidence, string? TemplateSignature = null, bool ValidationRejected = false)
{
    public string? ResourcePath => Kind switch
    {
        ModelResourceKind.Template or ModelResourceKind.Reasoning => Evidence.ChatTemplateRelativePath,
        ModelResourceKind.Vision => Evidence.VisionProjectorRelativePath,
        _ => Evidence.MtpDraftModelRelativePath,
    };
    public string Label => ResourcePath is null ? "内置 / Built-in" : Path.GetFileName(ResourcePath);
}

public sealed record ModelResourceDocument(int FormatVersion, string ModelId, IReadOnlyList<ModelResourceRecord> Records,
    string? DefaultTemplatePath = null);

/// <summary>Model-owned compatibility evidence, shared by all parameter documents.</summary>
public static class ModelResourceCatalog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string FilePath(ModelProfile model, string root) => Path.Combine(root, "scripts", "model-resources", model.Id + ".json");

    public static ModelProfile ApplyDefaultTemplate(ModelProfile newModel, string root)
    {
        var catalog = Read(newModel, root);
        return catalog.DefaultTemplatePath is { } path && catalog.Records.Any(r => r.Kind == ModelResourceKind.Template
            && ModelResourceManagement.SamePath(r.ResourcePath, path, root) && IsCurrent(r, root))
            ? newModel with { ChatTemplateRelativePath = path, Jinja = true }
            : newModel;
    }

    public static ModelResourceDocument Read(ModelProfile model, string root)
    {
        var path = FilePath(model, root);
        if (!File.Exists(path)) return new(1, model.Id, []);
        var result = JsonSerializer.Deserialize<ModelResourceDocument>(File.ReadAllText(path), Options);
        if (result is null || result.FormatVersion != 1 || result.ModelId != model.Id || result.Records is null
            || result.Records.Any(r => r is null || r.Evidence is null || r.Evidence.Id != model.Id || !Enum.IsDefined(r.Kind)))
            throw new InvalidDataException("模型资源记录格式无效 / Invalid model resource catalog.");
        // A removed model can later reuse a friendly ID. Evidence still belongs to the exact main-file binding.
        var bound = result.Records.Where(r => string.Equals(
            Path.GetFullPath(Path.Combine(root, r.Evidence.ModelRelativePath)),
            Path.GetFullPath(Path.Combine(root, model.ModelRelativePath)), StringComparison.OrdinalIgnoreCase)).ToArray();
        return result with { Records = bound, DefaultTemplatePath = bound.Any(r => r.Kind == ModelResourceKind.Template && r.ResourcePath == result.DefaultTemplatePath) ? result.DefaultTemplatePath : null };
    }

    public static bool IsCurrent(ModelResourceRecord record, string root)
    {
        if (record.ValidationRejected) return false;
        try
        {
            if (record.Kind is ModelResourceKind.Template or ModelResourceKind.Reasoning
                && !string.IsNullOrWhiteSpace(record.Evidence.ChatTemplateRelativePath))
                ChatTemplateFile.ResolveRelative(record.Evidence.ChatTemplateRelativePath, root);
            return record.Kind switch
            {
                ModelResourceKind.Mtp => MtpValidationFingerprint.IsCurrent(record.Evidence, root),
                ModelResourceKind.Vision => VisionValidationFingerprint.IsCurrent(record.Evidence, root),
                ModelResourceKind.Template => record.TemplateSignature == TemplateSignature(record.Evidence, root),
                _ => ReasoningValidationState.Check(record.Evidence, root).State == ReasoningValidationStateKind.Current,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException) { return false; }
    }

    // The explicit path is part of the proof: renaming an identical template still requires checking.
    private static string TemplateSignature(ModelProfile profile, string root) => ChatTemplateValidationCache.Hash(
        (profile.ChatTemplateRelativePath is { } path ? ChatTemplateFile.ResolveRelative(path, root) : "embedded")
        + "|" + ChatTemplateValidationCache.Signature(profile, root));

    public static async Task RegisterAsync(ModelProfile evidence, string root, ModelResourceKind kind,
        CancellationToken token = default, bool defaultTemplate = false)
    {
        evidence = evidence with { ConfigurationInitialized = false, PreferredConfigurationPath = null, ConfigurationError = null };
        var record = new ModelResourceRecord(kind, evidence,
            kind == ModelResourceKind.Template ? TemplateSignature(evidence, root) : null);
        if (!IsCurrent(record, root)) throw new InvalidDataException("检测结果无效，未登记资源 / Invalid evidence; resource not registered.");
        var path = FilePath(evidence, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var gate = await AcquireAsync(path + ".lock", token).ConfigureAwait(false);
        var previous = Read(evidence, root);
        var records = previous.Records.Where(r => r.Kind != kind
            || !ModelResourceManagement.SamePath(r.ResourcePath, record.ResourcePath, root)
            || (ProofSignature(r) != ProofSignature(record) && IsCurrent(r, root))).Append(record).ToArray();
        var next = previous with { Records = records, DefaultTemplatePath = defaultTemplate ? evidence.ChatTemplateRelativePath : previous.DefaultTemplatePath };
        await ModelConfigurationFile.WriteAtomicallyAsync(path, JsonSerializer.Serialize(next, Options), token).ConfigureAwait(false);
    }

    public static async Task EnsureMigratedAsync(ModelProfile model, string root, CancellationToken token = default)
    {
        await UpgradeCurrentEvidenceAsync(model, root, token).ConfigureAwait(false);
        var path = Path.Combine(JsonModelProfileStore.GetProfilesDirectory(root), model.Id + ".json");
        if (!File.Exists(path)) return;
        var legacy = JsonSerializer.Deserialize<ModelProfile>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
        if (legacy is null) return;
        if (!File.Exists(FilePath(model, root)))
        {
            // Import trusted pre-upgrade evidence once. A deleted catalog must not be rebuilt from imported JSON flags.
            if (!legacy.ResourceCatalogInitialized)
            {
                foreach (var kind in Enum.GetValues<ModelResourceKind>())
                {
                    if (kind == ModelResourceKind.Template && !ChatTemplateValidationCache.IsCurrent(legacy, root)) continue;
                    var record = new ModelResourceRecord(kind, legacy, kind == ModelResourceKind.Template ? TemplateSignature(legacy, root) : null);
                    if (IsCurrent(record, root)) await RegisterAsync(legacy, root, kind, token).ConfigureAwait(false);
                }
            }
            if (!File.Exists(FilePath(model, root)))
                await ModelConfigurationFile.WriteAtomicallyAsync(FilePath(model, root), JsonSerializer.Serialize(new ModelResourceDocument(1, model.Id, []), Options), token).ConfigureAwait(false);
        }
        if (!legacy.ResourceCatalogInitialized)
            await new JsonModelProfileStore().SaveRegistrationAsync(root, legacy with { ResourceCatalogInitialized = true }, token).ConfigureAwait(false);
        await UpgradeCurrentEvidenceAsync(model, root, token).ConfigureAwait(false);
    }

    private static async Task UpgradeCurrentEvidenceAsync(ModelProfile model, string root, CancellationToken token)
    {
        var path = FilePath(model, root);
        if (!File.Exists(path)) return;
        // Avoid even creating a lock file when there is no exact, verifiable legacy proof to upgrade.
        if (!Read(model, root).Records.Any(record => IsCurrent(record, root) && NeedsUpgrade(record))) return;
        await using var gate = await AcquireAsync(path + ".lock", token).ConfigureAwait(false);
        var previous = Read(model, root);
        var changed = false;
        var records = previous.Records.Select(record =>
        {
            if (!IsCurrent(record, root)) return record;
            var evidence = record.Evidence;
            if (record.Kind == ModelResourceKind.Vision)
                evidence = evidence with { VisionValidationSignature = VisionValidationFingerprint.Compute(evidence, root) };
            else if (record.Kind == ModelResourceKind.Reasoning)
                evidence = ReasoningValidationState.Check(evidence, root).ApplyTo(evidence);
            if (evidence == record.Evidence) return record;
            changed = true;
            return record with { Evidence = evidence };
        }).ToArray();
        if (changed)
            await ModelConfigurationFile.WriteAtomicallyAsync(path, JsonSerializer.Serialize(previous with { Records = records }, Options), token).ConfigureAwait(false);

        bool NeedsUpgrade(ModelResourceRecord record) => record.Kind switch
        {
            ModelResourceKind.Vision => record.Evidence.VisionValidationSignature != VisionValidationFingerprint.Compute(record.Evidence, root),
            ModelResourceKind.Reasoning => record.Evidence.ReasoningCapabilitySignature != ReasoningValidationFingerprint.Compute(record.Evidence, root),
            _ => false,
        };
    }

    private static string? ProofSignature(ModelResourceRecord record) => record.Kind switch
    {
        ModelResourceKind.Mtp => record.Evidence.MtpValidationSignature,
        ModelResourceKind.Vision => record.Evidence.VisionValidationSignature,
        ModelResourceKind.Template => record.TemplateSignature,
        _ => record.Evidence.ReasoningCapabilitySignature,
    };

    public static ModelProfile Resolve(ModelProfile candidate, string root)
    {
        var records = Read(candidate, root).Records;
        var embeddedMtp = false;
        if (candidate.MtpSource == MtpSourceKind.Embedded)
        {
            try { embeddedMtp = Launcher.Models.Scanning.GgufContextMetadataReader.ReadMetadata(Path.Combine(root, candidate.ModelRelativePath)).HasEmbeddedMtp; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        var result = candidate with
        {
            MtpCapabilityStatus = embeddedMtp ? MtpCapabilityStatus.EmbeddedCandidate : MtpCapabilityStatus.Unknown,
            MtpValidationSignature = null,
            MtpValidatedAtUtc = null,
            VisionCapabilityStatus = VisionCapabilityStatus.Unknown,
            VisionValidationSignature = null,
            VisionValidatedAtUtc = null,
            SupportsThinkingSwitch = null,
            DefaultThinkingEnabled = null,
            ReasoningCapabilityStatus = ReasoningCapabilityStatus.Unknown,
            SupportedReasoningLevels = [],
            DefaultReasoningLevel = null,
            ReasoningResponsesVerified = false,
            ReasoningClientCompatible = null,
            ReasoningClientExecutablePath = null,
            ReasoningLevelAliases = new Dictionary<string, string>(),
            ReasoningCapabilitySignature = null,
            ReasoningCapabilityCheckedAtUtc = null,
            ReasoningValidationBasis = null,
        };
        foreach (var record in records.Where(r => r.Evidence.Id == candidate.Id && IsCurrent(r, root)))
        {
            var proof = record.Evidence;
            if (record.Kind == ModelResourceKind.Mtp && proof.MtpSource == candidate.MtpSource && ModelResourceManagement.SamePath(proof.MtpDraftModelRelativePath, candidate.MtpDraftModelRelativePath, root))
            {
                // Existing proofs include the path spelling. Transfer a current proof to an
                // equivalent selected path, and recheck the original evidence before granting it.
                var signature = MtpValidationFingerprint.Compute(result, root);
                if (IsCurrent(record, root))
                    result = result with { MtpCapabilityStatus = proof.MtpCapabilityStatus, MtpValidationSignature = signature, MtpValidatedAtUtc = proof.MtpValidatedAtUtc };
            }
            if (record.Kind == ModelResourceKind.Vision && proof.VisionSource == candidate.VisionSource && ModelResourceManagement.SamePath(proof.VisionProjectorRelativePath, candidate.VisionProjectorRelativePath, root))
            {
                // The existing vision check recognizes GGUF metadata, not chat-template rendering.
                // Reuse only a still-current original proof, then bind its signature to this JSON's settings.
                var signature = VisionValidationFingerprint.Compute(result, root);
                if (IsCurrent(record, root))
                    result = result with { VisionCapabilityStatus = proof.VisionCapabilityStatus, VisionValidationSignature = signature, VisionValidatedAtUtc = proof.VisionValidatedAtUtc };
            }
            if (record.Kind != ModelResourceKind.Reasoning) continue;
            var reasoning = result with
            {
                SupportsThinkingSwitch = proof.SupportsThinkingSwitch,
                DefaultThinkingEnabled = proof.DefaultThinkingEnabled,
                ReasoningCapabilityStatus = proof.ReasoningCapabilityStatus,
                SupportedReasoningLevels = proof.SupportedReasoningLevels,
                DefaultReasoningLevel = proof.DefaultReasoningLevel,
                ReasoningResponsesVerified = proof.ReasoningResponsesVerified,
                ReasoningClientCompatible = proof.ReasoningClientCompatible,
                ReasoningClientExecutablePath = proof.ReasoningClientExecutablePath,
                ReasoningLevelAliases = proof.ReasoningLevelAliases,
                ReasoningCapabilitySignature = proof.ReasoningCapabilitySignature,
                ReasoningCapabilityCheckedAtUtc = proof.ReasoningCapabilityCheckedAtUtc,
                ReasoningValidationBasis = proof.ReasoningValidationBasis,
                ReasoningValidationDetails = proof.ReasoningValidationDetails,
            };
            if (ReasoningValidationState.Check(reasoning, root).State == ReasoningValidationStateKind.Current) result = reasoning;
        }
        return result;
    }

    internal static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
    {
        for (var i = 0; ; i++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 100) { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }
}
