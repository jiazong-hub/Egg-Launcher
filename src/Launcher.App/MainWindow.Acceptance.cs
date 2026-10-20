using Launcher.Core.Configuration;
using Launcher.Models.Profiles;

namespace Launcher.App;

public partial class MainWindow
{
    private (string ModelId, int? ProcessId, DateTimeOffset? StartedAtUtc)? _acceptanceSession;

    private void UpdateSidebarMetricFontSize(double sidebarWidth)
    {
        var fontSize = Lerp(11, 15, (sidebarWidth - 180) / 112);
        SidebarSpeedText.FontSize = SidebarAcceptanceText.FontSize = SidebarContextText.FontSize = fontSize;
    }

    private void UpdateSidebarAcceptance(RuntimeState? state, ModelProfile profile, TrustedRouterEndpoint endpoint)
    {
        var session = (profile.Id, endpoint.ProcessId, endpoint.StartedAtUtc);
        if (_acceptanceSession != session)
        {
            ClearSidebarAcceptance();
            _acceptanceSession = session;
        }
        if (_settings.SelectedMode != ProviderMode.Local || _settings.SelectedModelId != profile.Id)
        {
            ClearSidebarAcceptance();
            return;
        }
        // A transient read failure must not erase the last result in the same run.
        if (state is null) return;
        if (state.SelectedMode != ProviderMode.Local
            || state.Phase != RuntimePhase.Running || state.SelectedModelId != profile.Id
            || state.RouterProcessId != endpoint.ProcessId || state.RouterStartedAtUtc != endpoint.StartedAtUtc
            || !IsFreshRuntimeState(state.UpdatedAtUtc)
            || state.LatestDraftAcceptance is not { IsValid: true } value
            || value.ObservedAtUtc < state.RouterStartedAtUtc)
        {
            ClearSidebarAcceptance();
            return;
        }
        SidebarAcceptanceText.Text = $"{value.Rate * 100:0.0}%";
        SidebarAcceptancePanel.ToolTip = AppLanguageManager.Choose(
            $"最近一次推理请求的{(value.IncludesNgram ? "整体草稿" : "MTP 草稿")}接受率：{value.AcceptedTokens:N0} / {value.DraftTokens:N0}。\n数据来自 llama.cpp，保留至下一次有效统计。",
            $"Latest request's {(value.IncludesNgram ? "combined draft" : "MTP draft")} acceptance: {value.AcceptedTokens:N0} / {value.DraftTokens:N0}.\nReported by llama.cpp; retained until the next valid result.");
    }

    private void ClearSidebarAcceptance()
    {
        _acceptanceSession = null;
        SidebarAcceptanceText.Text = "–";
        SidebarAcceptancePanel.SetResourceReference(System.Windows.FrameworkElement.ToolTipProperty, "AcceptanceUnavailableTip");
    }
}
