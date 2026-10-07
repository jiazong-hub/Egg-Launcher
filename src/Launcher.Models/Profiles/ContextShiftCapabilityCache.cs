using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Launcher.Models.Profiles;

public sealed record ContextShiftCapabilityResult(bool? Supported, string Reason, DateTimeOffset CheckedAtUtc);

/// <summary>Independent of parameter defaults. A result applies only to the exact load configuration.</summary>
public static class ContextShiftCapabilityCache
{
    public static string Fingerprint(ModelProfile profile, string runtimeRoot)
    {
        static string FileStamp(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return $"{file.FullName}|{file.Exists}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        }
        var parameters = ModelParameterDefaults.FromProfile(profile with
        {
            ContextShiftEnabled = false,
            CompactionSafetyReserve = 1024,
            ToolOutputTokenLimit = null,
            CodexStreamIdleTimeoutMinutes = null,
            ThinkingEnabled = null,
            ExposeReasoningEffortInChatGpt = false,
        });
        parameters = parameters with
        {
            ExtraArguments = parameters.ExtraArguments
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        var material = JsonSerializer.Serialize(new
        {
            Version = 1,
            Runtime = FileStamp(Path.Combine(runtimeRoot, "llama-server.exe")),
            RuntimeLibraries = Directory.Exists(runtimeRoot) ? Directory.GetFiles(runtimeRoot, "*.dll")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(FileStamp).ToArray() : [],
            Model = FileStamp(Path.Combine(runtimeRoot, profile.ModelRelativePath)),
            Draft = string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath) ? null
                : FileStamp(Path.Combine(runtimeRoot, profile.MtpDraftModelRelativePath)),
            Projector = string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath) ? null
                : FileStamp(Path.Combine(runtimeRoot, profile.VisionProjectorRelativePath)),
            Template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath) ? null
                : FileStamp(Path.Combine(runtimeRoot, profile.ChatTemplateRelativePath)),
            Parameters = parameters,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string CachePath(ModelProfile profile, string runtimeRoot) => Path.Combine(
        JsonModelProfileStore.GetProfilesDirectory(runtimeRoot), ".context-shift-cache", Fingerprint(profile, runtimeRoot) + ".json");

    public static ContextShiftCapabilityResult? Read(ModelProfile profile, string runtimeRoot)
    {
        try
        {
            var path = CachePath(profile, runtimeRoot);
            return File.Exists(path) ? JsonSerializer.Deserialize<ContextShiftCapabilityResult>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    public static void Write(ModelProfile profile, string runtimeRoot, ContextShiftCapabilityResult result)
    {
        var path = CachePath(profile, runtimeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(result), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
