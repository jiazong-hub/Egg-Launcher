using System.Text.Json;
using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public static class ReasoningValidationFingerprint
{
    public static string Compute(ModelProfile profile, string root)
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
