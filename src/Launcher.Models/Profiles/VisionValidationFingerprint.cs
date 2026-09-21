using System.Security.Cryptography;
using System.Text;

namespace Launcher.Models.Profiles;

public static class VisionValidationFingerprint
{
    public static string Compute(ModelProfile profile, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var root = Path.GetFullPath(runtimeRoot);
        var model = FileSignature(Path.Combine(root, profile.ModelRelativePath));
        var projector = profile.VisionSource == VisionSourceKind.External
                        && !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath)
            ? FileSignature(Path.Combine(root, profile.VisionProjectorRelativePath))
            : "built-in";
        var runtime = FileSignature(Path.Combine(root, "llama-server.exe"));
        var mtmd = Directory.Exists(root)
            ? string.Join(",", Directory.EnumerateFiles(root, "*mtmd*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(FileSignature))
            : "missing";
        var template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? "default"
            : FileSignature(Path.Combine(root, profile.ChatTemplateRelativePath));
        var material = string.Join("|", model, projector, runtime, mtmd, template, profile.VisionSource);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static bool IsCurrent(ModelProfile profile, string runtimeRoot) =>
        profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
        && profile.VisionValidatedAtUtc is not null
        && !string.IsNullOrWhiteSpace(profile.VisionValidationSignature)
        && string.Equals(
            profile.VisionValidationSignature,
            Compute(profile, runtimeRoot),
            StringComparison.OrdinalIgnoreCase);

    public static bool HasUsableSource(ModelProfile profile) =>
        profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
        && (profile.VisionSource != VisionSourceKind.External
            || !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath));

    private static string FileSignature(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return "missing:" + fullPath;
        }

        var info = new FileInfo(fullPath);
        return $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }
}
