using Launcher.Models.Profiles;

namespace Launcher.Tests;

public sealed class VisionValidationFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launcher-vision-fingerprint", Guid.NewGuid().ToString("N"));

    [Fact]
    public void IsCurrent_ChangesWhenProjectorChanges()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllBytes(Path.Combine(_root, "models", "model.gguf"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "models", "mmproj.gguf"), [2]);
        File.WriteAllBytes(Path.Combine(_root, "llama-server.exe"), [3]);
        var profile = new ModelProfile
        {
            Id = "vision",
            DisplayName = "Vision",
            ModelRelativePath = @"models\model.gguf",
            Alias = "vision",
            ContextSize = 8192,
            VisionSource = VisionSourceKind.External,
            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
            VisionProjectorRelativePath = @"models\mmproj.gguf",
            VisionValidationSignature = "pending",
            VisionValidatedAtUtc = DateTimeOffset.UtcNow,
        };
        profile = profile with { VisionValidationSignature = VisionValidationFingerprint.Compute(profile, _root) };
        Assert.True(VisionValidationFingerprint.IsCurrent(profile, _root));

        using (var stream = new FileStream(Path.Combine(_root, "models", "mmproj.gguf"), FileMode.Append, FileAccess.Write))
        {
            stream.WriteByte(9);
        }
        Assert.False(VisionValidationFingerprint.IsCurrent(profile, _root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
