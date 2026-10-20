using System.Text.Json;
using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

/// <summary>A completed check explicitly rejected the asset, rather than being unavailable.</summary>
public sealed class ModelResourceValidationException(string message) : Exception(message);

public sealed record ModelResourceReference(ModelResourceKind Kind, string Path);
public sealed record ModelResourceDiscovery(IReadOnlyList<ModelResourceReference> References, IReadOnlyList<string> Warnings);

public static class ModelResourceRecheck
{
    /// <summary>Discover legacy references without importing any capability flags or writing JSON.</summary>
    public static async Task<ModelResourceDiscovery> DiscoverAsync(ModelProfile model, string root, CancellationToken token = default)
    {
        var directory = ModelConfigurationFile.GetDirectory(model, root);
        ModelConfigurationFile.ValidatePath(Path.Combine(directory, ".resource-check.json"), model, root);
        var references = new List<ModelResourceReference>();
        var warnings = new List<string>();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var found = await ModelConfigurationFile.ReadReferencesAsync(file, model, root, token).ConfigureAwait(false);
                    Add(ModelResourceKind.Template, found.Template);
                    Add(ModelResourceKind.Mtp, found.MtpFile);
                    Add(ModelResourceKind.Vision, found.VisionFile);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { warnings.Add(Path.GetFileName(file)); }
            }
        }
        return new(references, warnings);

        void Add(ModelResourceKind kind, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !references.Any(r => r.Kind == kind && ModelResourceManagement.SamePath(r.Path, path, root)))
                references.Add(new(kind, path));
        }
    }

    /// <summary>Revalidate in place. Only explicit rejection invalidates old proof; cancellation never does.</summary>
    public static async Task<bool> RunAsync(ModelProfile selection, string root, ModelResourceKind kind,
        Func<CancellationToken, Task<ModelProfile?>> validate, Func<bool> canMutate, CancellationToken token = default)
    {
        if (kind == ModelResourceKind.Mtp && (selection.MtpSource != MtpSourceKind.External
            || string.IsNullOrWhiteSpace(selection.MtpDraftModelRelativePath)))
            throw new InvalidOperationException("内置 MTP 沿用正式启动确认 / Embedded MTP is confirmed during normal startup.");
        EnsureAllowed();
        var before = Basis();
        ModelProfile? verified;
        try { verified = await validate(token); }
        catch (ModelResourceValidationException)
        {
            EnsureAllowed();
            // A changed source is already stale. Never reject evidence for a newly replaced source.
            if (before == Basis()) await RejectAsync(selection, root, kind, canMutate, token);
            throw;
        }
        if (verified is null) return false;
        EnsureAllowed();
        if (before != Basis()) throw new InvalidOperationException("检测期间文件或运行库已改变，请重新检测 / Validation source changed; check again.");
        if (verified.Id != selection.Id || !ModelResourceManagement.SamePath(verified.ModelRelativePath, selection.ModelRelativePath, root)
            || !ModelResourceManagement.SamePath(new ModelResourceRecord(kind, verified).ResourcePath,
                new ModelResourceRecord(kind, selection).ResourcePath, root)
            || kind == ModelResourceKind.Mtp && verified.MtpSource != selection.MtpSource
            || kind == ModelResourceKind.Vision && verified.VisionSource != selection.VisionSource
            || kind == ModelResourceKind.Template && verified.Jinja != selection.Jinja)
            throw new InvalidDataException("重新检测不得更换资源 / Rechecking cannot replace the selected resource.");
        await ModelResourceCatalog.RegisterAsync(verified, root, kind, token);
        return true;

        void EnsureAllowed()
        {
            token.ThrowIfCancellationRequested();
            if (!canMutate()) throw new InvalidOperationException("请停止本地服务和检测后重试 / Stop local services and detection first.");
        }
        string Basis() => kind switch
        {
            ModelResourceKind.Template => ChatTemplateValidationCache.Signature(selection, root),
            ModelResourceKind.Mtp => MtpValidationFingerprint.Compute(selection, root),
            ModelResourceKind.Vision => VisionValidationFingerprint.Compute(selection, root),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static async Task RejectAsync(ModelProfile selection, string root, ModelResourceKind kind,
        Func<bool> canMutate, CancellationToken token)
    {
        var path = ModelResourceCatalog.FilePath(selection, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var gate = await ModelResourceCatalog.AcquireAsync(path + ".lock", token);
        if (!canMutate()) throw new InvalidOperationException("Local service state changed during validation.");
        var previous = ModelResourceCatalog.Read(selection, root);
        var resourcePath = new ModelResourceRecord(kind, selection).ResourcePath;
        var records = previous.Records.Select(record => record.Kind != kind
            || !ModelResourceManagement.SamePath(record.ResourcePath, resourcePath, root) ? record : kind switch
            {
                ModelResourceKind.Template => record with { TemplateSignature = null, ValidationRejected = true },
                ModelResourceKind.Mtp => record with { Evidence = record.Evidence with { MtpValidationSignature = null }, ValidationRejected = true },
                ModelResourceKind.Vision => record with { Evidence = record.Evidence with { VisionValidationSignature = null }, ValidationRejected = true },
                _ => record,
            }).ToArray();
        // Embedded templates normally need no saved proof. Remember an explicit rendering rejection
        // so startup cannot silently fall back to an embedded template known to have failed.
        if (kind == ModelResourceKind.Template && resourcePath is null
            && !records.Any(record => record.Kind == kind && record.ResourcePath is null))
            records = [.. records, new(kind, selection, ValidationRejected: true)];
        await ModelConfigurationFile.WriteAtomicallyAsync(path, JsonSerializer.Serialize(previous with { Records = records },
            new JsonSerializerOptions { WriteIndented = true }), token);
        if (kind == ModelResourceKind.Template)
        {
            try
            {
                var cache = ChatTemplateValidationCache.Read(selection, root);
                if (cache?.Signature == ChatTemplateValidationCache.Signature(selection, root))
                    File.Delete(ChatTemplateValidationCache.PathFor(selection, root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The catalog proof is already invalidated. An unreadable legacy cache cannot restore it.
            }
        }
    }
}
