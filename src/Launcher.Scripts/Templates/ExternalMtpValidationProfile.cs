using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public static class ExternalMtpValidationProfile
{
    public static ModelProfile Create(ModelProfile source, string companionRelativePath) => TemplateValidationProfile.Create(source) with
    {
        // The existing MTP probe uses raw /completion, not chat-template rendering.
        Jinja = false,
        ChatTemplateRelativePath = null,
        MtpEnabled = true,
        MtpSource = MtpSourceKind.External,
        MtpCapabilityStatus = MtpCapabilityStatus.Verified,
        MtpDraftModelRelativePath = companionRelativePath,
        MtpDraftGpuLayers = source.MtpDraftGpuLayers,
        MtpDraftDevice = source.MtpDraftDevice,
        MtpDraftCacheTypeK = source.MtpDraftCacheTypeK,
        MtpDraftCacheTypeV = source.MtpDraftCacheTypeV,
        MtpDraftThreads = source.MtpDraftThreads,
        MtpDraftBatchThreads = source.MtpDraftBatchThreads,
        MtpValidationSignature = "VALIDATION-PENDING",
        MtpValidatedAtUtc = DateTimeOffset.UtcNow,
    };
}
