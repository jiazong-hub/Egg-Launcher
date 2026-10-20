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
        operation.Set("ngramEnabled", profile.Ngram.Enabled);
        operation.Set("ngramTypes", string.Join(',', profile.Ngram.Types));
        operation.SetDetail("ngramParameters", profile.Ngram.Parameters);
        operation.Set("llamaCustomNames", profile.CustomParameters.Llama.Select(r => r.Name).ToArray());
        operation.Set("codexCustomNames", profile.CustomParameters.Codex.Select(r => r.Name).ToArray());
        operation.SetDetail("batchSize", profile.BatchSize);
        operation.SetDetail("microBatchSize", profile.MicroBatchSize);
        operation.SetDetail("runtimeParameters", profile.RuntimeParameters);
        operation.SetDetail("mtpDraftGpuLayers", profile.MtpDraftGpuLayers);
        // Arbitrary custom values can be credentials or private paths. Never log them.
        operation.SetDetail("customDelivery", "llama=model_preset_and_bat;codex=local_config_transaction;effect=unverified");
        operation.SetDetail("staticCache", Path.GetFileName(profile.Ngram.StaticCachePath));
        operation.SetDetail("dynamicCache", Path.GetFileName(profile.Ngram.DynamicCachePath));
        await operation.StageAsync("resolve_resources").ConfigureAwait(false);
        profile = ModelResourceCatalog.Resolve(profile, root);
        if (!string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath))
            ChatTemplateFile.ResolveRelative(profile.ChatTemplateRelativePath, root);
        if (profile.Jinja && profile.ChatTemplateRelativePath is null
            && ModelResourceCatalog.Read(model, root).Records.Any(r => r.Kind == ModelResourceKind.Template
                && r.ResourcePath is null && r.ValidationRejected))
            throw new InvalidDataException("内置模板重新检测未通过，请重新检测或选择可用模板 / Embedded template recheck failed; recheck or select a usable template.");
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
        var checkpointsRequested = profile.ExtraArguments.Keys.Any(key => key is "ctx-checkpoints" or "swa-checkpoints");
        if (!profile.ContextShiftEnabled && !keepRequested && !checkpointsRequested && !profile.Ngram.Enabled && !profile.RuntimeParameters.HasIntent) return;
        var options = await LlamaRuntimeOptionDetector.DetectAsync(root, token).ConfigureAwait(false);
        foreach (var argument in profile.RuntimeParameters.Arguments(profile))
            if (options?.Contains(argument.Key) != true)
                throw new InvalidDataException($"当前运行库无法确认支持 --{argument.Key} / Runtime support is unconfirmed.");
        var ngramErrors = NgramOptions.ValidateRuntime(profile.Ngram, profile.MtpEnabled, options);
        if (ngramErrors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, ngramErrors));
        if (profile.ContextShiftEnabled && options?.Contains("context-shift") != true)
            throw new InvalidDataException("当前 llama.cpp Runtime 无法确认支持 --context-shift，请关闭滚动或更换 Runtime / Runtime support for --context-shift could not be confirmed.");
        if (keepRequested && options?.Contains("keep") != true)
            throw new InvalidDataException("当前 llama.cpp Runtime 无法确认支持 --keep，请清空保留开头 token 设置或更换 Runtime / Runtime support for --keep could not be confirmed.");
        if (checkpointsRequested && options?.Contains("ctx-checkpoints") != true)
            throw new InvalidDataException("当前运行库无法确认支持上下文检查点 / Runtime checkpoint support is unconfirmed.");
    }
}
