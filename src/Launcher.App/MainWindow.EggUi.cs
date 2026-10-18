using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Runtime.Router;
using Launcher.Scripts.Batch;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Launcher.App;

public partial class MainWindow
{
    private void ProjectHomeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/jiazong-hub/Egg-Launcher",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"无法打开 GitHub 项目主页：{exception.Message}",
                $"Unable to open the GitHub project page: {exception.Message}");
        }
    }

    private const int MaximumTrayTooltipLength = 127;
    private readonly bool _startHidden = Environment.GetCommandLineArgs()
        .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _trayLaunchItem;
    private Forms.ToolStripMenuItem? _trayCurrentModeItem;
    private Forms.ToolStripMenuItem? _trayModeMenuItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private bool _trayExitRequested;
    private bool _trayStatusRefreshInProgress;
    private DateTimeOffset _lastTrayStatusRefreshRequestUtc = DateTimeOffset.MinValue;
    private string _trayLlamaStatusResourceKey = "Stopped";
    private string _trayLlamaBackend = "–";
    private string? _appExecutablePath;
    private ModelProfile? _dragProfile;
    private System.Windows.Point _dragStartPoint;
    private DateTimeOffset _dragStartedAt;
    private bool _dragInProgress;
    private bool _reorderModeActive;
    private bool _reorderDirty;
    private bool _applyLocalProfileOnNextLaunch;
    private bool _pageTransitionInProgress;
    private bool _modelPanelTransitionInProgress;
    private bool _statusNotificationVisible = true;
    private IReadOnlyList<ModeCardItem> _renderedModeCards = Array.Empty<ModeCardItem>();
    private bool? _lastModeSelectionLocked;
    private bool _renderedModeCardsEnglish;
    private readonly SemaphoreSlim _windowVisibilityGate = new(1, 1);
    private bool _windowVisibilityTransitionInProgress;
    private bool _windowClosed;

    private void InitializeEggUi(string displayVersion)
    {
        Title = "Egg Launcher";
        HeaderVersionText.Text = $"v{displayVersion}";
        UpdateThemeToggleUi(AppThemeManager.CurrentTheme);
        _lastDownloadSummary = AppLanguageManager.Text("NoActiveDownload");
        LauncherTabs.SelectedItem = HomeTab;
        SetActiveNavigation(HomeNavButton);
        _appExecutablePath = ResolveAppExecutablePath();
        InitializeTrayIcon();
        RefreshDownloadUi();
        if (_startHidden)
        {
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowState = WindowState.Minimized;
            Opacity = 0;
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _trayOpenItem = new Forms.ToolStripMenuItem(AppLanguageManager.Text("OpenEggLauncher"));
        _trayOpenItem.Click += (_, _) => Dispatcher.BeginInvoke(
            new Action(async () => await ShowFromTrayAsync()));
        menu.Items.Add(_trayOpenItem);
        _trayLaunchItem = new Forms.ToolStripMenuItem(AppLanguageManager.Text("LaunchChatGpt"));
        _trayLaunchItem.Click += (_, _) => RunTrayLaunchAction();
        menu.Items.Add(_trayLaunchItem);

        _trayCurrentModeItem = new Forms.ToolStripMenuItem(
            AppLanguageManager.Format("TrayCurrentModeFormat", AppLanguageManager.Text("NotStarted")))
        {
            Enabled = false,
        };
        menu.Items.Add(_trayCurrentModeItem);

        _trayModeMenuItem = new Forms.ToolStripMenuItem(AppLanguageManager.Text("SwitchMode"));
        menu.Items.Add(_trayModeMenuItem);
        _trayExitItem = new Forms.ToolStripMenuItem(AppLanguageManager.Text("Exit"));
        _trayExitItem.Click += (_, _) => Dispatcher.BeginInvoke(
            new Action(async () => await RequestTrayExitAsync()));
        menu.Items.Add(_trayExitItem);
        menu.Opening += (_, _) =>
        {
            Dispatcher.Invoke(() => RefreshTrayMenuUi(forceClientRefresh: true));
            RequestTrayStatusRefresh(force: true);
        };

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Egg Launcher",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadTrayIcon(),
        };
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(new Action(async () => await ShowFromTrayAsync()));
            }
        };
        _trayIcon.MouseMove += (_, _) => RequestTrayStatusRefresh(force: false);
        RefreshTrayMenuUi();
    }

    private void RunTrayLaunchAction()
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await LaunchChatGptAsync("tray");
            RequestTrayStatusRefresh(force: true);
        }));
    }

    private void RequestTrayModeSelection(ProviderMode mode, string? modelId)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (IsModeSelectionLocked(forceClientRefresh: true))
            {
                RefreshTrayMenuUi();
                return;
            }

            try
            {
                await SavePendingSelectionAsync(mode, modelId);
            }
            catch (Exception exception)
            {
                StatusText.Text = mode == ProviderMode.OpenAI
                    ? AppLanguageManager.Choose($"保存预选模式失败：{exception.Message}", $"Failed to save the preselected mode: {exception.Message}")
                    : AppLanguageManager.Choose($"保存预选模型失败：{exception.Message}", $"Failed to save the preselected model: {exception.Message}");
            }

            RefreshTrayMenuUi();
        }));
    }

    private void RefreshTrayMenuUi(bool forceClientRefresh = false)
    {
        if (_trayLaunchItem is null || _trayCurrentModeItem is null || _trayModeMenuItem is null)
        {
            return;
        }

        if (_trayOpenItem is not null)
        {
            _trayOpenItem.Text = AppLanguageManager.Text("OpenEggLauncher");
        }

        if (_trayExitItem is not null)
        {
            _trayExitItem.Text = AppLanguageManager.Text("Exit");
        }

        _trayLaunchItem.Text = AppLanguageManager.Text("LaunchChatGpt");
        _trayModeMenuItem.Text = AppLanguageManager.Text("SwitchMode");
        var clientRunning = GetClientRunningSnapshot(forceClientRefresh);
        _trayLaunchItem.Enabled = CanLaunchChatGpt(clientRunning);
        _trayCurrentModeItem.Text = AppLanguageManager.Format(
            "TrayCurrentModeFormat",
            GetActualModeLabel(clientRunning));

        while (_trayModeMenuItem.DropDownItems.Count > 0)
        {
            var item = _trayModeMenuItem.DropDownItems[0];
            _trayModeMenuItem.DropDownItems.RemoveAt(0);
            item.Dispose();
        }

        var selectionLocked = _busy || _launchInProgress || clientRunning;
        var openAiItem = new Forms.ToolStripMenuItem("OpenAI")
        {
            Checked = _settings.PendingMode == ProviderMode.OpenAI,
            Enabled = !selectionLocked,
        };
        openAiItem.Click += (_, _) => RequestTrayModeSelection(ProviderMode.OpenAI, null);
        _trayModeMenuItem.DropDownItems.Add(openAiItem);

        foreach (var profile in _profiles.Where(profile => profile.ShowInModePage && !IsProfileUnavailable(profile)))
        {
            var modelId = profile.Id;
            var modelItem = new Forms.ToolStripMenuItem(profile.DisplayName)
            {
                Checked = _settings.PendingMode == ProviderMode.Local
                    && string.Equals(_settings.PendingModelId, modelId, StringComparison.Ordinal),
                Enabled = !selectionLocked,
                ToolTipText = profile.DisplayName,
            };
            modelItem.Click += (_, _) => RequestTrayModeSelection(ProviderMode.Local, modelId);
            _trayModeMenuItem.DropDownItems.Add(modelItem);
        }

        _trayModeMenuItem.Enabled = _trayModeMenuItem.DropDownItems.Count > 0;
    }

    private void RequestTrayStatusRefresh(bool force)
    {
        void RequestOnDispatcher()
        {
            var now = DateTimeOffset.UtcNow;
            if (_trayStatusRefreshInProgress
                || (!force && now - _lastTrayStatusRefreshRequestUtc < TimeSpan.FromSeconds(5)))
            {
                return;
            }

            _lastTrayStatusRefreshRequestUtc = now;
            UpdateTrayTooltip();
            _ = RefreshTrayStatusAsync();
        }

        if (Dispatcher.CheckAccess())
        {
            RequestOnDispatcher();
        }
        else
        {
            Dispatcher.BeginInvoke((Action)RequestOnDispatcher);
        }
    }

    private async Task RefreshTrayStatusAsync()
    {
        if (_trayStatusRefreshInProgress || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _trayStatusRefreshInProgress = true;
        try
        {
            try
            {
                var integration = await _inspector.InspectAsync(_lifetime.Token);
                _codexHome = integration.CodexHomePath;
                _configExists = integration.ConfigExists;
                _historyStateExists = integration.HistoryStateExists;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Retain the last verified configuration state after a transient inspection failure.
            }

            _trayLlamaStatusResourceKey = await ReadTrayLlamaStatusResourceKeyAsync();
            var llamaRunning = IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot);
            var profile = _profiles.FirstOrDefault(value => string.Equals(
                value.Id,
                _settings.SelectedModelId,
                StringComparison.Ordinal));
            UpdateLlamaBackendStatus(llamaRunning, profile);
            RenderEnvironmentStatus();
            RefreshTrayMenuUi();
            UpdateTrayTooltip();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _trayStatusRefreshInProgress = false;
        }
    }

    private async Task<string> ReadTrayLlamaStatusResourceKeyAsync()
    {
        if (!IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            return "Stopped";
        }

        try
        {
            var endpoint = await TryGetTrustedRouterEndpointAsync(_lifetime.Token);
            var profile = _profiles.FirstOrDefault(value => string.Equals(
                value.Id,
                _settings.SelectedModelId,
                StringComparison.Ordinal));
            if (endpoint is null || profile is null)
            {
                return "Running";
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            var management = new LlamaModelManagementClient(http);
            var models = await management.ListAsync(endpoint.BaseUri, cancellationToken: _lifetime.Token);
            var native = models.FirstOrDefault(model => string.Equals(model.Id, profile.Alias, StringComparison.Ordinal));
            if (native is null)
            {
                return "ModelStatusReading";
            }

            if (native.Failed)
            {
                return "StatusAbnormal";
            }

            return native.Status.ToLowerInvariant() switch
            {
                "loaded" => "ModelLoadedRunning",
                "loading" => "ModelLoadingRunning",
                "sleeping" => "ModelSleepingRunning",
                "unloaded" => "ModelUnloadedRunning",
                _ => "Running",
            };
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot)
                ? "StatusUnavailable"
                : "Stopped";
        }
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var clientRunning = GetClientRunningSnapshot();
        var activeProfile = _settings.SelectedMode == ProviderMode.Local
            ? _profiles.FirstOrDefault(profile => string.Equals(
                profile.Id,
                _settings.SelectedModelId,
                StringComparison.Ordinal))
            : null;
        var runningModel = clientRunning && activeProfile is not null
            ? activeProfile.DisplayName
            : "–";
        var lines = new[]
        {
            FormatTrayTooltipLine("TrayModeLabel", GetActualModeLabel(clientRunning)),
            FormatTrayTooltipLine("TrayChatGptLabel", AppLanguageManager.Text(clientRunning ? "Running" : "Closed")),
            FormatTrayTooltipLine("TrayConfigurationLabel", AppLanguageManager.Text(_configExists ? "Normal" : "NotFound")),
            FormatTrayTooltipLine("TrayLlamaServiceLabel", AppLanguageManager.Text(_trayLlamaStatusResourceKey)),
            FormatTrayTooltipLine("TrayLlamaBackendLabel", _trayLlamaBackend),
            FormatTrayTooltipLine("TrayRunningModelLabel", runningModel),
        };
        _trayIcon.Text = FitTrayTooltip(lines);
    }

    private static string FormatTrayTooltipLine(string labelResourceKey, string value) =>
        AppLanguageManager.IsEnglish
            ? $"{AppLanguageManager.Text(labelResourceKey)}: {value}"
            : $"{AppLanguageManager.Text(labelResourceKey)}：{value}";

    private static string FitTrayTooltip(IReadOnlyList<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length <= MaximumTrayTooltipLength || lines.Count == 0)
        {
            return text;
        }

        var prefix = string.Join(Environment.NewLine, lines.Take(lines.Count - 1)) + Environment.NewLine;
        var available = MaximumTrayTooltipLength - prefix.Length;
        if (available <= 1)
        {
            return text[..MaximumTrayTooltipLength];
        }

        var lastLine = lines[^1];
        return prefix + (lastLine.Length <= available
            ? lastLine
            : lastLine[..(available - 1)] + "…");
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Launcher.App;component/Assets/egg-launcher.ico"));
            if (resource?.Stream is null)
            {
                return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
            }

            using var stream = resource.Stream;
            using var icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            return (Drawing.Icon)icon.Clone();
        }
        catch
        {
            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }
    }

    private static string? ResolveAppExecutablePath()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "Launcher.App.exe");
        return File.Exists(packaged) ? packaged : Environment.ProcessPath;
    }

    private async Task HideToTrayAsync()
    {
        await _windowVisibilityGate.WaitAsync();
        _windowVisibilityTransitionInProgress = true;
        try
        {
            if (_windowClosed || _trayExitRequested)
            {
                return;
            }

            _statusRefreshTimer.Stop();
            _telemetryRefreshTimer.Stop();
            if (IsVisible && Opacity > 0 && UiMotion.AnimationsEnabled)
            {
                IsHitTestVisible = false;
                if (!await UiMotion.FadeToAsync(this, 0, UiMotion.FastMilliseconds))
                {
                    return;
                }
            }

            Hide();
            ShowInTaskbar = false;
            Opacity = 1;
            IsHitTestVisible = true;
        }
        finally
        {
            _windowVisibilityTransitionInProgress = false;
            _windowVisibilityGate.Release();
        }
    }

    private async Task ShowFromTrayAsync(bool animate = true, bool refresh = true)
    {
        await _windowVisibilityGate.WaitAsync();
        _windowVisibilityTransitionInProgress = true;
        try
        {
            if (_windowClosed)
            {
                return;
            }

            Opacity = animate && UiMotion.AnimationsEnabled ? 0 : 1;
            IsHitTestVisible = !animate || !UiMotion.AnimationsEnabled;
            ShowActivated = true;
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            AdaptiveWindowSizing.ClampToCurrentWorkArea(this);
            ApplyAdaptiveLayout(force: true);
            Activate();
            if (animate && UiMotion.AnimationsEnabled
                && !await UiMotion.FadeToAsync(this, 1, UiMotion.StandardMilliseconds))
            {
                return;
            }

            IsHitTestVisible = true;
            if (refresh && !_trayExitRequested && !_lifetime.IsCancellationRequested)
            {
                _statusRefreshTimer.Start();
                _telemetryRefreshTimer.Start();
                await RefreshVisibleUiAsync();
            }
        }
        finally
        {
            _windowVisibilityTransitionInProgress = false;
            _windowVisibilityGate.Release();
        }
    }

    internal async void RestoreFromExternalActivation() => await ShowFromTrayAsync();

    private async Task RefreshVisibleUiAsync()
    {
        _ = GetClientRunningSnapshot(force: true);
        RenderEnvironmentStatus();
        RefreshEggUiState();
        await RefreshMonitoringAsync(forceHardwareRefresh: false);
    }

    private void ResumeVisibleWindowAfterCanceledExit()
    {
        if (_windowClosed || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        _statusRefreshTimer.Start();
        _telemetryRefreshTimer.Start();
        _ = RefreshVisibleUiAsync();
    }

    private async Task RequestTrayExitAsync()
    {
        if (_windowClosed || _trayExitRequested)
        {
            return;
        }

        _trayExitRequested = true;
        await ShowFromTrayAsync(animate: false, refresh: false);
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
        _trayOpenItem = null;
        _trayLaunchItem = null;
        _trayCurrentModeItem = null;
        _trayModeMenuItem = null;
        _trayExitItem = null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _statusRefreshTimer.Stop();
            _telemetryRefreshTimer.Stop();
            return;
        }

        if (!_windowVisibilityTransitionInProgress && IsVisible && ShowInTaskbar)
        {
            _statusRefreshTimer.Start();
            _telemetryRefreshTimer.Start();
            _ = RefreshVisibleUiAsync();
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private async void CloseButton_Click(object sender, RoutedEventArgs e) => await HideToTrayAsync();

    private void DismissStatusButton_Click(object sender, RoutedEventArgs e)
    {
        _statusNotificationTimer.Stop();
        StatusText.Text = string.Empty;
    }

    private async void StatusText_TextChanged(object? sender, EventArgs e)
    {
        RestartStatusNotificationTimer();
        var hasText = !string.IsNullOrWhiteSpace(StatusText.Text);
        if (hasText)
        {
            if (!_statusNotificationVisible || StatusNotificationBar.Visibility != Visibility.Visible)
            {
                _statusNotificationVisible = true;
                _ = await UiMotion.AnimateEntranceAsync(
                    StatusNotificationBar,
                    offsetY: 8,
                    durationMilliseconds: UiMotion.StandardMilliseconds);
            }
            else
            {
                UiMotion.AnimateRefresh(StatusText);
            }

            return;
        }

        if (!_statusNotificationVisible || StatusNotificationBar.Visibility != Visibility.Visible)
        {
            return;
        }

        _statusNotificationVisible = false;
        if (!await UiMotion.AnimateExitAsync(
            StatusNotificationBar,
            offsetY: 4,
            durationMilliseconds: UiMotion.FastMilliseconds))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusNotificationBar.Visibility = Visibility.Collapsed;
            UiMotion.Reset(StatusNotificationBar);
        }
    }

    private void StatusNotificationBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        _statusNotificationTimer.Stop();

    private void StatusNotificationBar_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        RestartStatusNotificationTimer();

    private void StatusNotificationTimer_Tick(object? sender, EventArgs e)
    {
        _statusNotificationTimer.Stop();
        if (_busy || StatusNotificationBar.IsMouseOver)
        {
            return;
        }

        StatusText.Text = string.Empty;
    }

    private void RestartStatusNotificationTimer()
    {
        _statusNotificationTimer.Stop();
        if (_busy
            || StatusNotificationBar.IsMouseOver
            || string.IsNullOrWhiteSpace(StatusText.Text))
        {
            return;
        }

        _statusNotificationTimer.Start();
    }

    private async void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_themeUiReady || _busy || _themeChangeInProgress)
        {
            return;
        }

        var previousTheme = _settings.Theme;
        var nextTheme = previousTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
        _themeChangeInProgress = true;
        ThemeToggleButton.IsEnabled = false;
        try
        {
            AppThemeManager.Apply(nextTheme);
            UpdateThemeToggleUi(nextTheme);
            UiMotion.AnimateRefresh(MainContentGrid);
            await MutateSettingsAsync(
                settings => settings with { Theme = nextTheme },
                _lifetime.Token);
            StatusText.Text = AppLanguageManager.Text(
                nextTheme == AppTheme.Light ? "LightThemeEnabled" : "DarkThemeEnabled");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppThemeManager.Apply(previousTheme);
            UpdateThemeToggleUi(previousTheme);
            StatusText.Text = AppLanguageManager.Format("ThemeSaveFailed", exception.Message);
        }
        finally
        {
            _themeChangeInProgress = false;
            ThemeToggleButton.IsEnabled = _themeUiReady && !_busy;
        }
    }

    private void UpdateThemeToggleUi(AppTheme theme)
    {
        var light = theme == AppTheme.Light;
        ThemeToggleIcon.ContentTemplate = (DataTemplate)FindResource(
            light ? "LightbulbOnIconTemplate" : "LightbulbOffIconTemplate");
        ThemeToggleIcon.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            light ? "AccentBrush" : "PrimaryBrush");
        ThemeToggleButton.SetResourceReference(
            FrameworkElement.ToolTipProperty,
            light ? "SwitchToDarkTheme" : "SwitchToLightTheme");
    }

    private async void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_themeUiReady || _busy || _languageChangeInProgress)
        {
            return;
        }

        var nextLanguage = AppLanguageManager.CurrentLanguage == AppLanguage.English
            ? AppLanguage.Chinese
            : AppLanguage.English;
        await ChangeLanguagePreferenceAsync(nextLanguage);
    }

    private async void FollowSystemLanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_themeUiReady || _busy || _languageChangeInProgress)
        {
            return;
        }

        await ChangeLanguagePreferenceAsync(AppLanguage.System);
    }

    private async Task ChangeLanguagePreferenceAsync(AppLanguage nextPreference)
    {
        var previousLanguage = _settings.Language;
        if (previousLanguage == nextPreference)
        {
            UpdateLanguageToggleUi(previousLanguage);
            StatusText.Text = AppLanguageManager.Text("SystemLanguageEnabled");
            return;
        }

        _languageChangeInProgress = true;
        LanguageToggleButton.IsEnabled = false;
        try
        {
            AppLanguageManager.Apply(nextPreference);
            UiMotion.AnimateRefresh(MainContentGrid);
            await MutateSettingsAsync(
                settings => settings with { Language = nextPreference },
                _lifetime.Token);
            RefreshLocalizedUi();
            StatusText.Text = AppLanguageManager.Text(
                nextPreference == AppLanguage.System ? "SystemLanguageEnabled" : "LanguageChanged");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLanguageManager.Apply(previousLanguage);
            RefreshLocalizedUi();
            StatusText.Text = AppLanguageManager.Format("LanguageSaveFailed", exception.Message);
        }
        finally
        {
            _languageChangeInProgress = false;
            LanguageToggleButton.IsEnabled = _themeUiReady && !_busy;
        }
    }

    private void UpdateLanguageToggleUi(AppLanguage preference)
    {
        if (preference == AppLanguage.System)
        {
            LanguageToggleButton.ToolTip = AppLanguageManager.Format(
                "SystemLanguageToolTip",
                AppLanguageManager.Text(AppLanguageManager.IsEnglish
                    ? "EnglishLanguageName"
                    : "ChineseLanguageName"));
            return;
        }

        LanguageToggleButton.SetResourceReference(
            FrameworkElement.ToolTipProperty,
            "SwitchLanguageToolTip");
    }

    private void RefreshLocalizedUi()
    {
        var selectedProfileId = (ManagedModelsList.SelectedItem as ModelListItem)?.Profile?.Id;
        var selectedCandidatePath = (DiscoveredModelsList.SelectedItem as ModelListItem)?.Candidate?.PrimaryPath;
        UpdateThemeToggleUi(_settings.Theme);
        UpdateLanguageToggleUi(_settings.Language);
        RenderEnvironmentStatus();
        HardwareInventoryText.Text = FormatHomeHardwareInventory(_hardwareInventory, _runtimeDeviceOutput);
        if (_runtimeIsValid)
        {
            RuntimeStatusText.Text = AppLanguageManager.Choose("Runtime 已验证。", "Runtime validated.");
            DeviceText.Text = string.IsNullOrWhiteSpace(_runtimeDeviceOutput)
                ? string.Empty
                : CompactDeviceOutput(_runtimeDeviceOutput);
        }
        else
        {
            RuntimeStatusText.Text = AppLanguageManager.Text("RuntimeNotConfigured");
        }
        var managedItems = _profiles.Select(CreateModelListItem).ToArray();
        var discoveredItems = _discoveredModels.Select(ModelListItem.FromCandidate).ToArray();
        ManagedModelsList.ItemsSource = managedItems;
        DiscoveredModelsList.ItemsSource = discoveredItems;
        ManagedModelsList.SelectedItem = managedItems.FirstOrDefault(item =>
            string.Equals(item.Profile?.Id, selectedProfileId, StringComparison.Ordinal));
        DiscoveredModelsList.SelectedItem = discoveredItems.FirstOrDefault(item =>
            string.Equals(item.Candidate?.PrimaryPath, selectedCandidatePath, StringComparison.OrdinalIgnoreCase));
        if (!HasActiveOrQueuedDownloads && _downloadHistory.Count == 0)
        {
            _lastDownloadSummary = AppLanguageManager.Text("NoActiveDownload");
        }
        RefreshEggUiState();
        RefreshDownloadUi(refreshLaunchState: false);
        RefreshTrayMenuUi();
        UpdateTrayTooltip();
    }

    private async void HomeNavButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateMainPageAsync(HomeTab, HomeNavButton);
    }

    private async void ModeNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await NavigateMainPageAsync(LocalModelsTab, ModeNavButton))
        {
            return;
        }

        if (_runtimeIsValid && !string.IsNullOrWhiteSpace(_settings.LlamaRoot) && !_busy)
        {
            await RefreshProfilesForNavigationAsync();
        }
        RefreshEggUiState();
    }

    private async void ModelsNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await NavigateMainPageAsync(RuntimeMonitorTab, ModelsNavButton))
        {
            return;
        }

        if (_runtimeIsValid && !string.IsNullOrWhiteSpace(_settings.LlamaRoot) && !_busy)
        {
            await RefreshProfilesForNavigationAsync();
        }
    }

    private async Task<bool> NavigateMainPageAsync(TabItem targetTab, System.Windows.Controls.Button activeButton)
    {
        if (_pageTransitionInProgress)
        {
            return false;
        }

        if (ReferenceEquals(LauncherTabs.SelectedItem, targetTab))
        {
            SetActiveNavigation(activeButton);
            return true;
        }

        _pageTransitionInProgress = true;
        try
        {
            var oldIndex = LauncherTabs.SelectedIndex;
            var newIndex = LauncherTabs.Items.IndexOf(targetTab);
            if (LauncherTabs.SelectedItem is TabItem { Content: FrameworkElement outgoing })
            {
                await UiMotion.AnimateExitAsync(
                    outgoing,
                    offsetX: newIndex >= oldIndex ? -6 : 6,
                    offsetY: 0);
                UiMotion.Reset(outgoing);
            }

            LauncherTabs.SelectedItem = targetTab;
            SetActiveNavigation(activeButton);
            if (targetTab.Content is FrameworkElement incoming)
            {
                await UiMotion.AnimateEntranceAsync(
                    incoming,
                    offsetX: newIndex >= oldIndex ? 10 : -10,
                    offsetY: 0);
            }

            return true;
        }
        finally
        {
            _pageTransitionInProgress = false;
        }
    }

    private async Task RefreshProfilesForNavigationAsync()
    {
        try
        {
            await ReloadProfilesAsync(_settings.LlamaRoot!, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"刷新本地模型失败：{exception.Message}", $"Failed to refresh local models: {exception.Message}");
        }
    }

    private void SetActiveNavigation(System.Windows.Controls.Button active)
    {
        foreach (var button in new[] { HomeNavButton, ModeNavButton, ModelsNavButton })
        {
            var selected = ReferenceEquals(button, active);
            if (selected)
            {
                button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "NavigationSelectedBrush");
                button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            }
            else
            {
                button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            }
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow(GetDisplayVersion())
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private async void ManageModelsButton_Click(object sender, RoutedEventArgs e)
    {
        _ = GetClientRunningSnapshot(force: true);
        try
        {
            if (_runtimeIsValid && !string.IsNullOrWhiteSpace(_settings.LlamaRoot))
            {
                await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"刷新本地模型状态失败：{exception.Message}",
                $"Failed to refresh local model status: {exception.Message}");
        }

        UpdateManagedModelMutationButtons();
        await ShowModelPanelAsync(ModelManagementPanel);
    }

    private async void CloseModelManagementButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowModelPanelAsync(ModelCardsGrid);
    }

    private async void DownloadManagerButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowModelPanelAsync(DownloadManagementPanel);
    }

    private async void CloseDownloadManagerButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowModelPanelAsync(ModelCardsGrid);
    }

    private async Task ShowModelPanelAsync(FrameworkElement target)
    {
        if (_modelPanelTransitionInProgress || target.Visibility == Visibility.Visible)
        {
            return;
        }

        _modelPanelTransitionInProgress = true;
        try
        {
            var panels = new FrameworkElement[] { ModelCardsGrid, ModelManagementPanel, DownloadManagementPanel };
            var outgoing = panels
                .First(panel => panel.Visibility == Visibility.Visible);
            foreach (var panel in panels)
            {
                if (!ReferenceEquals(panel, outgoing) && !ReferenceEquals(panel, target))
                {
                    panel.Visibility = Visibility.Collapsed;
                    UiMotion.Reset(panel);
                }
            }

            await UiMotion.SwapAsync(outgoing, target);
        }
        finally
        {
            _modelPanelTransitionInProgress = false;
        }

        RuntimeMonitorScrollViewer.ScrollToTop();
    }

    private async void ModeCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsModeCardActionSource(e.OriginalSource)) return;
        if (_dragInProgress || _reorderModeActive)
        {
            _dragInProgress = false;
            return;
        }

        if (sender is not Border { DataContext: ModeCardItem item })
        {
            return;
        }

        if (!item.IsOpenAi && item.Profile is null)
        {
            return;
        }

        if (IsModeSelectionLocked(forceClientRefresh: true))
        {
            StatusText.Text = item.IsOpenAi
                ? AppLanguageManager.Choose("ChatGPT 正在运行；关闭客户端后才能预选其他模式。", "ChatGPT is running. Close the client before preselecting another mode.")
                : AppLanguageManager.Choose("ChatGPT 正在运行；关闭客户端后才能预选其他模型。", "ChatGPT is running. Close the client before preselecting another model.");
            return;
        }

        try
        {
            await SavePendingSelectionAsync(
                item.IsOpenAi ? ProviderMode.OpenAI : ProviderMode.Local,
                item.Profile?.Id);
        }
        catch (Exception exception)
        {
            StatusText.Text = item.IsOpenAi
                ? AppLanguageManager.Choose($"保存预选模式失败：{exception.Message}", $"Failed to save the preselected mode: {exception.Message}")
                : AppLanguageManager.Choose($"保存预选模型失败：{exception.Message}", $"Failed to save the preselected model: {exception.Message}");
        }
    }

    private async Task SavePendingSelectionAsync(ProviderMode mode, string? modelId)
    {
        if (_settings.PendingMode == mode
            && string.Equals(_settings.PendingModelId, modelId, StringComparison.Ordinal))
        {
            return;
        }

        var pendingProfile = mode == ProviderMode.Local
            ? _profiles.First(profile => string.Equals(profile.Id, modelId, StringComparison.Ordinal))
            : null;
        await MutateSettingsAsync(
            settings => settings with
            {
                PendingMode = mode,
                PendingModelId = modelId,
                PendingModelRelativePath = pendingProfile?.ModelRelativePath,
                PendingModelDisplayName = pendingProfile?.DisplayName,
                PendingSelectionInitialized = true,
            },
            _lifetime.Token);
        StatusText.Text = mode == ProviderMode.OpenAI
            ? AppLanguageManager.Choose("已预选 OpenAI；点击首页启动按钮时才会应用配置。", "OpenAI is preselected. Configuration is applied only when the Home launch button is clicked.")
            : AppLanguageManager.Choose(
                $"已预选 {_profiles.First(profile => profile.Id == modelId).DisplayName}；点击首页启动按钮时才会应用配置。",
                $"{_profiles.First(profile => profile.Id == modelId).DisplayName} is preselected. Configuration is applied only when the Home launch button is clicked.");
        RefreshEggUiState();
    }

    private async Task InitializePendingSelectionAsync(CancellationToken cancellationToken)
    {
        if (_settings.PendingSelectionInitialized)
        {
            return;
        }

        await MutateSettingsAsync(
            settings => settings with
            {
                PendingMode = settings.SelectedMode,
                PendingModelId = settings.SelectedMode == ProviderMode.Local ? settings.SelectedModelId : null,
                PendingModelRelativePath = settings.SelectedMode == ProviderMode.Local
                    ? _profiles.FirstOrDefault(profile => string.Equals(
                        profile.Id,
                        settings.SelectedModelId,
                        StringComparison.Ordinal))?.ModelRelativePath
                    : null,
                PendingModelDisplayName = settings.SelectedMode == ProviderMode.Local
                    ? _profiles.FirstOrDefault(profile => string.Equals(
                        profile.Id,
                        settings.SelectedModelId,
                        StringComparison.Ordinal))?.DisplayName
                    : null,
                PendingSelectionInitialized = true,
            },
            cancellationToken);
    }

    private void RefreshEggUiState(bool refreshModeCards = true)
    {
        var clientRunning = GetClientRunningSnapshot();
        LaunchChatGptButton.IsEnabled = CanLaunchChatGpt(clientRunning);
        UpdateLocalAuthenticationUi();

        var selectionLocked = IsModeSelectionLocked();
        ModePageHintText.Text = selectionLocked
            ? AppLanguageManager.Choose("模式选择已锁定", "Mode selection is locked")
            : _reorderModeActive
                ? AppLanguageManager.Choose(
                    "正在排序 · 拖动卡片，点击空白处结束并保存",
                    "Sorting · Drag cards; click empty space to finish and save")
                : AppLanguageManager.Choose(
                    "单击预选，启动时应用 · 长按拖动排序",
                    "Click to preselect; applied at launch · Hold and drag to reorder");

        if (refreshModeCards || _lastModeSelectionLocked != selectionLocked)
        {
            RefreshModeCards(selectionLocked);
        }

        _lastModeSelectionLocked = selectionLocked;
        UpdateManagedModelMutationButtons();
    }

    private void RefreshModeCards(bool selectionLocked)
    {
        var modeCards = new List<ModeCardItem>
        {
            new(
                null,
                "OpenAI",
                AppLanguageManager.Choose("官方账户与在线模型", "Official account and online models"),
                IsOpenAi: true,
                _settings.PendingMode == ProviderMode.OpenAI,
                selectionLocked ? 0.5 : 1,
                CanEdit: false),
        };
        modeCards.AddRange(_profiles
            .Where(profile => profile.ShowInModePage && !IsProfileUnavailable(profile))
            .Select(profile => new ModeCardItem(
                profile,
                profile.DisplayName,
                string.Empty,
                IsOpenAi: false,
                _settings.PendingMode == ProviderMode.Local
                    && string.Equals(_settings.PendingModelId, profile.Id, StringComparison.Ordinal),
                selectionLocked ? 0.5 : 1,
                CanUseModelCardActions(profile)))
            .ToArray());
        var languageIsEnglish = AppLanguageManager.IsEnglish;
        if (_renderedModeCardsEnglish == languageIsEnglish
            && _renderedModeCards.Count == modeCards.Count
            && _renderedModeCards.Zip(modeCards).All(pair => pair.First.SamePresentation(pair.Second)))
        {
            foreach (var item in _renderedModeCards) item.RefreshConfiguration(_settings.LlamaRoot);
            return;
        }

        if (_configurationMenu is { IsOpen: true }) _configurationMenu.IsOpen = false;
        foreach (var item in modeCards) item.RefreshConfiguration(_settings.LlamaRoot);
        _renderedModeCards = modeCards;
        _renderedModeCardsEnglish = languageIsEnglish;
        ModeModelsItemsControl.ItemsSource = _renderedModeCards;
        ApplyAdaptiveLayout(force: false);
    }

    private bool CanLaunchChatGpt(bool clientRunning)
    {
        if (_busy || _launchInProgress || _modelDialogOpening || _configurationSelectionInProgress || clientRunning)
        {
            return false;
        }

        return _settings.PendingMode == ProviderMode.OpenAI
            || (_settings.PendingMode == ProviderMode.Local
                && _profiles.Any(profile => string.Equals(
                    profile.Id,
                    _settings.PendingModelId,
                    StringComparison.Ordinal)
                    && !IsProfileUnavailable(profile)
                    && _settings.LlamaRoot is { } root && ModelConfigurationSelection.Inspect(profile, root).Exists));
    }

    private string GetActualModeLabel(bool clientRunning) => !clientRunning
        ? AppLanguageManager.Text("NotStarted")
        : _settings.SelectedMode == ProviderMode.OpenAI
            ? "OpenAI"
            : "Local LLM";

    private bool IsModeSelectionLocked(bool forceClientRefresh = false) =>
        _busy || _launchInProgress || _modelDialogOpening || _configurationSelectionInProgress || GetClientRunningSnapshot(forceClientRefresh);

    private bool CanEditProfile(ModelProfile profile, bool forceClientRefresh = false)
    {
        if (!_runtimeIsValid || IsProfileUnavailable(profile))
        {
            return false;
        }

        return !IsManagedProfileInUse(profile, forceClientRefresh);
    }

    private async void ModeCardSettings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: ModelProfile profile } && CanUseModelCardActions(profile, force: true))
        {
            await EditProfileAsync(profile);
        }
    }

    private async Task EditProfileAsync(ModelProfile profile)
    {
        if (_modelDialogOpening) return;
        _modelDialogOpening = true;
        RefreshEggUiState();
        try { await EditProfileCoreAsync(profile); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose("打开模型参数失败：", "Cannot open model settings: ") + exception.Message;
            MessageBox.Show(this, StatusText.Text, AppLanguageManager.Choose("模型参数", "Model settings"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _modelDialogOpening = false; RefreshEggUiState(); }
    }

    private async Task EditProfileCoreAsync(ModelProfile profile)
    {
        if (_busy || _launchInProgress || _configurationSelectionInProgress || string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            return;
        }
        profile = await _profileStore.ReadRegistrationAsync(_settings.LlamaRoot, profile, _lifetime.Token);
        _ = ModelConfigurationSelection.ListFiles(profile, _settings.LlamaRoot);
        profile = await ReadSelectedConfigurationForEditorAsync(profile, _settings.LlamaRoot);

        if (!CanEditProfile(profile, forceClientRefresh: true))
        {
            StatusText.Text = AppLanguageManager.Choose("该模型正被 ChatGPT 使用；关闭客户端后才能保存它的运行参数。", "This model is in use by ChatGPT. Close the client before saving its runtime parameters.");
            return;
        }

        if (profile.ModelType == ModelType.Unknown)
        {
            StatusText.Text = AppLanguageManager.Choose("尚未指定该模型是 Dense 还是 MoE；请在本地模型管理 → 模型管理 → 模型类型中指定。", "Specify Dense or MoE in Local Model Management → Model management → Model Type first.");
            MessageBox.Show(
                this,
                StatusText.Text,
                AppLanguageManager.Choose("需要指定模型类型", "Model Type Required"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await Launcher.Scripts.Templates.ModelResourceCatalog.EnsureMigratedAsync(profile, _settings.LlamaRoot, _lifetime.Token);
        profile = Launcher.Scripts.Templates.ModelResourceCatalog.Resolve(profile, _settings.LlamaRoot);
        var capabilities = CanRunModelResourceDetection()
            ? await Launcher.Runtime.Detection.LlamaRuntimeOptionDetector.DetectAsync(_settings.LlamaRoot, _lifetime.Token)
            : null;
        var editor = new ProfileEditorWindow(profile, _settings.LlamaRoot, capabilities, DetectContextShiftAsync, null, DetectEditorReasoningAsync,
            candidate => CanEditProfile(candidate, forceClientRefresh: true), CanRunModelResourceDetection, _diagnostics)
        { Owner = this };
        editor.ShowDialog();
        await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
    }

    private async void ModeCardName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: ModelProfile profile })
        {
            e.Handled = true;
            await ShowProfileDetailsAsync(profile);
        }
    }

    private async Task ShowProfileDetailsAsync(ModelProfile profile)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var text = FormatProfileDetails(profile);
            var endpoint = await TryGetTrustedRouterEndpointAsync(_lifetime.Token);
            if (endpoint is not null)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                var models = await new Launcher.Runtime.Router.LlamaModelManagementClient(http).ListAsync(
                    endpoint.BaseUri,
                    cancellationToken: _lifetime.Token);
                var native = models.FirstOrDefault(model =>
                    string.Equals(model.Id, profile.Alias, StringComparison.Ordinal)
                    || string.Equals(model.Id, profile.RemoteModelId, StringComparison.Ordinal));
                if (native is not null)
                {
                    text += AppLanguageManager.Choose("\n\nllama.cpp 当前数据\n", "\n\nCurrent llama.cpp Data\n") + FormatNativeModelDetails(native);
                }
            }

            MessageBox.Show(this, text, profile.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = AppLanguageManager.Choose("模型详情读取完成；未加载或切换模型。", "Model details loaded. No model was loaded or switched.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"读取模型详情失败：{exception.Message}", $"Failed to read model details: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ModeCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsModeCardActionSource(e.OriginalSource)) { _dragProfile = null; return; }
        if (IsModeSelectionLocked())
        {
            return;
        }

        _dragProfile = (sender as Border)?.Tag as ModelProfile;
        _dragStartPoint = e.GetPosition(this);
        _dragStartedAt = DateTimeOffset.UtcNow;
        _dragInProgress = false;
    }

    private void ModeCard_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragProfile is null || e.LeftButton != MouseButtonState.Pressed || IsModeSelectionLocked())
        {
            return;
        }

        var point = e.GetPosition(this);
        var moved = Math.Abs(point.X - _dragStartPoint.X) + Math.Abs(point.Y - _dragStartPoint.Y);
        if (DateTimeOffset.UtcNow - _dragStartedAt < TimeSpan.FromMilliseconds(450) || moved < 8)
        {
            return;
        }

        _reorderModeActive = true;
        _dragInProgress = true;
        RefreshEggUiState();
        DragDrop.DoDragDrop((DependencyObject)sender, _dragProfile, DragDropEffects.Move);
    }

    private void ModeCard_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not Border { Tag: ModelProfile target }
            || e.Data.GetData(typeof(ModelProfile)) is not ModelProfile source
            || string.Equals(source.Id, target.Id, StringComparison.Ordinal))
        {
            return;
        }

        var ordered = _profiles.OrderBy(profile => profile.DisplayOrder).ThenBy(profile => profile.DisplayName).ToList();
        var sourceIndex = ordered.FindIndex(profile => profile.Id == source.Id);
        var targetIndex = ordered.FindIndex(profile => profile.Id == target.Id);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var moved = ordered[sourceIndex];
        ordered.RemoveAt(sourceIndex);
        ordered.Insert(targetIndex, moved);
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index] = ordered[index] with { DisplayOrder = index };
        }

        _profiles = ordered;
        _reorderDirty = true;
        StatusText.Text = AppLanguageManager.Choose("卡片顺序已调整；点击空白处结束排序并保存。", "Card order changed. Click empty space to finish sorting and save.");
        RefreshEggUiState();
    }

    private async void ModePage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_reorderModeActive || FindTaggedModelCard(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        await FinishReorderAsync();
    }

    private static Border? FindTaggedModelCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Border { Tag: ModelProfile } border)
            {
                return border;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async Task FinishReorderAsync()
    {
        _reorderModeActive = false;
        if (!_reorderDirty || string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            RefreshEggUiState();
            return;
        }

        try
        {
            SetBusy(true);
            foreach (var profile in _profiles.OrderBy(profile => profile.DisplayOrder))
            {
                await _profileStore.SaveRegistrationAsync(_settings.LlamaRoot, profile, _lifetime.Token);
            }

            _reorderDirty = false;
            StatusText.Text = AppLanguageManager.Choose("本地模型卡片顺序已保存。", "Local model card order saved.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"保存卡片顺序失败：{exception.Message}", $"Failed to save card order: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LaunchChatGptButton_Click(object sender, RoutedEventArgs e) =>
        await LaunchChatGptAsync();

    private async Task LaunchChatGptAsync(string source = "home")
    {
        await using var operation = _diagnostics.Begin("launch_request", new Dictionary<string, object?>
        { ["source"] = source, ["pendingMode"] = _settings.PendingMode?.ToString(), ["modelId"] = _settings.PendingModelId });
        if (_busy || _launchInProgress || _modelDialogOpening || _configurationSelectionInProgress || GetClientRunningSnapshot(force: true))
        {
            await operation.CompleteAsync("blocked", "client_running_or_operation_busy");
            RefreshEggUiState();
            return;
        }

        await WaitForSettingsMutationsAsync();
        if (_busy || _launchInProgress || _modelDialogOpening || _configurationSelectionInProgress || GetClientRunningSnapshot(force: true))
        {
            await operation.CompleteAsync("blocked", "client_running_or_operation_busy");
            RefreshEggUiState();
            return;
        }

        var pendingMode = _settings.PendingMode;
        var pendingModelId = _settings.PendingModelId;
        _launchInProgress = true;
        SetBusy(true);
        try
        {
            if (pendingMode == ProviderMode.OpenAI)
            {
                await ActivateOpenAiAsync(busyStateOwnedByCaller: true);
                await operation.CompleteAsync("dispatched");
                return;
            }

            if (pendingMode != ProviderMode.Local
                || _profiles.FirstOrDefault(profile => string.Equals(profile.Id, pendingModelId, StringComparison.Ordinal)) is not { } profile)
            {
                await operation.CompleteAsync("blocked", "no_mode_or_model_preselected");
                StatusText.Text = AppLanguageManager.Choose("请先在模式页面预选 OpenAI 或一个有效的本地模型。", "Preselect OpenAI or a valid local model on the Mode page first.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.LlamaRoot)
                || !GetOwnedModelPaths(_settings.LlamaRoot, profile).All(File.Exists))
            {
                if (!string.IsNullOrWhiteSpace(_settings.LlamaRoot))
                {
                    await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
                }
                else
                {
                    await ClearPendingSelectionAsync();
                }

                await operation.CompleteAsync("blocked", "model_file_missing");
                StatusText.Text = AppLanguageManager.Choose("预选模型文件不存在或分片不完整；列表记录已保留并标记为不可用。请恢复文件后重新扫描添加。", "The preselected model file is missing or its shards are incomplete. Its list entry was retained and marked unavailable. Restore the files, then scan and add it again.");
                return;
            }

            var profilePath = Path.Combine(
                JsonModelProfileStore.GetProfilesDirectory(_settings.LlamaRoot),
                profile.Id + ".json");
            if (!File.Exists(profilePath))
            {
                profile = await RecreateDefaultProfileAsync(
                    _settings.LlamaRoot,
                    profile,
                    _lifetime.Token);
                await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
                _profilesRecreatedFromMissingConfiguration.Add(profile.Id);
                StatusText.Text = AppLanguageManager.Choose($"{profile.DisplayName} 的配置文件已丢失，已按默认参数重建后继续启动。", $"The configuration for {profile.DisplayName} was missing. It was rebuilt with defaults before launch continued.");
            }

            if (HasActiveOrQueuedDownloads)
            {
                if (!IsVisible || WindowState == WindowState.Minimized)
                {
                    await ShowFromTrayAsync();
                }

                var answer = MessageBox.Show(
                    this,
                    AppLanguageManager.Choose(
                        "当前存在模型下载任务。启动本地模型会先停止下载服务，并将任务保留到 Local 会话结束后继续。\n\n是否暂停下载并启动本地模型？",
                        "Model downloads are active. Starting a local model stops the download service and retains the tasks until the Local session ends.\n\nPause downloads and start the local model?"),
                    AppLanguageManager.Choose("本地模型优先", "Prioritize Local Model"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.Yes);
                if (answer != MessageBoxResult.Yes)
                {
                    await operation.CompleteAsync("cancelled", "downloads_continue");
                    StatusText.Text = AppLanguageManager.Choose("已取消启动，下载任务继续运行。", "Launch canceled; downloads continue.");
                    return;
                }

                _localLaunchReservationActive = true;
                if (!await SuspendDownloadsForLocalLaunchAsync())
                {
                    return;
                }
            }
            else if (IsAgentRunning())
            {
                await operation.CompleteAsync("blocked", "local_agent_running");
                StatusText.Text = AppLanguageManager.Choose("当前仍有 Egg Local Agent 正在运行。为避免 Router 冲突，本次未启动本地模型。", "An Egg Local Agent is still running. The local model was not started to avoid a Router conflict.");
                return;
            }
            else if (GetModelDownloadBlockReason() is { } llamaConflict)
            {
                await operation.CompleteAsync("blocked", "local_service_conflict");
                StatusText.Text = AppLanguageManager.Choose($"检测到 llama 服务冲突，本次未启动本地模型：{llamaConflict}", $"A llama service conflict was detected; the local model was not started: {llamaConflict}");
                return;
            }

            var configurationWasRecreated = _profilesRecreatedFromMissingConfiguration.Remove(profile.Id);
            await ActivateLocalAsync(
                profile,
                applyProfileOnLaunch: true,
                busyStateOwnedByCaller: true);
            await operation.CompleteAsync("dispatched");
            if (configurationWasRecreated)
            {
                StatusText.Text = AppLanguageManager.Choose($"未发现 {profile.DisplayName} 的配置文件，已使用默认参数。{StatusText.Text}", $"No configuration was found for {profile.DisplayName}; default parameters were used. {StatusText.Text}");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception);
            StatusText.Text = AppLanguageManager.Choose($"启动 ChatGPT 失败：{exception.Message}", $"Failed to launch ChatGPT: {exception.Message}");
        }
        finally
        {
            _localLaunchReservationActive = false;
            _launchInProgress = false;
            SetBusy(false);
            await TryResumeSuspendedDownloadsAsync();
        }
    }

    private async Task ClearPendingSelectionAsync()
    {
        await MutateSettingsAsync(
            settings => settings with
            {
                PendingMode = null,
                PendingModelId = null,
                PendingModelRelativePath = null,
                PendingModelDisplayName = null,
            },
            _lifetime.Token);
        RefreshEggUiState();
    }

    private async Task<ModelProfile> RecreateDefaultProfileAsync(
        string runtimeRoot,
        ModelProfile previous,
        CancellationToken cancellationToken)
    {
        var paths = GetOwnedModelPaths(runtimeRoot, previous);
        var primaryPath = paths[0];
        var candidate = new Launcher.Models.Scanning.GgufModelCandidate(
            primaryPath,
            Path.GetRelativePath(Path.Combine(runtimeRoot, "models"), primaryPath),
            previous.DisplayName,
            paths.Where(File.Exists).Sum(path => new FileInfo(path).Length),
            paths.Count,
            previous.RemoteModelId,
            previous.RemoteRepositoryId,
            previous.RemoteQuantization);
        var recreated = ModelProfileFactory.CreateDefault(
            candidate,
            runtimeRoot,
            _profiles.Where(profile => !string.Equals(profile.Id, previous.Id, StringComparison.Ordinal))) with
        {
            Id = previous.Id,
            Alias = previous.Alias,
            ShowInModePage = previous.ShowInModePage,
            DisplayOrder = previous.DisplayOrder,
        };
        await SaveProfileArtifactsAsync(recreated, cancellationToken);
        return recreated;
    }

    private Task<IReadOnlyList<ModelProfile>> ReconcileMissingProfilesAsync(
        string runtimeRoot,
        IReadOnlyList<ModelProfile> profiles,
        CancellationToken cancellationToken)
    {
        var modelsRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "models"));
        if (Directory.Exists(modelsRoot))
        {
            try
            {
                _ = Directory.EnumerateFileSystemEntries(modelsRoot).FirstOrDefault();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(profiles);
            }
        }

        _missingProfileIds.Clear();
        var newlyMissing = 0;
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelIsComplete = false;
            try
            {
                modelIsComplete = GetOwnedModelPaths(runtimeRoot, profile).All(File.Exists);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
            {
                modelIsComplete = false;
            }

            var wasMarkedMissing = _profileStore.IsMarkedMissing(runtimeRoot, profile.Id);
            if (!modelIsComplete)
            {
                _missingProfileIds.Add(profile.Id);
                if (!wasMarkedMissing)
                {
                    try
                    {
                        _profileStore.MarkMissing(runtimeRoot, profile.Id);
                        newlyMissing++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // The in-memory state still prevents use for this session.
                    }
                }
            }
            else if (wasMarkedMissing)
            {
                // Restoring a file must not silently reactivate stale parameters. A scan/add
                // cycle rebuilds the profile from defaults and clears this marker.
                _missingProfileIds.Add(profile.Id);
            }
        }

        if (newlyMissing > 0)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"检测到 {newlyMissing} 个模型文件已删除或分片不完整；列表记录已保留并显示为灰色。",
                $"Detected {newlyMissing} model files that are missing or incomplete. Their list entries were retained and shown in gray.");
        }

        return Task.FromResult(profiles);
    }

    private bool IsProfileUnavailable(ModelProfile profile) =>
        _missingProfileIds.Contains(profile.Id);

    private ModelListItem CreateModelListItem(ModelProfile profile) =>
        ModelListItem.FromProfile(profile, IsProfileUnavailable(profile));

    private static IReadOnlyList<string> GetOwnedModelPaths(string runtimeRoot, ModelProfile profile)
    {
        var root = Path.GetFullPath(runtimeRoot);
        var modelsRoot = Path.GetFullPath(Path.Combine(root, "models"));
        var primary = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
        var modelsPrefix = modelsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!primary.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLanguageManager.Choose("模型路径不在当前 Runtime 的 models 文件夹中。", "The model path is not inside the current Runtime's models folder."));
        }

        var match = Regex.Match(
            Path.GetFileName(primary),
            "^(?<base>.+)-(?<part>[0-9]{5})-of-(?<total>[0-9]{5})\\.gguf$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups["total"].Value, out var total)
            || total <= 1
            || total > 10_000)
        {
            return [primary];
        }

        var directory = Path.GetDirectoryName(primary)!;
        var baseName = match.Groups["base"].Value;
        return Enumerable.Range(1, total)
            .Select(index => Path.Combine(directory, $"{baseName}-{index:00000}-of-{total:00000}.gguf"))
            .ToArray();
    }

    private async void ToggleModelVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        var profile = selected.Profile;
        await using var operation = _diagnostics.Begin("model_visibility", ModelDiagnosticFields(profile));
        if (IsManagedProfileInUse(profile, forceClientRefresh: true))
        {
            StatusText.Text = AppLanguageManager.Choose("当前模型正在运行；停止客户端和相关服务后才能从模式页隐藏。", "The model is running. Stop the client and related services before hiding it from the Mode page.");
            await operation.CompleteAsync("blocked", "model_in_use");
            UpdateManagedModelMutationButtons();
            return;
        }

        var updated = profile with { ShowInModePage = !profile.ShowInModePage };
        try
        {
            await _profileStore.SaveRegistrationAsync(_settings.LlamaRoot, updated, _lifetime.Token);
            if (!updated.ShowInModePage
                && _settings.PendingMode == ProviderMode.Local
                && string.Equals(_settings.PendingModelId, profile.Id, StringComparison.Ordinal))
            {
                await ClearPendingSelectionAsync();
            }

            await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            operation.Set("visible", updated.ShowInModePage);
            await operation.CompleteAsync();
            StatusText.Text = updated.ShowInModePage
                ? AppLanguageManager.Choose($"{updated.DisplayName} 已显示在模式页面。", $"{updated.DisplayName} is now shown on the Mode page.")
                : AppLanguageManager.Choose($"{updated.DisplayName} 已从模式页面隐藏，模型与参数均已保留。", $"{updated.DisplayName} is hidden from the Mode page. Its model and parameters were retained.");
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception);
            StatusText.Text = AppLanguageManager.Choose($"更新模型显示状态失败：{exception.Message}", $"Failed to update model visibility: {exception.Message}");
        }
    }

    private async void DeleteLocalModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        var profile = selected.Profile;
        await using var operation = _diagnostics.Begin("model_list_remove", ModelDiagnosticFields(profile));
        if (Launcher.Orchestration.Models.ModelListRemoval.IsApplied(_settings, profile.Id))
        {
            StatusText.Text = AppLanguageManager.Choose(
                "请先实际切换到在线模式或另一个本地模型，再移除此模型；仅修改预选不算切换。",
                "Switch to online mode or another local model before removing this model. Changing the preselection alone is not a switch.");
            await operation.CompleteAsync("blocked", "model_is_applied");
            UpdateManagedModelMutationButtons();
            return;
        }
        if (IsManagedProfileInUse(profile, forceClientRefresh: true))
        {
            await operation.CompleteAsync("blocked", "model_in_use");
            StatusText.Text = AppLanguageManager.Choose("模型仍在运行或可能被 llama 服务占用；请先关闭客户端并退出相关服务。", "The model is still running or may be in use by a llama service. Close the client and stop related services first.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                $"从列表中移除“{profile.DisplayName}”？\n模型文件：{Path.GetFileName(profile.ModelRelativePath)}\n路径：{Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath))}\n\n将移除模型登记、列表记录、该模型的启动预选及启动器生成的 BAT。\n模型文件、参数 JSON、模板、外挂文件及能力检测记录全部保留，之后可重新扫描添加。",
                $"Remove \"{profile.DisplayName}\" from the list?\nModel file: {Path.GetFileName(profile.ModelRelativePath)}\nPath: {Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath))}\n\nThis removes its registration, list entry, launch preselection, and launcher-generated BAT.\nModel files, parameter JSON files, templates, companion files, and capability records are retained. You can scan and add it again later."),
            AppLanguageManager.Choose("从列表中移除", "Remove from List"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await operation.StageAsync("remove_registration_and_owned_files");
            await Launcher.Orchestration.Models.ModelListRemoval.RemoveAsync(profile, _settings.LlamaRoot,
                _paths.RouterPresetFile, () => _settings, MutateSettingsAsync, _lifetime.Token);
            await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            await operation.CompleteAsync();
            StatusText.Text = AppLanguageManager.Choose(
                $"已从列表中移除 {profile.DisplayName}；模型文件、参数配置、模板和能力检测记录均已保留。",
                $"Removed {profile.DisplayName} from the list. Model files, parameter configurations, templates, and capability records were retained.");
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception);
            StatusText.Text = AppLanguageManager.Choose($"从列表中移除失败：{exception.Message}", $"Failed to remove the model from the list: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void TryDeleteActiveRouterPreset()
    {
        try
        {
            if (File.Exists(_paths.RouterPresetFile))
            {
                File.Delete(_paths.RouterPresetFile);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"模型记录已清理，但活动 Router 配置删除失败：{exception.Message}",
                $"The model record was cleared, but the active Router configuration could not be deleted: {exception.Message}");
        }
    }

    private sealed record ModeCardItem(
        ModelProfile? Profile,
        string Title,
        string Summary,
        bool IsOpenAi,
        bool IsPending,
        double Opacity,
        bool CanEdit) : System.ComponentModel.INotifyPropertyChanged
    {
        private PreferredConfigurationState? _configuration;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string StartupConfigurationPrefix => AppLanguageManager.Choose("启动配置：", "Startup: ");
        public string StartupConfigurationName => _configuration?.Name ?? AppLanguageManager.Choose("无", "None");
        public string StartupConfigurationText => StartupConfigurationPrefix + StartupConfigurationName;
        public string? StartupConfigurationToolTip => _configuration?.Error is { } error
            ? _configuration.Path + Environment.NewLine + error : _configuration?.Path;
        public bool SamePresentation(ModeCardItem other) => Equals(Profile, other.Profile) && Title == other.Title
            && Summary == other.Summary && IsOpenAi == other.IsOpenAi && IsPending == other.IsPending
            && Opacity == other.Opacity && CanEdit == other.CanEdit;
        public void RefreshConfiguration(string? root) => UpdateConfiguration(Profile is not null && root is not null
            ? ModelConfigurationSelection.Inspect(Profile, root) : null);
        public void UpdateConfiguration(PreferredConfigurationState? state)
        {
            if (_configuration == state) return;
            _configuration = state;
            PropertyChanged?.Invoke(this, new(nameof(StartupConfigurationName)));
            PropertyChanged?.Invoke(this, new(nameof(StartupConfigurationText)));
            PropertyChanged?.Invoke(this, new(nameof(StartupConfigurationToolTip)));
        }

        public string CardTitle => IsOpenAi
            ? "OpenAI"
            : $"Local LLM 【{FormatModelType(Profile?.ModelType ?? ModelType.Unknown)}】";

        public string ModelName => IsOpenAi ? string.Empty : Title;

        public string GpuLayersText => Profile is null
            ? "—"
            : Profile.GpuLayers.ToLowerInvariant() switch
            {
                "auto" => AppLanguageManager.Choose("自动", "Auto"),
                "all" => AppLanguageManager.Choose("全部", "All"),
                _ => Profile.GpuLayers,
            };

        public string ContextSizeText => FormatTokenCount(Profile?.ContextSize);

        public string FlashAttentionText => Profile?.FlashAttention.ToLowerInvariant() switch
        {
            "on" or "true" or "1" => AppLanguageManager.Choose("开启", "On"),
            "off" or "false" or "0" => AppLanguageManager.Choose("关闭", "Off"),
            "auto" => AppLanguageManager.Choose("自动", "Auto"),
            null or "" => "—",
            _ => Profile!.FlashAttention.ToUpperInvariant(),
        };

        public string SafetyReserveText => FormatTokenCount(Profile?.CompactionSafetyReserve);

        public string CacheTypeKText => FormatCacheType(Profile?.CacheTypeK);

        public string CacheTypeVText => FormatCacheType(Profile?.CacheTypeV);

        public string LeftLabel1 => Profile?.ModelType == ModelType.Unknown
            ? AppLanguageManager.Choose("模型类型", "Model Type")
            : AppLanguageManager.Choose("卸载层数", "GPU Layers");
        public string LeftValue1 => Profile?.ModelType == ModelType.Unknown
            ? AppLanguageManager.Choose("未指定", "Unspecified")
            : GpuLayersText;
        public string LeftLabel2 => AppLanguageManager.Choose("上下文", "Context");
        public string LeftValue2 => ContextSizeText;
        public string LeftLabel3 => Profile?.ModelType == ModelType.MoE
            ? AppLanguageManager.Choose("自动适配", "Memory Fit")
            : AppLanguageManager.Choose("安全余量", "Reserve");
        public string LeftValue3 => Profile?.ModelType == ModelType.MoE ? ReadArgument("fit") : SafetyReserveText;
        public string RightLabel1 => Profile?.ModelType switch
        {
            ModelType.MoE => AppLanguageManager.Choose("专家位置", "Experts"),
            ModelType.Unknown => AppLanguageManager.Choose("参数设置", "Parameters"),
            _ => "FA",
        };
        public string RightValue1 => Profile?.ModelType switch
        {
            ModelType.MoE => FormatMoePlacement(),
            ModelType.Unknown => AppLanguageManager.Choose("请先指定", "Specify first"),
            _ => FlashAttentionText,
        };
        public string RightLabel2 => Profile?.ModelType == ModelType.MoE
            ? AppLanguageManager.Choose("加载模式", "Load Mode")
            : AppLanguageManager.Choose("K 缓存", "K Cache");
        public string RightValue2 => Profile?.ModelType == ModelType.MoE ? ReadArgument("load-mode") : CacheTypeKText;
        public string RightLabel3 => Profile?.ModelType == ModelType.MoE
            ? AppLanguageManager.Choose("KV 缓存", "KV Cache")
            : AppLanguageManager.Choose("V 缓存", "V Cache");
        public string RightValue3 => Profile?.ModelType == ModelType.MoE
            ? $"{CacheTypeKText}/{CacheTypeVText}"
            : CacheTypeVText;

        public Visibility LocalDetailsVisibility => IsOpenAi ? Visibility.Collapsed : Visibility.Visible;

        public Visibility OpenAiDetailsVisibility => IsOpenAi ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SettingsVisibility => IsOpenAi ? Visibility.Collapsed : Visibility.Visible;

        private static string FormatTokenCount(int? value)
        {
            if (value is null or <= 0)
            {
                return "—";
            }

            return value.Value % 1024 == 0
                ? $"{value.Value / 1024}K"
                : value.Value.ToString("N0");
        }

        private static string FormatCacheType(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "—" : value.ToUpperInvariant();

        private string ReadArgument(string key)
        {
            if (Profile?.ExtraArguments is null || !Profile.ExtraArguments.TryGetValue(key, out var value))
            {
                return AppLanguageManager.Choose("自动", "Auto");
            }

            return string.IsNullOrWhiteSpace(value) ? AppLanguageManager.Choose("开启", "On") : value switch
            {
                "on" => AppLanguageManager.Choose("开启", "On"),
                "off" => AppLanguageManager.Choose("关闭", "Off"),
                _ => value,
            };
        }

        private string FormatMoePlacement()
        {
            if (Profile?.MoeExpertPlacement == MoeExpertPlacement.Gpu)
            {
                return AppLanguageManager.Choose("GPU优先", "GPU preferred");
            }

            if (Profile?.MoeExpertPlacement == MoeExpertPlacement.CpuAll)
            {
                return AppLanguageManager.Choose("CPU全部", "All CPU");
            }

            if (Profile?.MoeExpertPlacement == MoeExpertPlacement.CpuFirstLayers)
            {
                return AppLanguageManager.Choose($"CPU {Profile.CpuMoeLayers}层", $"CPU {Profile.CpuMoeLayers} layers");
            }

            if (Profile?.ExtraArguments.ContainsKey("cpu-moe") == true)
            {
                return AppLanguageManager.Choose("CPU 全部", "All CPU");
            }

            return Profile?.ExtraArguments.TryGetValue("n-cpu-moe", out var layers) == true
                ? AppLanguageManager.Choose($"CPU {layers}层", $"CPU {layers} layers")
                : AppLanguageManager.Choose("自动", "Auto");
        }
    }
}
