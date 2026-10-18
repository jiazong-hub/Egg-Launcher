using System.IO;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Runtime.Detection;

namespace Launcher.App;

public partial class MainWindow
{
    private async Task LoadExternalVisionAsync(ModelProfile model)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || !CanEditProfile(model, forceClientRefresh: true))
        {
            return;
        }

        if (!CanRunModelResourceDetection()) return;
        await using var operation = _diagnostics.Begin("vision_resource_add", ModelDiagnosticFields(model));
        await operation.StageAsync("select_and_validate_file");
        var runtimeRoot = Path.GetFullPath(_settings.LlamaRoot);
        string? projectorRelativePath = null;
        var source = VisionSourceKind.External;
        if (DetectBuiltInVision(model, runtimeRoot))
        {
            var answer = MessageBox.Show(
                ResourceDialogOwner,
                AppLanguageManager.Choose(
                    "主 GGUF 中检测到视觉编码器元数据。\n\n选择“是”关联模型自带的视觉能力；选择“否”改为选择外置 mmproj 文件。",
                    "Vision encoder metadata was found in the main GGUF.\n\nChoose Yes to associate built-in vision, or No to select an external mmproj file."),
                AppLanguageManager.Choose("选择视觉模块来源", "Choose Vision Source"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                source = VisionSourceKind.BuiltIn;
            }
        }

        if (source == VisionSourceKind.External)
        {
            var modelsRoot = Path.Combine(runtimeRoot, "models");
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppLanguageManager.Choose(
                    "选择与当前主模型匹配的视觉投影 GGUF（mmproj）",
                    "Select a vision projector GGUF (mmproj) matching the current model"),
                Filter = "GGUF (*.gguf)|*.gguf",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = Directory.Exists(modelsRoot) ? modelsRoot : runtimeRoot,
            };
            if (dialog.ShowDialog(ResourceDialogOwner) != true)
            {
                return;
            }

            try
            {
                operation.Set("resource", Path.GetFileName(dialog.FileName));
                projectorRelativePath = ValidateExternalVisionSelection(model, runtimeRoot, dialog.FileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
            {
                await operation.FailAsync(exception, "resource_file_rejected");
                MessageBox.Show(ResourceDialogOwner, exception.Message, AppLanguageManager.Choose("不能加载该视觉模块", "Unable to Load This Vision Module"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        operation.Set("resource", Path.GetFileName(projectorRelativePath));
        if (await TryReuseResourceAsync(model with { VisionSource = source, VisionProjectorRelativePath = projectorRelativePath },
                runtimeRoot, Launcher.Scripts.Templates.ModelResourceKind.Vision))
        { await operation.CompleteAsync("blocked", "already_registered"); return; }

        SetBusy(true);
        try
        {
            if (!CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
                throw new InvalidOperationException(AppLanguageManager.Choose("本地服务状态已改变，请停止服务后再检测。", "Local service state changed. Stop services before checking."));
            await Launcher.Scripts.Templates.ModelResourceCatalog.EnsureMigratedAsync(model, runtimeRoot, _lifetime.Token);
            await operation.StageAsync("metadata_and_runtime_option_check");
            var capabilities = await LlamaRuntimeOptionDetector.DetectAsync(runtimeRoot, _lifetime.Token);
            if (capabilities is not null && !capabilities.Contains("mmproj"))
            {
                throw new NotSupportedException(AppLanguageManager.Choose("当前 llama.cpp Runtime 未报告 --mmproj。", "The current llama.cpp Runtime does not report --mmproj."));
            }

            if (!CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
            {
                throw new InvalidOperationException(AppLanguageManager.Choose("识别期间模型被启动，因此没有保存视觉关联。", "The model was started during recognition, so the vision association was not saved."));
            }

            var recognized = model with
            {
                VisionEnabled = false,
                VisionSource = source,
                VisionCapabilityStatus = VisionCapabilityStatus.Verified,
                VisionProjectorRelativePath = source == VisionSourceKind.External ? projectorRelativePath : null,
                VisionValidationSignature = null,
                VisionValidatedAtUtc = DateTimeOffset.UtcNow,
            };
            recognized = recognized with
            {
                VisionValidationSignature = VisionValidationFingerprint.Compute(recognized, runtimeRoot),
            };
            operation.Set("source", source.ToString());
            await operation.StageAsync("register_recognized_vision");
            await Launcher.Scripts.Templates.ModelResourceCatalog.RegisterAsync(recognized, runtimeRoot, Launcher.Scripts.Templates.ModelResourceKind.Vision, _lifetime.Token);
            operation.Set("resourceRegistered", true);
            await ReloadProfilesAsync(runtimeRoot, _lifetime.Token);
            await operation.CompleteAsync();
            StatusText.Text = AppLanguageManager.Choose("视觉模块已通过 GGUF 元数据识别并登记；已有配置未改变，未执行额外加载测试，实际兼容性由 llama.cpp 在正式加载时确认。", "Vision was recognized from GGUF metadata and registered. Existing configurations are unchanged. No separate load test was run; llama.cpp confirms compatibility during normal loading.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await operation.CompleteAsync("cancelled");
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception, "resource_validation_failed");
            StatusText.Text = AppLanguageManager.Choose($"视觉模块识别失败：{exception.Message}", $"Vision recognition failed: {exception.Message}");
            MessageBox.Show(ResourceDialogOwner, StatusText.Text, AppLanguageManager.Choose("视觉识别失败", "Vision Recognition Failed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string ValidateExternalVisionSelection(ModelProfile profile, string runtimeRoot, string selectedFile)
    {
        var root = Path.GetFullPath(runtimeRoot);
        var modelsRoot = Path.GetFullPath(Path.Combine(root, "models")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var selectedPath = Path.GetFullPath(selectedFile);
        var mainPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
        if (!selectedPath.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLanguageManager.Choose("视觉投影文件必须位于当前 Runtime 的 models 目录内。", "The vision projector must be inside the current Runtime models directory."));
        }

        if (string.Equals(selectedPath, mainPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLanguageManager.Choose("主模型不能同时作为外置视觉投影文件。", "The main model cannot also be the external vision projector."));
        }

        var metadata = GgufContextMetadataReader.ReadMetadata(selectedPath);
        if (!metadata.HasVisionEncoder)
        {
            throw new InvalidDataException(AppLanguageManager.Choose("所选 GGUF 没有可确认的视觉编码器元数据；仅凭文件名无法证明兼容性，因此未建立关联。", "The selected GGUF has no verifiable vision-encoder metadata. A filename alone cannot prove compatibility, so no association was created."));
        }

        return Path.GetRelativePath(root, selectedPath);
    }

    private static bool DetectBuiltInVision(ModelProfile profile, string runtimeRoot)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
            return File.Exists(path) && GgufContextMetadataReader.ReadMetadata(path).HasVisionEncoder;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

}
