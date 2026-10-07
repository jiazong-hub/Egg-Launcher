using System.Text.Json;
using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public static class ReasoningValidationFingerprint
{
    // Only known resource/performance options are excluded. Unknown options remain proof dependencies.
    private static readonly HashSet<string> IndependentArguments = new(StringComparer.Ordinal)
    {
        "fit", "fit-target", "fit-ctx", "threads", "threads-batch", "load-mode", "lazy-mode",
        "n-cpu-ffn", "split-mode", "tensor-split", "main-gpu", "numa", "override-tensor",
        "kv-offload", "no-kv-offload", "repack", "no-repack", "op-offload", "no-op-offload",
        "no-host", "ctx-checkpoints", "swa-checkpoints", "keep", "cpu-moe", "n-cpu-moe",
    };

    public static string Compute(ModelProfile profile, string root) =>
        ChatTemplateValidationCache.Hash(JsonSerializer.Serialize(new { Version = 5, Basis = CaptureBasis(profile, root) }));

    public static IReadOnlyDictionary<string, string> CaptureBasis(ModelProfile profile, string root)
    {
        static string Stamp(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return $"{file.FullName}|{file.Exists}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        }
        static string Hash(object? value) => ChatTemplateValidationCache.Hash(JsonSerializer.Serialize(value));
        var template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(root, profile.ModelRelativePath)) ?? ""
            : File.ReadAllText(Path.Combine(root, profile.ChatTemplateRelativePath));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = Hash(Stamp(Path.Combine(root, profile.ModelRelativePath))),
            ["template"] = Hash(new { template, profile.ChatTemplateRelativePath, profile.Jinja }),
            ["runtime"] = Hash(Stamp(Path.Combine(root, "llama-server.exe"))),
            ["libraries"] = Hash(Directory.Exists(root) ? Directory.GetFiles(root, "*.dll").Order(StringComparer.OrdinalIgnoreCase).Select(Stamp).ToArray() : []),
            ["client"] = Hash(profile.ReasoningClientExecutablePath is { } client ? Stamp(client) : null),
            ["arguments"] = Hash(profile.ExtraArguments.Where(pair => !IndependentArguments.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()),
        };
    }

    // Exact v4 calculation, accepted only when it still matches the unmodified original profile.
    public static string ComputeLegacy(ModelProfile profile, string root)
    {
        static string Stamp(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            return $"{file.FullName}|{file.Exists}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}";
        }
        var template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(root, profile.ModelRelativePath)) ?? ""
            : File.ReadAllText(Path.Combine(root, profile.ChatTemplateRelativePath));
        return ChatTemplateValidationCache.Hash(JsonSerializer.Serialize(new
        {
            Version = 4,
            Client = profile.ReasoningClientExecutablePath is { } client ? Stamp(client) : null,
            Model = Stamp(Path.Combine(root, profile.ModelRelativePath)),
            Template = ChatTemplateValidationCache.Hash(template),
            profile.ChatTemplateRelativePath,
            profile.Jinja,
            Runtime = Stamp(Path.Combine(root, "llama-server.exe")),
            Libraries = Directory.Exists(root) ? Directory.GetFiles(root, "*.dll").Order(StringComparer.OrdinalIgnoreCase).Select(Stamp).ToArray() : [],
            Arguments = profile.ExtraArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
        }));
    }
}
