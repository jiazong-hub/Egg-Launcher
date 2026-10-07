using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Models.Profiles;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private readonly Func<ModelProfile, CancellationToken, Task<ModelProfile>>? _templateProbe;
    private CancellationTokenSource? _templateCancellation;
    private bool _checkingTemplate;

    private void TemplatePath_Changed(object sender, RoutedEventArgs e) => UpdateTemplateStatus();

    private void UpdateTemplateStatus()
    {
        if (TemplateStatusText is null || ChatTemplatePathTextBox is null || JinjaCheckBox is null) return;
        if (!_populatingProfile && _reasoningProfile is not null)
        {
            _reasoningProfile = EnsureCurrentReasoningValidation(_reasoningProfile with
            {
                Jinja = JinjaCheckBox.IsChecked == true,
                ChatTemplateRelativePath = NullWhenWhiteSpace(ChatTemplatePathTextBox.Text),
            });
            RefreshReasoningUi();
        }
        var profile = _originalProfile with
        {
            Jinja = JinjaCheckBox.IsChecked == true,
            ChatTemplateRelativePath = NullWhenWhiteSpace(ChatTemplatePathTextBox.Text),
        };
        if (!profile.Jinja)
        {
            TemplateStatusText.Text = AppLanguageManager.Choose("Jinja 已关闭；模板检测不可用。", "Jinja is disabled; template checking is unavailable.");
            return;
        }
        try
        {
            var embedded = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath);
            var owned = !embedded && File.Exists(Path.Combine(_runtimeRoot, profile.ChatTemplateRelativePath!))
                && File.ReadLines(Path.Combine(_runtimeRoot, profile.ChatTemplateRelativePath!)).FirstOrDefault() == CodexChatTemplateCompatibility.OwnershipMarker;
            var source = embedded ? AppLanguageManager.Choose("GGUF 内置模板", "Embedded GGUF template")
                : owned ? AppLanguageManager.Choose("启动器兼容模板", "Launcher compatibility template")
                : AppLanguageManager.Choose("用户指定模板", "User-selected template");
            var status = ChatTemplateValidationCache.IsCurrent(profile, _runtimeRoot)
                ? AppLanguageManager.Choose("渲染检查通过，实际对话待验证", "Rendering checks passed; inference not verified")
                : File.Exists(ChatTemplateValidationCache.PathFor(profile, _runtimeRoot))
                    ? AppLanguageManager.Choose("结果已过期，请重新检测", "Result expired; check again")
                    : AppLanguageManager.Choose("未检测", "Not checked");
            if (embedded && !ChatTemplateValidationCache.IsCurrent(profile, _runtimeRoot))
            {
                var analysis = ChatTemplateRules.Analyze(CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(
                    Path.Combine(_runtimeRoot, profile.ModelRelativePath)));
                status += analysis.Kind == ChatTemplateMatchKind.Repairable
                    ? AppLanguageManager.Choose("；发现已支持的不兼容规则", "; supported incompatibility rule found")
                    : AppLanguageManager.Choose("；检测未覆盖，兼容性未知", "; structure not covered; compatibility unknown");
            }
            TemplateStatusText.Text = source + " · " + status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            TemplateStatusText.Text = AppLanguageManager.Choose("模板信息读取失败：", "Cannot read template information: ") + exception.Message;
        }
    }

    private async void CheckTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingTemplate || _contextShiftCancellation is not null || _templateProbe is null || JinjaCheckBox.IsChecked != true) return;
        _probeProfile = null;
        CompleteSave(saveAsModelDefault: false, captureForProbe: true);
        if (_probeProfile is not { } candidate) return;
        _checkingTemplate = true;
        _templateCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        ProfileEditorTabs.IsEnabled = false;
        SaveProfileButton.IsEnabled = SaveModelDefaultButton.IsEnabled = RestoreModelDefaultsButton.IsEnabled = false;
        TemplateCancelButton.Visibility = Visibility.Visible;
        try
        {
            TemplateStatusText.Text = AppLanguageManager.Choose("正在检测模板，可能需要加载模型…", "Checking the template; the model may need to load…");
            var result = await _templateProbe(candidate, _templateCancellation.Token);
            if (!IsLoaded) return;
            ChatTemplatePathTextBox.Text = result.ChatTemplateRelativePath ?? string.Empty;
            UpdateTemplateStatus();
            if (!ChatTemplateValidationCache.IsCurrent(result, _runtimeRoot))
            {
                var template = CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(Path.Combine(_runtimeRoot, result.ModelRelativePath));
                var analysis = ChatTemplateRules.Analyze(template);
                TemplateStatusText.Text += AppLanguageManager.Choose(
                    analysis.Kind == ChatTemplateMatchKind.NotMatched ? "；检测未覆盖，未修改配置。" : "；发现已支持的不兼容规则，尚未启用兼容模板。",
                    "; No new compatible template was selected.");
            }
        }
        catch (OperationCanceledException) { if (IsLoaded) UpdateTemplateStatus(); }
        catch (Exception exception) { if (IsLoaded) TemplateStatusText.Text = exception.Message; }
        finally
        {
            _checkingTemplate = false;
            _templateCancellation.Dispose();
            _templateCancellation = null;
            if (IsLoaded)
            {
                ProfileEditorTabs.IsEnabled = true;
                SaveProfileButton.IsEnabled = SaveModelDefaultButton.IsEnabled = true;
                RestoreModelDefaultsButton.IsEnabled = _originalProfile.DefaultParameters is not null;
                TemplateCancelButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelTemplateCheck_Click(object sender, RoutedEventArgs e) => _templateCancellation?.Cancel();

    private void RestoreEmbeddedTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingTemplate) return;
        ChatTemplatePathTextBox.Text = string.Empty;
        UpdateTemplateStatus();
    }

    private void OpenTemplateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(_runtimeRoot, "scripts", "templates");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception exception) { TemplateStatusText.Text = exception.Message; }
    }
}
