using System.IO;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Runtime.Detection;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class MainWindow
{
    private async Task<string> RecheckModelResourceAsync(ModelProfile model, ModelResourceKind kind, string? relativePath)
    {
        if (_busy || _settings.LlamaRoot is not { } root || !CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
            throw new InvalidOperationException(AppLanguageManager.Choose("请先停止本地服务和检测。", "Stop local services and detection first."));
        if (ModelConfigurationFile.IsTypeMismatch(model.ConfigurationError)) model = ModelConfigurationFile.CreateUnloadedDraft(model);
        await using var operation = _diagnostics.Begin("resource_recheck", ModelDiagnosticFields(model));
        operation.Set("resourceKind", kind.ToString());
        operation.Set("resource", relativePath is null ? "embedded" : Path.GetFileName(relativePath));
        SetBusy(true);
        try
        {
            await operation.StageAsync("validate_resource_path");
            if (!File.Exists(Path.Combine(root, model.ModelRelativePath))) throw new FileNotFoundException("主模型文件缺失 / Main model is missing.");
            // Use only the selected resource, independent of whichever parameter JSON is preselected.
            var selection = TemplateValidationProfile.Create(model) with { ChatTemplateRelativePath = null };
            switch (kind)
            {
                case ModelResourceKind.Template:
                    if (relativePath is not null)
                    {
                        var full = ChatTemplateFile.ResolveRelative(relativePath, root);
                        if (!File.Exists(full)) throw new FileNotFoundException("模板文件缺失 / Template is missing.");
                    }
                    selection = selection with { ChatTemplateRelativePath = relativePath, Jinja = true };
                    break;
                case ModelResourceKind.Mtp when relativePath is not null:
                    if (!File.Exists(Path.Combine(root, relativePath))) throw new FileNotFoundException("MTP 文件缺失 / MTP file is missing.");
                    selection = ExternalMtpValidationProfile.Create(model, relativePath) with
                    { MtpEnabled = false, MtpCapabilityStatus = MtpCapabilityStatus.Unknown, MtpValidationSignature = null, MtpValidatedAtUtc = null };
                    break;
                case ModelResourceKind.Vision:
                    if (relativePath is not null && !File.Exists(Path.Combine(root, relativePath)))
                        throw new FileNotFoundException("视觉模块文件缺失 / Vision module is missing.");
                    selection = selection with
                    {
                        VisionSource = relativePath is null ? VisionSourceKind.BuiltIn : VisionSourceKind.External,
                        VisionProjectorRelativePath = relativePath
                    };
                    break;
                default: throw new InvalidOperationException("内置 MTP 沿用正式启动确认 / Embedded MTP is confirmed during normal startup.");
            }
            var completed = await ModelResourceRecheck.RunAsync(selection, root, kind, async token =>
            {
                await operation.StageAsync("validate_resource");
                switch (kind)
                {
                    case ModelResourceKind.Template:
                        var result = await PrepareChatTemplateAsync(selection, root, token, explicitCheck: true, allowGeneration: false);
                        return result.RenderCheckCompleted ? result.Profile : null;
                    case ModelResourceKind.Mtp:
                        if (GetModelDownloadBlockReason() is { } conflict) throw new InvalidOperationException(conflict);
                        try { ValidateExternalMtpSelection(model, root, Path.Combine(root, relativePath!)); }
                        catch (InvalidDataException ex) { throw new ModelResourceValidationException(ex.Message); }
                        if (MessageBox.Show(ResourceDialogOwner, AppLanguageManager.Choose(
                            "将临时加载主模型和此 MTP 文件，并执行极短推理，占用内存和显存。是否继续？",
                            "Temporarily load the model and this MTP file for a short inference, using RAM and VRAM. Continue?"),
                            AppLanguageManager.Choose("重新检测", "Recheck"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                            return null;
                        return await ValidateExternalMtpWithNativeRouterAsync(selection, root, relativePath!, token);
                    case ModelResourceKind.Vision:
                        try
                        {
                            if (relativePath is null)
                            {
                                if (!DetectBuiltInVision(model, root)) throw new InvalidDataException("未检测到内置视觉元数据 / No built-in vision metadata found.");
                            }
                            else ValidateExternalVisionSelection(model, root, Path.Combine(root, relativePath));
                        }
                        catch (InvalidDataException ex) { throw new ModelResourceValidationException(ex.Message); }
                        var capabilities = await LlamaRuntimeOptionDetector.DetectAsync(root, token);
                        if (capabilities is not null && !capabilities.Contains("mmproj"))
                            throw new NotSupportedException("当前 Runtime 未报告 --mmproj / The runtime does not report --mmproj.");
                        var recognized = selection with
                        {
                            VisionEnabled = false,
                            VisionCapabilityStatus = VisionCapabilityStatus.Verified,
                            VisionValidatedAtUtc = DateTimeOffset.UtcNow
                        };
                        return recognized with { VisionValidationSignature = VisionValidationFingerprint.Compute(recognized, root) };
                    default: throw new ArgumentOutOfRangeException(nameof(kind));
                }
            }, () => CanRunModelResourceDetection() && CanEditProfile(model, forceClientRefresh: true), _lifetime.Token);
            if (completed) await operation.StageAsync("validation_record_updated");
            await operation.CompleteAsync(completed ? "success" : "cancelled", completed ? null : "user_cancelled");
            StatusText.Text = completed
                ? AppLanguageManager.Choose("重新检测通过，验证记录已更新；配置文件和启动预选未改变。", "Recheck passed. Evidence updated; configurations and preselection unchanged.")
                : AppLanguageManager.Choose("已取消检测，原记录保持不变。", "Check cancelled; previous records were kept.");
        }
        catch (Exception ex)
        {
            await operation.FailAsync(ex, ex is ModelResourceValidationException ? "resource_rejected" : "recheck_not_completed");
            StatusText.Text = ex is ModelResourceValidationException
                ? AppLanguageManager.Choose("重新检测未通过，原验证已失效：", "Recheck rejected; previous proof invalidated: ") + ex.Message
                : AppLanguageManager.Choose("检测未完成，未主动清除原验证记录：", "Recheck incomplete; previous proof was not cleared: ") + ex.Message;
            MessageBox.Show(ResourceDialogOwner, StatusText.Text, AppLanguageManager.Choose("重新检测", "Recheck"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
        return StatusText.Text;
    }
}
