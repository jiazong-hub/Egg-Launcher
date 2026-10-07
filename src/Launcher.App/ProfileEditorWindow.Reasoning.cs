using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.ChatGPT.Catalog;
using Launcher.Models.Profiles;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private readonly Func<ModelProfile, CancellationToken, Task<ModelProfile>>? _reasoningProbe;
    private ModelProfile _reasoningProfile;
    private CancellationTokenSource? _reasoningCancellation;
    private bool _populatingReasoning;
    private string? _reasoningOutcome;

    private ModelProfile EnsureCurrentReasoningValidation(ModelProfile profile)
    {
        if (profile.ReasoningCapabilityCheckedAtUtc is null && profile.ThinkingEnabled is null && !profile.ExposeReasoningEffortInChatGpt) return profile;
        return ReasoningValidationState.Check(profile, _runtimeRoot).ApplyTo(profile);
    }

    private void PopulateReasoningCapability(ModelProfile profile)
    {
        _reasoningProfile = EnsureCurrentReasoningValidation(profile);
        RefreshReasoningUi();
    }

    private void RefreshReasoningUi()
    {
        if (ThinkingEnabledCheckBox is null) return;
        _populatingReasoning = true;
        var profile = _reasoningProfile;
        var busy = _reasoningCancellation is not null;
        DetectReasoningButton.IsEnabled = !busy && _reasoningProbe is not null;
        ThinkingEnabledCheckBox.IsEnabled = !busy && profile.SupportsThinkingSwitch == true;
        ThinkingEnabledCheckBox.IsChecked = profile.ThinkingEnabled ?? profile.DefaultThinkingEnabled;
        InheritThinkingButton.IsEnabled = !busy && profile.SupportsThinkingSwitch == true && profile.ThinkingEnabled.HasValue;
        ThinkingStateTextBlock.Text = profile.ThinkingEnabled is bool enabled
            ? AppLanguageManager.Choose(enabled ? "已设置：开启" : "已设置：关闭", enabled ? "Configured: on" : "Configured: off")
            : profile.DefaultThinkingEnabled is bool defaultEnabled
                ? AppLanguageManager.Choose(defaultEnabled ? "沿用模板默认：开启" : "沿用模板默认：关闭", defaultEnabled ? "Template default: on" : "Template default: off")
                : AppLanguageManager.Choose("沿用模板行为；默认状态未确认", "Inherit template behavior; default state unconfirmed");
        var verified = profile.ReasoningCapabilityStatus == ReasoningCapabilityStatus.Verified;
        var recognized = profile.SupportedReasoningLevels.Where(CodexReasoningLevels.IsRecognized).ToArray();
        ReasoningEffortCheckBox.IsEnabled = !busy && verified && profile.ReasoningResponsesVerified && profile.ReasoningClientCompatible == true
            && recognized.Length > 0 && (profile.ThinkingEnabled ?? profile.DefaultThinkingEnabled) != false;
        ReasoningEffortCheckBox.IsChecked = profile.ExposeReasoningEffortInChatGpt;
        ReverseReasoningOrderCheckBox.IsEnabled = ReasoningEffortCheckBox.IsEnabled && profile.ExposeReasoningEffortInChatGpt;
        ReverseReasoningOrderCheckBox.IsChecked = profile.ReverseReasoningLevelDisplayOrder;
        ReasoningCapabilityStatusTextBlock.Text = busy ? AppLanguageManager.Choose("正在隔离加载并检测思考能力…", "Loading in isolation and checking reasoning capabilities…")
            : profile.ReasoningCapabilityCheckedAtUtc is null ? AppLanguageManager.Choose("思考能力尚未检测，或结果已过期。", "Reasoning capabilities are not checked or the result has expired.")
            : AppLanguageManager.Choose(
                $"思考开关：{(profile.SupportsThinkingSwitch == true ? "已验证" : "未确认")}；档位：{(verified ? "已确认" : "未确认")}；Codex 传递：{(profile.ReasoningResponsesVerified ? "已验证" : "未确认")}",
                $"Thinking switch: {(profile.SupportsThinkingSwitch == true ? "verified" : "unconfirmed")}; levels: {(verified ? "verified" : "unconfirmed")}; Responses effort: {(profile.ReasoningResponsesVerified ? "verified" : "unconfirmed")}");
        if (!busy && _reasoningOutcome is not null) ReasoningCapabilityStatusTextBlock.Text = _reasoningOutcome + "\n" + ReasoningCapabilityStatusTextBlock.Text;
        ReasoningLevelsTextBlock.Text = AppLanguageManager.Choose("已确认档位：", "Verified levels: ")
            + (verified ? string.Join("、", profile.SupportedReasoningLevels.Select(FormatReasoningLevel)) : AppLanguageManager.Choose("未确认", "Unconfirmed"));
        ReasoningDefaultTextBlock.Text = AppLanguageManager.Choose("默认档位：", "Default level: ")
            + (profile.DefaultReasoningLevel is { } level ? FormatReasoningLevel(level) : AppLanguageManager.Choose("未确认", "Unconfirmed"));
        var details = profile.ReasoningValidationDetails ?? AppLanguageManager.Choose("检测使用基础参数中当前选定的模板。", "Detection uses the template selected in Basic Parameters.");
        if (profile.ReasoningLevelAliases.Count > 0)
            details += "\n" + AppLanguageManager.Choose("模板内部别名（不另列档位）：", "Template aliases (not separate levels): ")
                + string.Join(", ", profile.ReasoningLevelAliases.Select(pair => $"{pair.Key} = {pair.Value}"));
        if (verified && profile.ReasoningClientCompatible != true)
            details += "\n" + AppLanguageManager.Choose("当前 Codex 协议尚未确认兼容全部原始档位，暂不开放聊天档位调节。", "The current Codex protocol has not confirmed all native values; effort control remains unavailable.");
        ReasoningDetailsTextBlock.Text = details;
        _populatingReasoning = false;
    }

    private static string FormatReasoningLevel(string level) => level switch
    {
        "low" => AppLanguageManager.Choose("低（low）", "Low (low)"),
        "medium" => AppLanguageManager.Choose("中（medium）", "Medium (medium)"),
        "high" => AppLanguageManager.Choose("高（high）", "High (high)"),
        "xhigh" => AppLanguageManager.Choose("极高（xhigh）", "Extra high (xhigh)"),
        _ => level,
    };

    private void ThinkingEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_populatingReasoning || _reasoningProfile.SupportsThinkingSwitch != true) return;
        _reasoningProfile = _reasoningProfile with
        {
            ThinkingEnabled = ThinkingEnabledCheckBox.IsChecked == true,
            ExposeReasoningEffortInChatGpt = ReasoningEffortCheckBox.IsChecked == true
        };
        RefreshReasoningUi();
    }

    private void ReasoningEffort_Changed(object sender, RoutedEventArgs e)
    {
        if (_populatingReasoning) return;
        _reasoningProfile = _reasoningProfile with { ExposeReasoningEffortInChatGpt = ReasoningEffortCheckBox.IsChecked == true };
        RefreshReasoningUi();
    }

    private void ReverseReasoningOrder_Changed(object sender, RoutedEventArgs e)
    {
        if (!_populatingReasoning) _reasoningProfile = _reasoningProfile with
        {
            ReverseReasoningLevelDisplayOrder = ReverseReasoningOrderCheckBox.IsChecked == true
        };
    }

    private void InheritThinking_Click(object sender, RoutedEventArgs e)
    {
        _reasoningProfile = _reasoningProfile with
        {
            ThinkingEnabled = null,
            ExposeReasoningEffortInChatGpt = ReasoningEffortCheckBox.IsChecked == true
        };
        RefreshReasoningUi();
    }

    private async void DetectReasoningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reasoningProbe is null || _reasoningCancellation is not null || _checkingTemplate || _contextShiftCancellation is not null) return;
        _probeProfile = null;
        CompleteSave(false, captureForProbe: true);
        if (_probeProfile is not { } candidate) return;
        if (MessageBox.Show(this, AppLanguageManager.Choose(
            "检测将临时加载当前模型并执行少量受限请求，占用内存和显存。结果点击保存后才应用。继续吗？",
            "Detection temporarily loads this model and makes small bounded requests using memory and VRAM. Results apply only after Save. Continue?"),
            AppLanguageManager.Text("DetectThinkingCapability"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _reasoningOutcome = null;
        _reasoningProfile = candidate;
        _reasoningCancellation = cancellation;
        var selected = ProfileEditorTabs.SelectedItem;
        foreach (TabItem tab in ProfileEditorTabs.Items) if (!ReferenceEquals(tab, selected)) tab.IsEnabled = false;
        SaveProfileButton.IsEnabled = SaveModelDefaultButton.IsEnabled = RestoreModelDefaultsButton.IsEnabled = false;
        CancelReasoningButton.Visibility = Visibility.Visible;
        RefreshReasoningUi();
        try
        {
            var result = await _reasoningProbe(candidate, cancellation.Token);
            if (IsLoaded) { _reasoningProfile = result; _reasoningOutcome = AppLanguageManager.Choose("检测已完成；点击保存后应用。", "Detection completed; apply with Save."); }
        }
        catch (OperationCanceledException)
        {
            _reasoningOutcome = AppLanguageManager.Choose("检测已取消或超时；保留检测前设置，未保存结果。", "Detection canceled or timed out; previous settings retained, no result saved.");
        }
        catch (Exception exception)
        {
            _reasoningOutcome = AppLanguageManager.Choose("检测失败；保留检测前设置，未保存结果。", "Detection failed; previous settings retained, no result saved.");
            if (IsLoaded) MessageBox.Show(this, exception.Message, AppLanguageManager.Text("DetectThinkingCapability"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _reasoningCancellation = null;
            if (IsLoaded)
            {
                foreach (TabItem tab in ProfileEditorTabs.Items) tab.IsEnabled = true;
                SaveProfileButton.IsEnabled = SaveModelDefaultButton.IsEnabled = true;
                RestoreModelDefaultsButton.IsEnabled = _originalProfile.DefaultParameters is not null;
                CancelReasoningButton.Visibility = Visibility.Collapsed;
                RefreshReasoningUi();
            }
        }
    }

    private void CancelReasoningButton_Click(object sender, RoutedEventArgs e) => _reasoningCancellation?.Cancel();
}
