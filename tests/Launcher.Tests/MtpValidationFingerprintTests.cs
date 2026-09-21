using Launcher.Models.Profiles;

namespace Launcher.Tests;

public sealed class MtpValidationFingerprintTests : IDisposable
{
    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        $"egg-launcher-mtp-fingerprint-{Guid.NewGuid():N}");

    [Fact]
    public void IsCurrent_WhenValidatedFilesAreUnchanged_ReturnsTrue()
    {
        var profile = CreateVerifiedExternalProfile();

        Assert.True(MtpValidationFingerprint.IsCurrent(profile, _runtimeRoot));
        Assert.True(MtpValidationFingerprint.HasUsableSource(profile));
    }

    [Fact]
    public void IsCurrent_WhenExternalCompanionIsDeleted_ReturnsFalse()
    {
        var profile = CreateVerifiedExternalProfile();
        File.Delete(Path.Combine(_runtimeRoot, profile.MtpDraftModelRelativePath!));

        Assert.False(MtpValidationFingerprint.IsCurrent(profile, _runtimeRoot));
    }

    [Theory]
    [InlineData(MtpSourceKind.Embedded, MtpCapabilityStatus.EmbeddedCandidate, true)]
    [InlineData(MtpSourceKind.Embedded, MtpCapabilityStatus.Verified, true)]
    [InlineData(MtpSourceKind.Embedded, MtpCapabilityStatus.Unknown, false)]
    [InlineData(MtpSourceKind.External, MtpCapabilityStatus.ExternalConfigured, false)]
    [InlineData(MtpSourceKind.External, MtpCapabilityStatus.ValidationFailed, false)]
    public void HasUsableSource_ReflectsCapabilityRatherThanEnabledState(
        MtpSourceKind source,
        MtpCapabilityStatus status,
        bool expected)
    {
        var profile = CreateProfile() with
        {
            MtpEnabled = false,
            MtpSource = source,
            MtpCapabilityStatus = status,
            MtpDraftModelRelativePath = source == MtpSourceKind.External ? @"models\draft.gguf" : null,
        };

        Assert.Equal(expected, MtpValidationFingerprint.HasUsableSource(profile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }
    }

    private ModelProfile CreateVerifiedExternalProfile()
    {
        Directory.CreateDirectory(Path.Combine(_runtimeRoot, "models"));
        File.WriteAllText(Path.Combine(_runtimeRoot, "llama-server.exe"), "runtime");
        File.WriteAllText(Path.Combine(_runtimeRoot, "models", "main.gguf"), "main");
        File.WriteAllText(Path.Combine(_runtimeRoot, "models", "draft.gguf"), "draft");

        var profile = CreateProfile() with
        {
            MtpSource = MtpSourceKind.External,
            MtpCapabilityStatus = MtpCapabilityStatus.Verified,
            MtpDraftModelRelativePath = @"models\draft.gguf",
            MtpValidationSignature = "pending",
            MtpValidatedAtUtc = DateTimeOffset.UtcNow,
        };
        return profile with
        {
            MtpValidationSignature = MtpValidationFingerprint.Compute(profile, _runtimeRoot),
        };
    }

    private static ModelProfile CreateProfile() => new()
    {
        Id = "main",
        DisplayName = "Main",
        ModelRelativePath = @"models\main.gguf",
        Alias = "main",
        ContextSize = 32_768,
    };
}
