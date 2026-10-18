using Launcher.Models.Profiles;
using Launcher.Core.Diagnostics;
using Launcher.Scripts.Templates;
using Launcher.Runtime.Detection;

namespace Launcher.Orchestration.Models;

/// <summary>The only resolver for a new Local run. Missing selection never falls back to old artifacts.</summary>
public static class ModelStartupConfiguration
{
    public static Task<ModelProfile> ResolveAsync(ModelProfile model, string root, CancellationToken token = default,
        DiagnosticJournal? diagnostics = null) => (diagnostics ?? DiagnosticJournal.None).RunAsync("startup_configuration",
        new Dictionary<string, object?> { ["modelId"] = model.Id, ["configuration"] = Path.GetFileName(model.PreferredConfigurationPath) },
        operation => ResolveCoreAsync(model, root, operation, token));

    private static async Task<ModelProfile> ResolveCoreAsync(ModelProfile model, string root, DiagnosticOperation operation, CancellationToken token)
    {
        await ModelResourceCatalog.EnsureMigratedAsync(model, root, token).ConfigureAwait(false);
        if (!model.ConfigurationInitialized || string.IsNullOrWhiteSpace(model.PreferredConfigurationPath))
            throw new InvalidDataException("没有预选配置文件 / No configuration selected.");
        await operation.StageAsync("read_configuration").ConfigureAwait(false);
        var profile = await ModelConfigurationFile.ReadAsync(model.PreferredConfigurationPath, model, root, token).ConfigureAwait(false);
        operation.Set("jinja", profile.Jinja);
        operation.Set("template", Path.GetFileName(profile.ChatTemplateRelativePath));
        operation.Set("visionEnabled", profile.VisionEnabled);
        operation.Set("visionResource", Path.GetFileName(profile.VisionProjectorRelativePath));
        operation.Set("mtpEnabled", profile.MtpEnabled);
        operation.Set("mtpResource", Path.GetFileName(profile.MtpDraftModelRelativePath));
        await operation.StageAsync("resolve_resources").ConfigureAwait(false);
        profile = ModelResourceCatalog.Resolve(profile, root);
        if (!string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
            ChatTemplateFile.ResolveRelative(profile.ChatTemplateRelativePath, root);
        if (profile.Jinja && profile.ChatTemplateRelativePath is not null
            && !ModelResourceCatalog.Read(model, root).Records.Any(r => r.Kind == ModelResourceKind.Template
                && ModelResourceManagement.SamePath(r.ResourcePath, profile.ChatTemplateRelativePath, root) && ModelResourceCatalog.IsCurrent(r, root)))
            throw new InvalidDataException("所选聊天模板尚无有效检查记录，请在本地模型管理中添加或生成模板 / Check the selected template in Local Model Management.");
        await operation.StageAsync("validate_parameters").ConfigureAwait(false);
        var errors = ModelProfileValidator.Validate(profile, root);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        await operation.StageAsync("validate_reasoning").ConfigureAwait(false);
        if (ReasoningValidationState.RequiresCurrentProof(profile)
            && ReasoningValidationState.Check(profile, root).State != ReasoningValidationStateKind.Current)
            throw new InvalidDataException("当前配置的思考验证无效，请重新检测 / Reasoning proof is not current.");
        await operation.StageAsync("validate_runtime_options").ConfigureAwait(false);
        await ValidateRuntimeOptionsAsync(profile, root, token).ConfigureAwait(false);
        return profile;
    }

    private static async Task ValidateRuntimeOptionsAsync(ModelProfile profile, string root, CancellationToken token)
    {
        if (profile.ContextShiftEnabled && ContextShiftCapabilityCache.Read(profile, root)?.Supported != true)
            throw new InvalidDataException("当前配置的上下文滚动尚无有效验证，请重新检测或关闭 / Context shifting is unverified; check it again or turn it off.");
        var keepRequested = profile.ExtraArguments.Keys.Any(key => key.Equals("keep", StringComparison.OrdinalIgnoreCase));
        if (!profile.ContextShiftEnabled && !keepRequested) return;
        var options = await LlamaRuntimeOptionDetector.DetectAsync(root, token).ConfigureAwait(false);
        if (profile.ContextShiftEnabled && options?.Contains("context-shift") != true)
            throw new InvalidDataException("当前 llama.cpp Runtime 无法确认支持 --context-shift，请关闭滚动或更换 Runtime / Runtime support for --context-shift could not be confirmed.");
        if (keepRequested && options?.Contains("keep") != true)
            throw new InvalidDataException("当前 llama.cpp Runtime 无法确认支持 --keep，请清空保留开头 token 设置或更换 Runtime / Runtime support for --keep could not be confirmed.");
    }
}
