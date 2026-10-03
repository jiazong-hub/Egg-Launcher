using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public sealed record ChatTemplateValidationRecord(string Signature, string TemplateHash, DateTimeOffset CheckedAtUtc);

public static class ChatTemplateValidationCache
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Signature(ModelProfile profile, string root)
    {
        var model = new FileInfo(Path.Combine(root, profile.ModelRelativePath));
        var runtime = new FileInfo(Path.Combine(root, "llama-server.exe"));
        var template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(model.FullName) ?? string.Empty
            : File.ReadAllText(Path.Combine(root, profile.ChatTemplateRelativePath));
        return Hash($"{model.FullName}|{model.Length}|{model.LastWriteTimeUtc.Ticks}|{Hash(template)}|"
            + $"{runtime.Length}|{runtime.LastWriteTimeUtc.Ticks}|{ChatTemplateRules.VersionSignature}|{profile.Jinja}");
    }

    public static string PathFor(ModelProfile profile, string root) =>
        Path.Combine(root, "scripts", "templates", profile.Id + ".validation.json");

    public static bool IsCurrent(ModelProfile profile, string root)
    {
        try
        {
            var record = Read(profile, root);
            return record is not null && record.Signature == Signature(profile, root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static ChatTemplateValidationRecord? Read(ModelProfile profile, string root) =>
        File.Exists(PathFor(profile, root))
            ? JsonSerializer.Deserialize<ChatTemplateValidationRecord>(File.ReadAllText(PathFor(profile, root))) : null;

    public static async Task WriteAsync(ModelProfile profile, string root, CancellationToken cancellationToken)
    {
        var template = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(root, profile.ModelRelativePath)) ?? string.Empty
            : await File.ReadAllTextAsync(Path.Combine(root, profile.ChatTemplateRelativePath), cancellationToken);
        var record = new ChatTemplateValidationRecord(Signature(profile, root), Hash(template), DateTimeOffset.UtcNow);
        var path = PathFor(profile, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
