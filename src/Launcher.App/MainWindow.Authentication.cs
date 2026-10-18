using System.Windows;
using System.Windows.Controls;

namespace Launcher.App;

public partial class MainWindow
{
    private bool CanChangeLocalAuthentication(bool forceClientRefresh = false) =>
        _themeUiReady && !IsModeSelectionLocked(forceClientRefresh)
        && !IsLocalRuntimeSessionActive(forceClientRefresh)
        && !IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot);

    private void UpdateLocalAuthenticationUi()
    {
        var enabled = _settings.LocalRequiresOpenAiAuth;
        var canChange = CanChangeLocalAuthentication();
        LocalAuthenticationButton.IsEnabled = canChange;
        LocalAuthenticationIcon.ContentTemplate = (DataTemplate)FindResource(
            enabled ? "AuthenticationOnIconTemplate" : "AuthenticationOffIconTemplate");
        LocalAuthenticationIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,
            enabled ? "AccentBrush" : "PrimaryBrush");
        var state = AppLanguageManager.Text(enabled ? "LocalAuthenticationOn" : "LocalAuthenticationOff");
        var hint = canChange
            ? AppLanguageManager.Text(enabled ? "LocalAuthenticationOnHint" : "LocalAuthenticationOffHint")
            : state + "\n" + AppLanguageManager.Text("LocalAuthenticationLockedHint");
        LocalAuthenticationButton.ToolTip = hint;
        System.Windows.Automation.AutomationProperties.SetHelpText(LocalAuthenticationButton, hint);
        System.Windows.Automation.AutomationProperties.SetItemStatus(LocalAuthenticationButton, state);
    }

    private async void LocalAuthenticationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeLocalAuthentication(forceClientRefresh: true))
        {
            await _diagnostics.WriteAsync("authentication_preference", "blocked",
                new Dictionary<string, object?> { ["reasonCode"] = "client_service_or_operation_busy" });
            UpdateLocalAuthenticationUi();
            return;
        }

        SetBusy(true);
        await using var operation = _diagnostics.Begin("authentication_preference");
        try
        {
            await MutateSettingsAsync(settings => settings with
            {
                LocalRequiresOpenAiAuth = !settings.LocalRequiresOpenAiAuth,
            }, _lifetime.Token);
            operation.Set("savedRequiresOpenAiAuth", _settings.LocalRequiresOpenAiAuth);
            operation.Set("takesEffect", "next_local_launch");
            await operation.CompleteAsync();
            StatusText.Text = AppLanguageManager.Text(_settings.LocalRequiresOpenAiAuth
                ? "LocalAuthenticationEnabled" : "LocalAuthenticationDisabled");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception, "settings_save_failed");
            StatusText.Text = AppLanguageManager.Format("LocalAuthenticationSaveFailed", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
