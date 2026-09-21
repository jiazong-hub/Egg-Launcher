using System.Security.Cryptography;
using System.Text;

namespace Launcher.Models.Profiles;

public static class MtpValidationFingerprint
{
    public static string Compute(ModelProfile profile, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var root = Path.GetFullPath(runtimeRoot);
        var modelSignature = FileSignature(Path.Combine(root, profile.ModelRelativePath));
        var draftSignature = profile.MtpSource == MtpSourceKind.External
                             && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath)
            ? FileSignature(Path.Combine(root, profile.MtpDraftModelRelativePath))
            : "embedded";
        var runtimeSignature = FileSignature(Path.Combine(root, "llama-server.exe"));
        var material = string.Join("|", modelSignature, draftSignature, runtimeSignature, profile.MtpSource);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static bool IsCurrent(ModelProfile profile, string runtimeRoot)
    {
        if (profile.MtpCapabilityStatus != MtpCapabilityStatus.Verified
            || string.IsNullOrWhiteSpace(profile.MtpValidationSignature)
            || profile.MtpValidatedAtUtc is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                profile.MtpValidationSignature,
                Compute(profile, runtimeRoot),
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    public static bool HasUsableSource(ModelProfile profile) =>
        profile.MtpSource == MtpSourceKind.External
            ? profile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
              && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath)
            : profile.MtpCapabilityStatus is MtpCapabilityStatus.EmbeddedCandidate
                or MtpCapabilityStatus.Verified;

    private static string FileSignature(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        return info.Exists
            ? $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
            : $"{fullPath}|missing";
    }
}
