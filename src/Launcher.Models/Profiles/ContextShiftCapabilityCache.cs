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
        static string Stamp(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return $"{file.FullName}|{file.Exists}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        }
        var probe = ContextShiftProbeProfile.Create(profile);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Version = 2,
            Runtime = Stamp(Path.Combine(runtimeRoot, "llama-server.exe")),
            Libraries = Directory.Exists(runtimeRoot) ? Directory.GetFiles(runtimeRoot, "*.dll").Order(StringComparer.OrdinalIgnoreCase).Select(Stamp).ToArray() : [],
            Model = Stamp(Path.Combine(runtimeRoot, probe.ModelRelativePath)),
            Draft = probe.MtpEnabled && probe.MtpDraftModelRelativePath is { } draft ? Stamp(Path.Combine(runtimeRoot, draft)) : null,
            Parameters = ModelParameterDefaults.FromProfile(probe),
        }))));
    }

    // Exact old calculation: only a matching old key can authorize migration, never a nearby configuration.
    public static string LegacyFingerprint(ModelProfile profile, string runtimeRoot)
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
            ShowThinkingProcess = false,
            PreferredReasoningLevel = null,
            ExposeReasoningEffortInChatGpt = false,
            // Controlled capability probes do not execute experimental arguments or n-gram.
            CustomParameters = new(),
            Ngram = new(),
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
            if (File.Exists(path)) return JsonSerializer.Deserialize<ContextShiftCapabilityResult>(File.ReadAllText(path));
            var legacy = Path.Combine(Path.GetDirectoryName(path)!, LegacyFingerprint(profile, runtimeRoot) + ".json");
            if (!File.Exists(legacy)) return null;
            var result = JsonSerializer.Deserialize<ContextShiftCapabilityResult>(File.ReadAllText(legacy));
            if (result is not null)
            {
                try { Write(profile, runtimeRoot, result); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return result;
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
