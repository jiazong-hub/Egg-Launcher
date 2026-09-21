using System.IO;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Runtime.Detection;

namespace Launcher.App;

public partial class MainWindow
{
    private async void LoadExternalVisionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        var runtimeRoot = Path.GetFullPath(_settings.LlamaRoot);
        string? projectorRelativePath = null;
        var source = VisionSourceKind.External;
        if (DetectBuiltInVision(selected.Profile, runtimeRoot))
        {
            var answer = MessageBox.Show(
                this,
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
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                projectorRelativePath = ValidateExternalVisionSelection(selected.Profile, runtimeRoot, dialog.FileName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
            {
                MessageBox.Show(this, exception.Message, AppLanguageManager.Choose("不能加载该视觉模块", "Unable to Load This Vision Module"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        SetBusy(true);
        try
        {
            var capabilities = await LlamaRuntimeOptionDetector.DetectAsync(runtimeRoot, _lifetime.Token);
            if (capabilities is not null && !capabilities.Contains("mmproj"))
            {
                throw new NotSupportedException(AppLanguageManager.Choose("当前 llama.cpp Runtime 未报告 --mmproj。", "The current llama.cpp Runtime does not report --mmproj."));
            }

            if (!CanEditProfile(selected.Profile, forceClientRefresh: true))
            {
                throw new InvalidOperationException(AppLanguageManager.Choose("识别期间模型被启动，因此没有保存视觉关联。", "The model was started during recognition, so the vision association was not saved."));
            }

            var recognized = selected.Profile with
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
            await SaveProfileArtifactsAsync(recognized, _lifetime.Token);
            await ReloadProfilesAsync(runtimeRoot, _lifetime.Token);
            StatusText.Text = AppLanguageManager.Choose("已通过 GGUF 元数据识别并关联视觉模块；未执行额外加载测试，开关仍保持关闭，实际兼容性由 llama.cpp 在正式加载时确认。", "Vision was recognized and associated from GGUF metadata. No separate load test was run; the switch remains off, and llama.cpp will confirm actual compatibility during normal loading.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"视觉模块识别失败：{exception.Message}", $"Vision recognition failed: {exception.Message}");
            MessageBox.Show(this, StatusText.Text, AppLanguageManager.Choose("视觉识别失败", "Vision Recognition Failed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveExternalVisionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem
            {
                Profile: { VisionSource: VisionSourceKind.External, VisionProjectorRelativePath: not null } profile,
            })
        {
            return;
        }

        if (MessageBox.Show(
                this,
                AppLanguageManager.Choose($"取消 {profile.DisplayName} 与外置视觉模块的关联？\n\n磁盘文件不会删除。", $"Remove the external vision association from {profile.DisplayName}?\n\nThe file will remain on disk."),
                AppLanguageManager.Choose("取消视觉关联", "Remove Vision Association"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var runtimeRoot = Path.GetFullPath(_settings.LlamaRoot);
            var builtIn = DetectBuiltInVision(profile, runtimeRoot);
            var updated = profile with
            {
                VisionEnabled = false,
                VisionSource = VisionSourceKind.BuiltIn,
                VisionCapabilityStatus = builtIn ? VisionCapabilityStatus.BuiltInCandidate : VisionCapabilityStatus.Unknown,
                VisionProjectorRelativePath = null,
                VisionValidationSignature = null,
                VisionValidatedAtUtc = null,
            };
            await SaveProfileArtifactsAsync(updated, _lifetime.Token);
            await ReloadProfilesAsync(runtimeRoot, _lifetime.Token);
            StatusText.Text = AppLanguageManager.Choose("已取消外置视觉关联；原文件未删除。", "The external vision association was removed; the file was not deleted.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"取消视觉关联失败：{exception.Message}", $"Failed to remove the vision association: {exception.Message}");
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
