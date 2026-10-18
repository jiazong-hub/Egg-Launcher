using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

/// <summary>Resource identity and registration changes; never deletes a source asset.</summary>
public static class ModelResourceManagement
{
    public static bool SamePath(string? left, string? right, string root) =>
        left is null || right is null ? left is null && right is null : string.Equals(
            Path.GetFullPath(Path.Combine(root, left)), Path.GetFullPath(Path.Combine(root, right)), StringComparison.OrdinalIgnoreCase);

    public static ModelResourceRecord? FindReusable(ModelProfile selection, string root, ModelResourceKind kind) =>
        ModelResourceCatalog.Read(selection, root).Records.FirstOrDefault(record => record.Kind == kind
            && SamePath(record.ResourcePath, new ModelResourceRecord(kind, selection).ResourcePath, root)
            && ModelResourceCatalog.IsCurrent(record, root)
            && (kind != ModelResourceKind.Vision || (record.Evidence.VisionSource == selection.VisionSource
                && SamePath(record.Evidence.ChatTemplateRelativePath, selection.ChatTemplateRelativePath, root)))
            && (kind != ModelResourceKind.Mtp || record.Evidence.MtpSource == selection.MtpSource)
            && (kind != ModelResourceKind.Template || record.Evidence.Jinja == selection.Jinja));

    public static ModelResourceRecord? FindGeneratedTemplate(ModelProfile model, string root)
    {
        var source = CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(root, model.ModelRelativePath));
        if (!CodexChatTemplateCompatibility.TryCreateCompatibleTemplate(source, out var repaired)) return null;
        var expected = CodexChatTemplateCompatibility.CreateOwnedOutput(source!, repaired);
        return ModelResourceCatalog.Read(model, root).Records.FirstOrDefault(record => record.Kind == ModelResourceKind.Template
            && record.ResourcePath is not null && ModelResourceCatalog.IsCurrent(record, root)
            && File.ReadAllText(ChatTemplateFile.ResolveRelative(record.ResourcePath, root)) == expected);
    }

    public static async Task<IReadOnlyList<string>> FindReferencesAsync(ModelProfile model, string root,
        ModelResourceKind kind, string resourcePath, CancellationToken token = default)
    {
        var directory = ModelConfigurationFile.GetDirectory(model, root);
        // Validate even an empty directory, so a junction cannot hide configurations elsewhere.
        ModelConfigurationFile.ValidatePath(Path.Combine(directory, ".resource-check.json"), model, root);
        var references = new List<string>();
        if (!Directory.Exists(directory)) return references;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            ModelConfigurationReferences configuration;
            try { configuration = await ModelConfigurationFile.ReadReferencesAsync(path, model, root, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                throw new InvalidDataException($"无法确认配置引用，请先修复 / Cannot inspect configuration: {Path.GetFileName(path)}", ex);
            }
            var selected = kind switch
            {
                ModelResourceKind.Template => configuration.Template,
                ModelResourceKind.Mtp => configuration.MtpFile,
                ModelResourceKind.Vision => configuration.VisionFile,
                _ => null,
            };
            if (selected is not null && SamePath(selected, resourcePath, root)) references.Add(Path.GetFileName(path));
        }
        return references;
    }

    public static async Task RemoveAsync(ModelProfile model, string root, ModelResourceKind kind, string resourcePath,
        Func<bool> canMutate, CancellationToken token = default)
    {
        if (kind == ModelResourceKind.Reasoning || string.IsNullOrWhiteSpace(resourcePath))
            throw new InvalidOperationException("内置能力不能移除 / Built-in capabilities cannot be removed.");
        ModelConfigurationFile.GetDirectory(model, root);
        EnsureAllowed();
        await ModelResourceCatalog.EnsureMigratedAsync(model, root, token).ConfigureAwait(false);
        var catalogPath = ModelResourceCatalog.FilePath(model, root);
        await using var gate = await ModelResourceCatalog.AcquireAsync(catalogPath + ".lock", token).ConfigureAwait(false);
        var previous = ModelResourceCatalog.Read(model, root);
        var references = await FindReferencesAsync(model, root, kind, resourcePath, token).ConfigureAwait(false);
        if (references.Count > 0)
            throw new InvalidOperationException("以下配置仍选择了该资源，请修改并保存后再移除 / Resource is selected by:" + Environment.NewLine + string.Join(Environment.NewLine, references));
        EnsureAllowed();
        var remaining = previous.Records.Where(record => record.Kind != kind || !SamePath(record.ResourcePath, resourcePath, root)).ToArray();
        var next = previous with
        {
            Records = remaining,
            DefaultTemplatePath = kind == ModelResourceKind.Template && SamePath(previous.DefaultTemplatePath, resourcePath, root)
                ? null : previous.DefaultTemplatePath,
        };
        await ModelConfigurationFile.WriteAtomicallyAsync(catalogPath, System.Text.Json.JsonSerializer.Serialize(next,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), token).ConfigureAwait(false);

        void EnsureAllowed()
        {
            if (!canMutate()) throw new InvalidOperationException("本地服务或检测正在运行，暂时不能移除 / Stop local services and detection before removing a resource.");
        }
    }
}
