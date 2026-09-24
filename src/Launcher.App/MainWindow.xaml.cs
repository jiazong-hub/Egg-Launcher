using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Discovery;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Agent;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.Security;
using Launcher.Core.State;
using Launcher.Core.Startup;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Orchestration.Models;
using Launcher.Runtime.Detection;
using Launcher.Runtime.Fit;
using Launcher.Runtime.Monitoring;
using Launcher.Runtime.Processes;
using Launcher.Runtime.Router;
using Launcher.Scripts.Batch;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;
using Microsoft.Win32;

namespace Launcher.App;

public partial class MainWindow : Window
{
    private static readonly Uri LlamaCppDownloadsUri =
        new("https://github.com/ggml-org/llama.cpp/releases", UriKind.Absolute);

    private readonly LauncherDataPaths _paths = LauncherDataPaths.ForCurrentUser();
    private readonly ChatGptIntegrationInspector _inspector = new();
    private readonly ChatGptClientDetector _clientDetector = new();
    private readonly LlamaRuntimeProbe _runtimeProbe = new(new ProcessRunner());
    private readonly LlamaFitParamsRunner _fitParamsRunner = new(new ProcessRunner());
    private readonly WindowsHardwareInventoryReader _hardwareInventoryReader = new(new ProcessRunner());
    private readonly WindowsPerformanceSampler _performanceSampler = new();
    private readonly GgufModelScanner _modelScanner = new();
    private readonly JsonModelProfileStore _profileStore = new();
    private readonly ModelArtifactTransaction _modelArtifactTransaction = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly JsonSettingsStore _settingsStore;
    private readonly SemaphoreSlim _settingsMutationGate = new(1, 1);
    private readonly object _settingsMutationStateGate = new();
    private readonly DependencyPropertyDescriptor _statusTextDescriptor;
    private readonly DispatcherTimer _statusNotificationTimer;
    private readonly DispatcherTimer _statusRefreshTimer;
    private readonly DispatcherTimer _telemetryRefreshTimer;
    private readonly DispatcherTimer _backgroundLifecycleTimer;
    private readonly AgentStartupRegistration _agentStartupRegistration =
        new(new WindowsUserRunEntryStore());
    private LauncherSettings _settings = new();
    private IReadOnlyList<ModelProfile> _profiles = Array.Empty<ModelProfile>();
    private readonly HashSet<string> _missingProfileIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _profilesRecreatedFromMissingConfiguration = new(StringComparer.Ordinal);
    private IReadOnlyList<GgufModelCandidate> _discoveredModels = Array.Empty<GgufModelCandidate>();
    private IReadOnlyList<CacheModelListItem> _cachedModels = Array.Empty<CacheModelListItem>();
    private HardwareInventory? _hardwareInventory;
    private string _runtimeDeviceOutput = string.Empty;
    private string _codexHome = string.Empty;
    private string? _agentExecutablePath;
    private bool _runtimeIsValid;
    private bool _busy;
    private bool _configExists;
    private bool _historyStateExists;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _monitorRefreshInProgress;
    private bool _telemetryRefreshInProgress;
    private bool _backgroundLifecycleCheckInProgress;
    private bool _launchInProgress;
    private bool _themeChangeInProgress;
    private bool _languageChangeInProgress;
    private bool _themeUiReady;
    private int _settingsMutationsInProgress;
    private bool _settingsCloseWaitInProgress;
    private TaskCompletionSource _settingsMutationsIdle = CreateCompletedSignal();
    private HttpClient? _telemetryHttpClient;
    private LlamaTelemetryClient? _telemetryClient;
    private TrustedRouterEndpoint? _telemetryEndpoint;
    private string? _telemetryModelAlias;
    private static readonly TimeSpan ForegroundClientStatusInterval = TimeSpan.FromSeconds(3);
    private bool _hasClientRunningSnapshot;
    private bool _clientRunningSnapshot;
    private DateTimeOffset _clientRunningSnapshotAtUtc = DateTimeOffset.MinValue;

    public MainWindow()
    {
        _settingsStore = new JsonSettingsStore(_paths.SettingsFile);
        InitializeComponent();
        _statusNotificationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _statusNotificationTimer.Tick += StatusNotificationTimer_Tick;
        _statusTextDescriptor = DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty,
            typeof(TextBlock));
        _statusTextDescriptor.AddValueChanged(StatusText, StatusText_TextChanged);
        StatusNotificationBar.MouseEnter += StatusNotificationBar_MouseEnter;
        StatusNotificationBar.MouseLeave += StatusNotificationBar_MouseLeave;
        _statusRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _statusRefreshTimer.Tick += StatusRefreshTimer_Tick;
        _telemetryRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _telemetryRefreshTimer.Tick += TelemetryRefreshTimer_Tick;
        _backgroundLifecycleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _backgroundLifecycleTimer.Tick += BackgroundLifecycleTimer_Tick;
        var displayVersion = GetDisplayVersion();
        InitializeEggUi(displayVersion);
        if (!_startHidden)
        {
            UiMotion.AttachWindowEntrance(this);
        }
        AboutVersionText.Text = displayVersion;
        RestartStatusNotificationTimer();
    }

    private static string GetDisplayVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null
            ? AppLanguageManager.Choose("未知", "Unknown")
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
        return signal;
    }

    private async Task<LauncherSettings> MutateSettingsAsync(
        Func<LauncherSettings, LauncherSettings> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        BeginSettingsMutation();
        try
        {
            await _settingsMutationGate.WaitAsync(cancellationToken);
            try
            {
                var updated = mutation(_settings);
                if (updated == _settings)
                {
                    return _settings;
                }

                await _settingsStore.SaveAsync(updated, cancellationToken);
                _settings = updated;
                return updated;
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }
        finally
        {
            EndSettingsMutation();
        }
    }

    private bool GetClientRunningSnapshot(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force
            && _hasClientRunningSnapshot
            && (IsModelManagementVisible
                || now - _clientRunningSnapshotAtUtc < ForegroundClientStatusInterval))
        {
            return _clientRunningSnapshot;
        }

        _clientRunningSnapshot = _clientDetector.IsRunning();
        _clientRunningSnapshotAtUtc = now;
        _hasClientRunningSnapshot = true;
        return _clientRunningSnapshot;
    }

    private bool IsModelManagementVisible =>
        ReferenceEquals(LauncherTabs.SelectedItem, RuntimeMonitorTab)
        && ModelManagementPanel.Visibility == Visibility.Visible;

    private void InvalidateClientRunningSnapshot()
    {
        _hasClientRunningSnapshot = false;
        _clientRunningSnapshotAtUtc = DateTimeOffset.MinValue;
    }

    private void BeginSettingsMutation()
    {
        lock (_settingsMutationStateGate)
        {
            if (_settingsMutationsInProgress++ == 0)
            {
                _settingsMutationsIdle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndSettingsMutation()
    {
        TaskCompletionSource? completed = null;
        lock (_settingsMutationStateGate)
        {
            _settingsMutationsInProgress--;
            if (_settingsMutationsInProgress == 0)
            {
                completed = _settingsMutationsIdle;
            }
        }

        completed?.TrySetResult();
    }

    private bool HasPendingSettingsMutations
    {
        get
        {
            lock (_settingsMutationStateGate)
            {
                return _settingsMutationsInProgress > 0;
            }
        }
    }

    private Task WaitForSettingsMutationsAsync()
    {
        lock (_settingsMutationStateGate)
        {
            return _settingsMutationsIdle.Task;
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var configTransactionService = new ChatGptConfigTransactionService(_clientDetector);
            var recovery = await new ModeRecoveryCoordinator(
                _settingsStore,
                configTransactionService,
                _paths).RecoverAsync(_lifetime.Token);
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            AppThemeManager.Apply(_settings.Theme);
            AppLanguageManager.Apply(_settings.Language);
            UpdateThemeToggleUi(_settings.Theme);
            UpdateLanguageToggleUi(_settings.Language);
            await InitializePendingSelectionAsync(_lifetime.Token);
            RefreshAgentStartupRegistration();
            await RefreshEnvironmentAsync(_lifetime.Token);
            _statusRefreshTimer.Start();
            _telemetryRefreshTimer.Start();

            if (!_startHidden)
            {
                await OfferLegacyBackupMigrationAsync(configTransactionService, _lifetime.Token);
            }

            if (recovery.Changed)
            {
                StatusText.Text = AppLanguageManager.Choose("检测到上次未完成的模式切换，配置与 Egg Launcher 状态已安全收敛。", "An incomplete previous mode switch was detected. Configuration and Egg Launcher state were safely reconciled.");
            }

            if (!string.IsNullOrWhiteSpace(_settings.LlamaRoot))
            {
                await ConfigureRuntimeAsync(
                    _settings.LlamaRoot,
                    saveSelection: false,
                    offerToCreateFolders: false,
                    _lifetime.Token);
                if (IsAgentRunning()
                    && (!GetClientRunningSnapshot(force: true) || _settings.SelectedMode == ProviderMode.OpenAI))
                {
                    StatusText.Text = AppLanguageManager.Choose("正在停止不再对应活动 Local 客户端的后台服务…", "Stopping background services that no longer correspond to an active Local client…");
                    if (!await StopAgentForExitAsync())
                    {
                        StatusText.Text = AppLanguageManager.Choose("检测到残留的 Local 后台服务，但未能安全停止；请查看诊断日志。", "A residual Local background service was detected but could not be stopped safely. See Diagnostic Logs.");
                    }
                }
            }
            else
            {
                RenderRuntimeUnavailable(AppLanguageManager.Text("RuntimeNotConfigured"));
            }

            _themeUiReady = true;
            ThemeToggleButton.IsEnabled = true;
            LanguageToggleButton.IsEnabled = true;
            RefreshEggUiState();
            if (_startHidden)
            {
                await HideToTrayAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"读取环境失败：{exception.Message}", $"Failed to read the environment: {exception.Message}");
        }
    }

    private async Task OfferLegacyBackupMigrationAsync(
        ChatGptConfigTransactionService configTransactionService,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.BackupsDirectory))
        {
            return;
        }

        var legacyCount = Directory.EnumerateFiles(
                _paths.BackupsDirectory,
                "config.toml.*.bak",
                SearchOption.TopDirectoryOnly)
            .Count();
        if (legacyCount == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                $"检测到 {legacyCount} 份旧版本留下的明文 config.toml 备份。推荐选择“是”，现在使用当前 Windows 用户的 DPAPI 加密迁移。\n\n"
                + "该操作只保护 Egg Launcher 备份，不修改当前 ChatGPT 配置或 auth.json。"
                + "每份密文回读校验成功并更新恢复记录后，才会删除对应明文。",
                $"Found {legacyCount} plaintext config.toml backups left by an older version. Select Yes to migrate them now using DPAPI for the current Windows user.\n\n"
                + "This protects only Egg Launcher backups and does not change the active ChatGPT configuration or auth.json. "
                + "Each plaintext file is deleted only after its encrypted copy is verified and the recovery record is updated."),
            AppLanguageManager.Choose("保护旧版配置备份", "Protect Legacy Configuration Backups"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = AppLanguageManager.Choose(
                "旧版明文配置备份尚未迁移；可在下次启动 Egg Launcher 时处理。",
                "Legacy plaintext configuration backups were not migrated. You can handle them the next time Egg Launcher starts.");
            return;
        }

        var result = await configTransactionService.MigrateLegacyBackupsAsync(_paths, cancellationToken);
        StatusText.Text = result.RemainingPlaintextCount == 0
            ? AppLanguageManager.Choose(
                $"已安全迁移 {result.MigratedCount} 份旧版配置备份。",
                $"Safely migrated {result.MigratedCount} legacy configuration backups.")
            : AppLanguageManager.Choose(
                $"已迁移 {result.MigratedCount} 份，仍有 {result.RemainingPlaintextCount} 份明文备份需要处理。",
                $"Migrated {result.MigratedCount} backups; {result.RemainingPlaintextCount} plaintext backups still need attention.");
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowClosed = true;
        UiMotion.Reset(this);
        _statusNotificationTimer.Stop();
        _statusNotificationTimer.Tick -= StatusNotificationTimer_Tick;
        _statusTextDescriptor.RemoveValueChanged(StatusText, StatusText_TextChanged);
        StatusNotificationBar.MouseEnter -= StatusNotificationBar_MouseEnter;
        StatusNotificationBar.MouseLeave -= StatusNotificationBar_MouseLeave;
        _statusRefreshTimer.Stop();
        _statusRefreshTimer.Tick -= StatusRefreshTimer_Tick;
        _telemetryRefreshTimer.Stop();
        _telemetryRefreshTimer.Tick -= TelemetryRefreshTimer_Tick;
        _backgroundLifecycleTimer.Stop();
        _backgroundLifecycleTimer.Tick -= BackgroundLifecycleTimer_Tick;
        _lifetime.Cancel();
        _telemetryHttpClient?.Dispose();
        _performanceSampler.Dispose();
        _settingsStore.Dispose();
        _settingsMutationGate.Dispose();
        DisposeTrayIcon();
        _lifetime.Dispose();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_trayExitRequested && !_allowClose)
        {
            e.Cancel = true;
            await HideToTrayAsync();
            return;
        }

        if (_allowClose)
        {
            return;
        }

        if (HasPendingSettingsMutations)
        {
            e.Cancel = true;
            if (_settingsCloseWaitInProgress)
            {
                return;
            }

            _settingsCloseWaitInProgress = true;
            IsEnabled = false;
            try
            {
                StatusText.Text = AppLanguageManager.Choose(
                    "正在完成设置保存，随后退出…",
                    "Finishing the settings save before exiting…");
                await WaitForSettingsMutationsAsync();
                Close();
            }
            finally
            {
                if (!_allowClose && IsLoaded)
                {
                    IsEnabled = true;
                    _settingsCloseWaitInProgress = false;
                }
            }

            return;
        }

        if (_closeInProgress)
        {
            e.Cancel = true;
            return;
        }

        if (_busy)
        {
            e.Cancel = true;
            _trayExitRequested = false;
            ResumeVisibleWindowAfterCanceledExit();
            MessageBox.Show(
                this,
                AppLanguageManager.Choose("启动器正在保存或切换配置。请等待当前操作完成后再关闭，以免中断事务。", "The launcher is saving or switching configuration. Wait for the current operation before closing to avoid interrupting the transaction."),
                AppLanguageManager.Choose("暂时无法关闭", "Unable to Close Yet"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (HasActiveOrQueuedDownloads && !_downloadExitConfirmed)
        {
            e.Cancel = true;
            var answer = MessageBox.Show(
                this,
                AppLanguageManager.Choose("当前仍有模型下载任务。真正退出 Egg Launcher 将取消当前下载并清空等待队列，是否继续？", "Model download tasks are still active. Exiting Egg Launcher will cancel the current download and clear the waiting queue. Continue?"),
                AppLanguageManager.Choose("退出并取消下载", "Exit and Cancel Downloads"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                _trayExitRequested = false;
                ResumeVisibleWindowAfterCanceledExit();
                return;
            }

            _downloadExitConfirmed = true;
            IsEnabled = false;
            await CancelDownloadsForExitAsync();
            IsEnabled = true;
            Close();
            return;
        }

        var agentRunning = IsAgentRunning();
        var runtimeLlamaRunning = IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot);
        if (_settings.SelectedMode == ProviderMode.Local && (agentRunning || runtimeLlamaRunning))
        {
            e.Cancel = true;
            var serviceDescription = agentRunning
                ? AppLanguageManager.Choose("由 Egg Launcher 启动的后台 Agent 与 llama 服务会被停止。", "Background Agent and llama services started by Egg Launcher will be stopped.")
                : AppLanguageManager.Choose("检测到 llama 服务正在运行，但无法确认它由 Egg Launcher 启动；退出时不会强制结束未知进程。", "A llama service is running, but Egg Launcher cannot confirm it started the service. Unknown processes will not be forced to stop on exit.");
            var answer = MessageBox.Show(
                this,
                AppLanguageManager.Choose($"当前 Local 服务仍在运行。{serviceDescription}\n\n是否继续退出？", $"Local services are still running. {serviceDescription}\n\nContinue exiting?"),
                AppLanguageManager.Choose("退出 Egg Launcher", "Exit Egg Launcher"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                _trayExitRequested = false;
                ResumeVisibleWindowAfterCanceledExit();
                return;
            }
        }

        if (!agentRunning)
        {
            _allowClose = true;
            e.Cancel = false;
            return;
        }

        e.Cancel = true;

        _closeInProgress = true;
        IsEnabled = false;
        StatusText.Text = _settings.SelectedMode == ProviderMode.Local
            ? AppLanguageManager.Choose("正在正常停止 Local 后台服务…", "Stopping Local background services normally…")
            : AppLanguageManager.Choose("正在结束当前无需驻留的后台 Agent…", "Stopping the background Agent that no longer needs to remain active…");
        try
        {
            if (!await StopAgentForExitAsync())
            {
                StatusText.Text = AppLanguageManager.Choose("后台 Agent 未退出；程序目录仍可能被占用。", "The background Agent did not exit; the program directory may still be in use.");
                return;
            }

            _allowClose = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"结束后台 Agent 失败：{exception.Message}", $"Failed to stop the background Agent: {exception.Message}");
            MessageBox.Show(
                this,
                StatusText.Text,
                AppLanguageManager.Choose("无法完整退出", "Unable to Exit Completely"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!_allowClose)
            {
                IsEnabled = true;
                _closeInProgress = false;
                _trayExitRequested = false;
                ResumeVisibleWindowAfterCanceledExit();
            }
        }
    }

    private async Task<bool> StopAgentForExitAsync()
    {
        var processIds = GetAgentProcessIds();
        if (processIds.Count == 0)
        {
            return true;
        }

        var controlResult = await AgentControlPipe.RequestShutdownAsync(
            TimeSpan.FromSeconds(3),
            _lifetime.Token);
        if (controlResult.Succeeded
            && await WaitForAgentExitAsync(processIds, TimeSpan.FromSeconds(8), _lifetime.Token))
        {
            return true;
        }

        var diagnostic = string.IsNullOrWhiteSpace(controlResult.Diagnostic)
            ? AppLanguageManager.Choose("Agent 在等待期内没有退出。", "The Agent did not exit within the wait period.")
            : controlResult.Diagnostic;
        var verifiedAgent = await TryGetVerifiedAgentIdentityAsync(processIds, _lifetime.Token);
        if (verifiedAgent is null)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"后台 Agent 未能正常退出：{diagnostic}\n\n"
                    + "启动器无法用运行状态文件确认该进程属于当前程序包，因此不会强制结束同名进程。"
                    + "请在任务管理器中核对路径后手动结束，或重新启动 Windows。",
                    $"The background Agent did not exit normally: {diagnostic}\n\n"
                    + "The launcher cannot use the runtime state file to verify that this process belongs to the current package, so it will not force-stop a process based only on its name. "
                    + "Verify its path in Task Manager and stop it manually, or restart Windows."),
                AppLanguageManager.Choose("无法确认后台进程归属", "Unable to Verify Background Process"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var answer = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                $"后台 Agent 未能正常退出：{diagnostic}\n"
                + $"已确认 PID {verifiedAgent.ProcessId} 的路径、启动时间和状态协议均属于当前程序包。\n\n"
                + "是否强制结束该 Agent？如果当前处于 Local 模式，回环代理和 llama.cpp Router 也会停止，但模式配置不会切换。",
                $"The background Agent did not exit normally: {diagnostic}\n"
                + $"PID {verifiedAgent.ProcessId} has been verified as belonging to the current package by path, start time, and state protocol.\n\n"
                + "Force-stop this Agent? In Local mode, the loopback proxy and llama.cpp Router will also stop, but the selected mode will not change."),
            AppLanguageManager.Choose("结束当前后台 Agent", "Stop Current Background Agent"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(verifiedAgent.ProcessId);
            if (!IsExpectedProcess(process, verifiedAgent.ExecutablePath, verifiedAgent.StartedAtUtc))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"无法结束 Launcher.Agent PID {verifiedAgent.ProcessId}：{exception.Message}",
                    $"Unable to stop Launcher.Agent PID {verifiedAgent.ProcessId}: {exception.Message}"),
                AppLanguageManager.Choose("后台 Agent 仍在运行", "Background Agent Is Still Running"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        return await WaitForAgentExitAsync(
            new HashSet<int> { verifiedAgent.ProcessId },
            TimeSpan.FromSeconds(5),
            _lifetime.Token);
    }

    private static async Task<bool> WaitForAgentExitAsync(
        IReadOnlySet<int> processIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processIds.All(HasProcessExited))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return processIds.All(HasProcessExited);
    }

    private static bool HasProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private async Task RefreshEnvironmentAsync(CancellationToken cancellationToken)
    {
        var integration = await _inspector.InspectAsync(cancellationToken);
        _codexHome = integration.CodexHomePath;
        _configExists = integration.ConfigExists;
        _historyStateExists = integration.HistoryStateExists;
        RenderEnvironmentStatus();
    }

    private async void StatusRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _statusRefreshTimer.Stop();
            _telemetryRefreshTimer.Stop();
            return;
        }

        RenderEnvironmentStatus();
        RefreshEggUiState(refreshModeCards: false);
        await RefreshMonitoringAsync(forceHardwareRefresh: false);
    }

    private async void TelemetryRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _telemetryRefreshTimer.Stop();
            return;
        }

        await RefreshTelemetryAsync();
    }

    private async void BackgroundLifecycleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_downloadsSuspendedForLocal)
        {
            _backgroundLifecycleTimer.Stop();
            return;
        }

        if (_backgroundLifecycleCheckInProgress || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _backgroundLifecycleCheckInProgress = true;
        try
        {
            await TryResumeSuspendedDownloadsAsync();
        }
        finally
        {
            _backgroundLifecycleCheckInProgress = false;
        }
    }

    private void RenderEnvironmentStatus()
    {
        var clientRunning = GetClientRunningSnapshot();
        var activeProfile = _settings.SelectedMode == ProviderMode.Local
            ? _profiles.FirstOrDefault(profile => string.Equals(profile.Id, _settings.SelectedModelId, StringComparison.Ordinal))
            : null;
        CurrentModeText.Text = GetActualModeLabel(clientRunning);
        ChatGptStatusText.Text = AppLanguageManager.Text(clientRunning ? "Running" : "Closed");
        ConfigurationStatusText.Text = AppLanguageManager.Text(_configExists ? "Normal" : "NotFound");
        RunningModelText.Text = clientRunning && activeProfile is not null
            ? activeProfile.DisplayName
            : "–";
    }

    private async void SelectRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (IsLocalRuntimeSessionActive(forceClientRefresh: true))
        {
            StatusText.Text = AppLanguageManager.Choose("Local 服务正在使用当前 Runtime；请先关闭客户端并等待服务停止后再更换文件夹。", "Local services are using the current Runtime. Close the client and wait for services to stop before changing the folder.");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = AppLanguageManager.Choose("选择 llama.cpp Runtime 文件夹", "Select llama.cpp Runtime Folder"),
            Multiselect = false,
            InitialDirectory = Directory.Exists(_settings.LlamaRoot) ? _settings.LlamaRoot : null,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await ConfigureRuntimeAsync(
                dialog.FolderName,
                saveSelection: true,
                offerToCreateFolders: true,
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"Runtime 配置失败：{exception.Message}", $"Runtime configuration failed: {exception.Message}");
        }
    }

    private async void StartAgentAtLoginCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_appExecutablePath))
        {
            RefreshAgentStartupRegistration();
            return;
        }

        var before = _agentStartupRegistration.Inspect(_appExecutablePath, "--startup");
        var enabled = StartAgentAtLoginCheckBox.IsChecked == true;
        try
        {
            SetBusy(true);
            _agentStartupRegistration.SetEnabled(enabled, _appExecutablePath, "--startup");
            await MutateSettingsAsync(
                settings => settings with { StartAgentAtLogin = enabled },
                _lifetime.Token);
            StatusText.Text = enabled
                ? AppLanguageManager.Choose("已启用开机静默启动；Egg Launcher 将最小化到系统托盘。", "Silent startup enabled. Egg Launcher will start minimized to the system tray.")
                : AppLanguageManager.Choose("已关闭开机启动；不会影响当前模式与模型配置。", "Startup disabled. Current mode and model configuration are unaffected.");
        }
        catch (Exception exception)
        {
            try
            {
                _agentStartupRegistration.Restore(before.RegisteredCommand);
            }
            catch (Exception rollbackException)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    StatusText.Text =
                        AppLanguageManager.Choose(
                            $"Egg Launcher 开机启动项更新失败，且回滚也失败：{exception.Message}；{rollbackException.Message}",
                            $"Failed to update Egg Launcher startup, and rollback also failed: {exception.Message}; {rollbackException.Message}");
                }

                return;
            }

            if (!_lifetime.IsCancellationRequested)
            {
                StatusText.Text = AppLanguageManager.Choose($"Egg Launcher 开机启动项更新失败，已回滚：{exception.Message}", $"Failed to update Egg Launcher startup; the change was rolled back: {exception.Message}");
            }
        }
        finally
        {
            SetBusy(false);
            RefreshAgentStartupRegistration();
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.LogsDirectory,
                UseShellExecute = true,
            });
            StatusText.Text = AppLanguageManager.Choose("已打开诊断日志文件夹；proxy.jsonl 不记录提示词正文或认证头。llama 原生 stdout/stderr 内容由 Runtime 决定。", "Diagnostic Logs folder opened. proxy.jsonl does not record prompt text or authentication headers. Native llama stdout/stderr content depends on the Runtime.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"无法打开诊断日志文件夹：{exception.Message}", $"Unable to open the Diagnostic Logs folder: {exception.Message}");
        }
    }

    private void OpenLlamaDownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LlamaCppDownloadsUri.AbsoluteUri,
                UseShellExecute = true,
            });
            StatusText.Text = AppLanguageManager.Choose("已在默认浏览器中打开 llama.cpp 官方下载页面。", "Opened the official llama.cpp download page in the default browser.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"无法打开 llama.cpp 官方下载页面：{exception.Message}", $"Unable to open the official llama.cpp download page: {exception.Message}");
        }
    }

    private async Task ConfigureRuntimeAsync(
        string runtimeRoot,
        bool saveSelection,
        bool offerToCreateFolders,
        CancellationToken cancellationToken)
    {
        SetBusy(true);
        try
        {
            var root = Path.GetFullPath(runtimeRoot);
            if (saveSelection)
            {
                ModeSwitchGuard.EnsureCanChangeRuntime(
                    _settings.SelectedMode,
                    _settings.LlamaRoot,
                    root);
            }
            RuntimePathText.Text = root;
            RuntimeStatusText.Text = AppLanguageManager.Choose("正在验证 llama-server 版本与能力…", "Validating the llama-server version and capabilities…");
            DeviceText.Text = string.Empty;
            StatusText.Text = AppLanguageManager.Choose("正在验证 Runtime；不会加载任何 GGUF 模型。", "Validating the Runtime. No GGUF model will be loaded.");

            var probe = await _runtimeProbe.ProbeAsync(root, cancellationToken);
            if (!probe.IsValid)
            {
                var diagnostic = probe.Diagnostics.Count == 0
                    ? AppLanguageManager.Choose("llama-server 验证失败。", "llama-server validation failed.")
                    : string.Join(" ", probe.Diagnostics);
                RenderRuntimeUnavailable(diagnostic);
                return;
            }

            if (!probe.Capabilities.SupportsRouter)
            {
                RenderRuntimeUnavailable(AppLanguageManager.Choose(
                    "当前 llama.cpp Runtime 缺少完整 Router 参数，本版本不会在 Egg Launcher 内模拟该能力。请升级或选择兼容的 llama.cpp Runtime。",
                    "The current llama.cpp Runtime lacks complete Router parameters. This release does not emulate that capability. Upgrade or select a compatible llama.cpp Runtime."));
                return;
            }

            if (!probe.Capabilities.SupportsIdleSleep)
            {
                RenderRuntimeUnavailable(AppLanguageManager.Choose(
                    "当前 llama.cpp Runtime 缺少 --sleep-idle-seconds，无法由 llama.cpp 原生管理空闲显存释放。请升级或选择兼容的 llama.cpp Runtime。",
                    "The current llama.cpp Runtime lacks --sleep-idle-seconds and cannot natively release idle VRAM. Upgrade or select a compatible llama.cpp Runtime."));
                return;
            }

            if (!probe.Capabilities.SupportsChatTemplateFile)
            {
                RenderRuntimeUnavailable(AppLanguageManager.Choose(
                    "当前 llama.cpp Runtime 缺少 --chat-template-file，无法由 llama.cpp 加载模型专用 Codex 兼容模板。请升级或选择兼容的 llama.cpp Runtime。",
                    "The current llama.cpp Runtime lacks --chat-template-file and cannot load model-specific Codex-compatible templates. Upgrade or select a compatible llama.cpp Runtime."));
                return;
            }

            if (offerToCreateFolders)
            {
                EnsureRuntimeFolders(root);
            }

            await MutateSettingsAsync(
                settings => RuntimeSelectionPolicy.ApplyProbeResult(
                    settings,
                    root,
                    saveSelection,
                    probe.Capabilities.SupportsMetrics),
                cancellationToken);

            _runtimeIsValid = true;
            _runtimeDeviceOutput = probe.DeviceOutput;
            RuntimeStatusText.Text = AppLanguageManager.IsEnglish
                ? $"Validated · {probe.VersionText ?? "Unknown version"} · Router / idle sleep / template file: supported · Metrics: {(probe.Capabilities.SupportsMetrics ? "supported" : "unavailable (inference unaffected)")}"
                : $"验证通过 · {probe.VersionText ?? "版本未知"} · Router / 空闲休眠 / 模板文件：支持 · Metrics：{(probe.Capabilities.SupportsMetrics ? "支持" : "不可用（不影响推理）")}";
            DeviceText.Text = CompactDeviceOutput(probe.DeviceOutput);
            await ReloadProfilesAsync(root, cancellationToken);
            StatusText.Text = saveSelection
                ? AppLanguageManager.Choose("Runtime 已保存。可以扫描模型；尚未加载模型或修改 ChatGPT 配置。", "Runtime saved. Models can now be scanned; no model has been loaded and ChatGPT configuration has not been changed.")
                : AppLanguageManager.Choose("已读取上次保存的 Runtime；尚未加载模型或修改 ChatGPT 配置。", "The previously saved Runtime was loaded. No model has been loaded and ChatGPT configuration has not been changed.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void EnsureRuntimeFolders(string runtimeRoot)
    {
        var missingDirectories = new[] { "models", "scripts", "logs" }
            .Where(name => !Directory.Exists(Path.Combine(runtimeRoot, name)))
            .ToArray();
        if (missingDirectories.Length == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                $"缺少目录：{string.Join("、", missingDirectories)}。是否现在创建？",
                $"Missing directories: {string.Join(", ", missingDirectories)}. Create them now?"),
            AppLanguageManager.Choose("初始化 llama.cpp 目录", "Initialize llama.cpp Directories"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var directory in missingDirectories)
        {
            Directory.CreateDirectory(Path.Combine(runtimeRoot, directory));
        }
    }

    private async void ScanModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_runtimeIsValid || string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            return;
        }

        SetBusy(true);
        try
        {
            StatusText.Text = AppLanguageManager.Choose("正在递归扫描 GGUF；模型不会被加载或自动添加。", "Scanning recursively for GGUF files. Models will not be loaded or added automatically.");
            var modelsRoot = Path.Combine(_settings.LlamaRoot, "models");
            var scan = await Task.Run(
                () => _modelScanner.Scan(modelsRoot, _lifetime.Token),
                _lifetime.Token);
            _discoveredModels = scan.Models
                .Where(model => FindManagedProfile(model, _settings.LlamaRoot) is not { } existing
                                || IsProfileUnavailable(existing))
                .ToArray();
            DiscoveredModelsList.ItemsSource = _discoveredModels.Select(ModelListItem.FromCandidate).ToArray();
            AddModelButton.IsEnabled = false;

            StatusText.Text = AppLanguageManager.IsEnglish
                ? $"Scan complete: {scan.Models.Count} primary models, {scan.ExcludedFiles.Count} excluded files, {_discoveredModels.Count} not yet added."
                : $"扫描完成：{scan.Models.Count} 个主模型，{scan.ExcludedFiles.Count} 个文件被排除，{_discoveredModels.Count} 个尚未添加。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"扫描失败：{exception.Message}", $"Scan failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SearchDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_runtimeIsValid || string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            return;
        }

        var dialog = new ModelDownloadWindow(
            _settings.LlamaRoot,
            () => HasActiveOrQueuedDownloads
                ? AppLanguageManager.Choose(
                    "当前已有一个文件正在下载或等待；请完成或取消后再创建下一个任务。",
                    "One file is already downloading or waiting. Finish or cancel it before creating another task.")
                : GetModelDownloadBlockReason())
        {
            Owner = this,
        };
        DownloadStatusSummaryText.Text = AppLanguageManager.Choose("搜索窗口已打开；选中版本后会创建下载任务", "Search window opened. Select a version to create a download task.");
        if (dialog.ShowDialog() != true)
        {
            RefreshDownloadUi();
            return;
        }

        if (dialog.SelectedDownload is not null)
        {
            EnqueueDownload(dialog.SelectedDownload);
        }
        else if (dialog.SelectedMtpDownload is not null)
        {
            EnqueueDownload(dialog.SelectedMtpDownload);
        }
        else if (dialog.SelectedVisionDownload is not null)
        {
            EnqueueDownload(dialog.SelectedVisionDownload);
        }
    }

    private void DiscoveredModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddModelButton.IsEnabled = !_busy && DiscoveredModelsList.SelectedIndex >= 0;
    }

    private void ManagedModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedProfile = (ManagedModelsList.SelectedItem as ModelListItem)?.Profile;
        var hasSelection = selectedProfile is not null;
        var unavailable = selectedProfile is not null && IsProfileUnavailable(selectedProfile);
        EditModelButton.IsEnabled = !_busy && selectedProfile is not null && CanEditModelParameters(selectedProfile);
        ChangeModelTypeButton.IsEnabled = !_busy && selectedProfile is not null && CanEditModelParameters(selectedProfile);
        AutoFitModelButton.IsEnabled = !_busy && hasSelection && CanUseFitTool();
        ModelDetailsButton.IsEnabled = !_busy && hasSelection;
        DetectReasoningCapabilityButton.IsEnabled = !_busy
            && selectedProfile is not null
            && CanEditModelParameters(selectedProfile);
        LoadExternalMtpButton.IsEnabled = !_busy && selectedProfile is not null && CanEditModelParameters(selectedProfile);
        RemoveExternalMtpButton.IsEnabled = !_busy
            && selectedProfile is { MtpSource: MtpSourceKind.External }
            && !string.IsNullOrWhiteSpace(selectedProfile.MtpDraftModelRelativePath)
            && CanEditModelParameters(selectedProfile);
        LoadExternalVisionButton.IsEnabled = !_busy && selectedProfile is not null && CanEditModelParameters(selectedProfile);
        RemoveExternalVisionButton.IsEnabled = !_busy
            && selectedProfile is { VisionSource: VisionSourceKind.External }
            && !string.IsNullOrWhiteSpace(selectedProfile.VisionProjectorRelativePath)
            && CanEditModelParameters(selectedProfile);
        ActivateLocalButton.IsEnabled = !_busy && hasSelection && !unavailable;
        ToggleModelVisibilityButton.IsEnabled = !_busy && hasSelection && !unavailable;
        DeleteLocalModelButton.IsEnabled = !_busy && hasSelection;
        ToggleModelVisibilityButton.Content = selectedProfile?.ShowInModePage == true
            ? AppLanguageManager.Choose("在模式卡片中隐藏", "Hide from Mode Page")
            : AppLanguageManager.Choose("在模式卡片中显示", "Show on Mode Page");
        SelectedModelDetailsText.Text = ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } selected
            ? FormatProfileSummary(selected.Profile)
            : AppLanguageManager.Choose("请选择模型以查看文件、来源和参数摘要。", "Select a model to view file, source, and parameter details.");
        UpdateManagedModelMutationButtons();
    }

    private async void AddModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || DiscoveredModelsList.SelectedItem is not ModelListItem { Candidate: not null } selected)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var added = await AddCandidateAsync(selected.Candidate, _lifetime.Token);
            StatusText.Text = added.Status;
            await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);

            _discoveredModels = _discoveredModels
                .Where(candidate => !string.Equals(
                    Path.GetFullPath(candidate.PrimaryPath),
                    Path.GetFullPath(selected.Candidate.PrimaryPath),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            DiscoveredModelsList.ItemsSource = _discoveredModels.Select(ModelListItem.FromCandidate).ToArray();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"添加模型失败：{exception.Message}", $"Failed to add model: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<(string Status, ModelProfile Profile)> AddCandidateAsync(
        GgufModelCandidate candidate,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        var previous = FindManagedProfile(candidate, runtimeRoot);
        if (previous is not null && !IsProfileUnavailable(previous))
        {
            throw new InvalidOperationException(AppLanguageManager.Choose("该模型已经在管理列表中。", "This model is already in the managed list."));
        }

        var rebuilding = previous is not null;
        var defaultProfile = ModelProfileFactory.CreateDefault(
            candidate,
            runtimeRoot,
            previous is null
                ? _profiles
                : _profiles.Where(item => !string.Equals(item.Id, previous.Id, StringComparison.Ordinal)));
        var profile = previous is null
            ? defaultProfile
            : defaultProfile with
            {
                Id = previous.Id,
                Alias = previous.Alias,
                ShowInModePage = previous.ShowInModePage,
                DisplayOrder = previous.DisplayOrder,
            };
        var compatibility = await _modelArtifactTransaction.ExecuteAsync(
            runtimeRoot,
            profile.Id,
            _paths.RouterPresetFile,
            includeRouterPreset: false,
            async transactionCancellationToken =>
            {
                BatchScriptGenerator.EnsureCanWrite(runtimeRoot, profile.Id);
                var result = await CodexChatTemplateCompatibility.EnsureAsync(
                    profile,
                    runtimeRoot,
                    transactionCancellationToken);
                _ = BatchScriptGenerator.Generate(result.Profile, runtimeRoot);
                await _profileStore.SaveAsync(
                    runtimeRoot,
                    result.Profile,
                    transactionCancellationToken);
                await BatchScriptGenerator.WriteOwnedAsync(
                    runtimeRoot,
                    result.Profile,
                    transactionCancellationToken);
                return result;
            },
            cancellationToken);
        profile = compatibility.Profile;
        _profileStore.ClearMissingMarker(runtimeRoot, profile.Id);
        _missingProfileIds.Remove(profile.Id);
        if (rebuilding && (_settings.SelectedModelId == profile.Id || _settings.PendingModelId == profile.Id))
        {
            _applyLocalProfileOnNextLaunch = true;
        }

        var contextReminder = profile.ContextSize < 1_024
            ? AppLanguageManager.Choose(" 请先按模型和硬件编辑 Context，之后才能切换到 ChatGPT Desktop。", " Edit Context according to the model and hardware before switching ChatGPT Desktop.")
            : string.Empty;
        var status = rebuilding
            ? AppLanguageManager.Choose($"已重新添加 {profile.DisplayName} 并按默认值重建配置；磁盘中的模型数据未改动。下次启动本地模型时会应用新配置。{contextReminder}", $"Re-added {profile.DisplayName} and rebuilt its configuration from defaults. Model data on disk was not changed. The new configuration will be applied on the next local launch.{contextReminder}")
            : compatibility.TemplateGenerated
            ? AppLanguageManager.Choose($"已添加 {profile.DisplayName}；已从该 GGUF 生成模型专用 Codex 模板并交由 llama.cpp 加载。{contextReminder}", $"Added {profile.DisplayName}. A model-specific Codex template was generated from the GGUF for llama.cpp to load.{contextReminder}")
            : AppLanguageManager.Choose($"已添加 {profile.DisplayName}，并生成可独立运行的 BAT；尚未加载模型或修改 ChatGPT 配置。{contextReminder}", $"Added {profile.DisplayName} and generated a standalone BAT. No model has been loaded and ChatGPT configuration has not been changed.{contextReminder}");
        return (status, profile);
    }

    private async Task ReloadProfilesAsync(string runtimeRoot, CancellationToken cancellationToken)
    {
        var pendingProfilePath = _settings.PendingMode == ProviderMode.Local
            && !string.IsNullOrWhiteSpace(_settings.PendingModelId)
            ? Path.Combine(
                JsonModelProfileStore.GetProfilesDirectory(runtimeRoot),
                _settings.PendingModelId + ".json")
            : null;
        var pendingConfigurationWasMissing = pendingProfilePath is not null
            && !File.Exists(pendingProfilePath);
        var recreatedProfiles = await _profileStore.RecreateMissingProfilesFromBackupsAsync(
            runtimeRoot,
            cancellationToken);
        var result = await _profileStore.LoadAsync(runtimeRoot, cancellationToken);
        var pendingProfileRecreated = await TryRecreatePendingProfileAsync(
            runtimeRoot,
            result.Profiles,
            cancellationToken);
        if (pendingProfileRecreated)
        {
            result = await _profileStore.LoadAsync(runtimeRoot, cancellationToken);
        }

        _profiles = await ReconcileMissingProfilesAsync(runtimeRoot, result.Profiles, cancellationToken);
        _profiles = await ReconcileMtpAvailabilityAsync(runtimeRoot, _profiles, cancellationToken);
        _profiles = await ReconcileVisionAvailabilityAsync(runtimeRoot, _profiles, cancellationToken);
        if (pendingConfigurationWasMissing
            && _profiles.Any(profile => string.Equals(
                profile.Id,
                _settings.PendingModelId,
                StringComparison.Ordinal)))
        {
            _profilesRecreatedFromMissingConfiguration.Add(_settings.PendingModelId!);
        }

        if (_settings.PendingMode == ProviderMode.Local
            && string.IsNullOrWhiteSpace(_settings.PendingModelRelativePath)
            && _profiles.FirstOrDefault(profile => string.Equals(
                profile.Id,
                _settings.PendingModelId,
                StringComparison.Ordinal)) is { } pendingProfile)
        {
            await MutateSettingsAsync(
                settings => settings with
                {
                    PendingModelRelativePath = pendingProfile.ModelRelativePath,
                    PendingModelDisplayName = pendingProfile.DisplayName,
                },
                cancellationToken);
        }

        ManagedModelsList.ItemsSource = _profiles.Select(CreateModelListItem).ToArray();
        EditModelButton.IsEnabled = false;
        ChangeModelTypeButton.IsEnabled = false;
        AutoFitModelButton.IsEnabled = false;
        ModelDetailsButton.IsEnabled = false;
        DetectReasoningCapabilityButton.IsEnabled = false;
        LoadExternalMtpButton.IsEnabled = false;
        RemoveExternalMtpButton.IsEnabled = false;
        LoadExternalVisionButton.IsEnabled = false;
        RemoveExternalVisionButton.IsEnabled = false;
        SelectedModelDetailsText.Text = AppLanguageManager.Choose("请选择模型以查看文件、来源和参数摘要。", "Select a model to view file, source, and parameter details.");
        if (recreatedProfiles > 0 || pendingProfileRecreated)
        {
            var count = recreatedProfiles + (pendingProfileRecreated ? 1 : 0);
            StatusText.Text = AppLanguageManager.Choose($"检测到配置文件丢失，已按默认参数重建 {count} 个模型配置。", $"Missing configuration files were detected. {count} model profiles were rebuilt with default parameters.");
        }
        else if (result.Diagnostics.Count > 0)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"部分 Profile 未载入：{string.Join(" ", result.Diagnostics)}",
                $"Some profiles could not be loaded: {string.Join(" ", result.Diagnostics)}");
        }

        RefreshEggUiState();
    }

    private async Task<bool> TryRecreatePendingProfileAsync(
        string runtimeRoot,
        IReadOnlyList<ModelProfile> loadedProfiles,
        CancellationToken cancellationToken)
    {
        if (_settings.PendingMode != ProviderMode.Local
            || string.IsNullOrWhiteSpace(_settings.PendingModelId)
            || string.IsNullOrWhiteSpace(_settings.PendingModelRelativePath)
            || loadedProfiles.Any(profile => string.Equals(
                profile.Id,
                _settings.PendingModelId,
                StringComparison.Ordinal)))
        {
            return false;
        }

        var root = Path.GetFullPath(runtimeRoot);
        var modelsRoot = Path.GetFullPath(Path.Combine(root, "models"));
        var modelPath = Path.GetFullPath(Path.Combine(root, _settings.PendingModelRelativePath));
        var modelsPrefix = modelsRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!modelPath.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(modelPath)
            || loadedProfiles.Any(profile => string.Equals(
                profile.Alias,
                _settings.PendingModelId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var scan = await Task.Run(
            () => _modelScanner.Scan(modelsRoot, cancellationToken),
            cancellationToken);
        var candidate = scan.Models.FirstOrDefault(item => string.Equals(
            Path.GetFullPath(item.PrimaryPath),
            modelPath,
            StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return false;
        }

        var recreated = ModelProfileFactory.CreateDefault(
            candidate,
            root,
            loadedProfiles,
            detectModelType: false) with
        {
            Id = _settings.PendingModelId,
            Alias = _settings.PendingModelId,
            DisplayName = string.IsNullOrWhiteSpace(_settings.PendingModelDisplayName)
                ? candidate.DisplayName
                : _settings.PendingModelDisplayName,
            ShowInModePage = true,
            DisplayOrder = loadedProfiles.Count == 0
                ? 0
                : loadedProfiles.Max(profile => profile.DisplayOrder) + 1,
            ModelType = ModelType.Unknown,
            ModelTypeSource = ModelTypeSource.Legacy,
        };
        await _profileStore.SaveAsync(root, recreated, cancellationToken);
        return true;
    }

    private async void EditModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        await EditProfileAsync(selected.Profile);
    }

    private async void ChangeModelTypeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected
            || !CanEditModelParameters(selected.Profile, forceClientRefresh: true))
        {
            return;
        }

        var profile = selected.Profile;
        var dialog = new ModelTypeSelectionWindow(profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedModelType is not { } selectedType
            || selectedType == profile.ModelType)
        {
            return;
        }

        var rebuild = profile.ModelType != ModelType.Unknown;
        if (rebuild && MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"把“{profile.DisplayName}”从 {FormatModelType(profile.ModelType)} 更改为 {FormatModelType(selectedType)}？\n\n"
                    + "现有参数配置将被重新建立，所有运行参数恢复为新类型的默认状态；模型文件不会改变。",
                    $"Change \"{profile.DisplayName}\" from {FormatModelType(profile.ModelType)} to {FormatModelType(selectedType)}?\n\n"
                    + "The existing parameter profile will be rebuilt and all runtime parameters will return to the defaults for the new type. Model files will not be changed."),
                AppLanguageManager.Choose("重新建立模型配置", "Rebuild Model Profile"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var updated = rebuild
            ? ModelProfileFactory.RecreateForModelType(profile, selectedType)
            : AssignPreviouslyUnknownModelType(profile, selectedType);

        SetBusy(true);
        try
        {
            await SaveProfileArtifactsAsync(updated, _lifetime.Token);
            await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            StatusText.Text = rebuild
                ? AppLanguageManager.Choose(
                    $"已将 {updated.DisplayName} 更改为 {FormatModelType(selectedType)}，并重新建立参数配置。",
                    $"Changed {updated.DisplayName} to {FormatModelType(selectedType)} and rebuilt its parameter profile.")
                : AppLanguageManager.Choose(
                    $"已将 {updated.DisplayName} 指定为 {FormatModelType(selectedType)}。",
                    $"Set {updated.DisplayName} to {FormatModelType(selectedType)}.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"更改模型类型失败：{exception.Message}",
                $"Failed to change the model type: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static ModelProfile AssignPreviouslyUnknownModelType(ModelProfile profile, ModelType modelType)
    {
        var extra = profile.ExtraArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (modelType == ModelType.Dense)
        {
            extra.Remove("cpu-moe");
            extra.Remove("n-cpu-moe");
        }

        return profile with
        {
            ModelType = modelType,
            ModelTypeSource = ModelTypeSource.UserSelected,
            MoeExpertPlacement = null,
            CpuMoeLayers = null,
            ExtraArguments = extra,
        };
    }

    private async void AutoFitModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        if (!CanEditModelParameters(selected.Profile, forceClientRefresh: true))
        {
            StatusText.Text = AppLanguageManager.Choose(
                "Local 客户端、后台 Agent 或 llama 服务运行期间不能修改任何模型参数；请先停止 Local 服务。",
                "Model parameters cannot be changed while the Local client, background Agent, or llama service is running. Stop Local services first.");
            return;
        }

        SetBusy(true);
        try
        {
            await ApplyLlamaFitAsync(selected.Profile, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose(
                $"llama 自动适配失败：{exception.Message}",
                $"llama auto-fit failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OfferAutoFitAfterAddAsync(ModelProfile profile)
    {
        if (!CanUseFitTool())
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                $"是否让 llama.cpp 为“{profile.DisplayName}”计算一次可选参数建议？\n\n"
                + "这会运行 llama-fit-params，但不会启动 ChatGPT、不会修改其他模型，也不会强制采用结果。",
                $"Let llama.cpp calculate optional parameter recommendations for \"{profile.DisplayName}\"?\n\n"
                + "This runs llama-fit-params, but does not start ChatGPT, modify other models, or force the result to be applied."),
            AppLanguageManager.Choose("可选的 llama 参数适配", "Optional llama Parameter Fit"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await ApplyLlamaFitAsync(profile, _lifetime.Token);
        }
    }

    private async Task ApplyLlamaFitAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        }

        if (_settings.SelectedMode != ProviderMode.OpenAI)
        {
            throw new InvalidOperationException(AppLanguageManager.Choose(
                "请先切换到 OpenAI，再运行 llama 参数适配，避免与 Local Router 争用硬件。",
                "Switch to OpenAI before running llama parameter fitting to avoid competing with the Local Router for hardware."));
        }

        if (IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            throw new InvalidOperationException(AppLanguageManager.Choose(
                "llama 服务正在运行。请先释放并结束 Local 服务，再执行参数适配。",
                "A llama service is running. Release and stop Local services before fitting parameters."));
        }

        StatusText.Text = AppLanguageManager.Choose(
            $"正在由 llama-fit-params 分析 {profile.DisplayName}；不会加载模型权重。",
            $"llama-fit-params is analyzing {profile.DisplayName}; model weights will not be loaded.");
        var modelPath = Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath));
        var recommendation = await _fitParamsRunner.RecommendAsync(
            _settings.LlamaRoot,
            modelPath,
            minimumContextSize: 4096,
            cancellationToken);
        var details = AppLanguageManager.IsEnglish
            ? $"llama.cpp recommendation:\nContext: {recommendation.ContextSize:N0}\nGPU Layers: {recommendation.GpuLayers:N0}" +
              (recommendation.TensorSplit is null ? string.Empty : $"\nTensor Split: {recommendation.TensorSplit}") +
              (recommendation.TensorBufferOverrides is null ? string.Empty : "\nTensor Buffer: generated by llama") +
              $"\n\nCurrent: Context {profile.ContextSize:N0}, GPU Layers {profile.GpuLayers}\n\nApply these values to the model's current parameters?"
            : $"llama.cpp 建议：\nContext：{recommendation.ContextSize:N0}\nGPU Layers：{recommendation.GpuLayers:N0}" +
              (recommendation.TensorSplit is null ? string.Empty : $"\nTensor Split：{recommendation.TensorSplit}") +
              (recommendation.TensorBufferOverrides is null ? string.Empty : "\nTensor Buffer：由 llama 自动生成") +
              $"\n\n当前：Context {profile.ContextSize:N0}，GPU Layers {profile.GpuLayers}\n\n是否应用到该模型当前参数？";
        if (MessageBox.Show(
                this,
                details,
                AppLanguageManager.Choose("llama 参数建议", "llama Parameter Recommendation"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            StatusText.Text = AppLanguageManager.Choose("已保留原参数；llama 建议未应用。", "Original parameters kept; the llama recommendation was not applied.");
            return;
        }

        var extra = profile.ExtraArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        extra.Remove("tensor-split");
        extra.Remove("override-tensor");
        if (!string.IsNullOrWhiteSpace(recommendation.TensorSplit))
        {
            extra["tensor-split"] = recommendation.TensorSplit;
        }

        if (!string.IsNullOrWhiteSpace(recommendation.TensorBufferOverrides))
        {
            extra["override-tensor"] = recommendation.TensorBufferOverrides;
        }

        var updated = profile with
        {
            ContextSize = recommendation.ContextSize,
            CompactionSafetyReserve = profile.CompactionSafetyReserve < recommendation.ContextSize
                ? profile.CompactionSafetyReserve
                : ModelProfile.CalculateInitialCompactionSafetyReserve(recommendation.ContextSize),
            GpuLayers = recommendation.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExtraArguments = extra,
        };
        var saveDefault = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                "是否同时把这组参数设为该模型的专用默认值？\n\n选择“否”只更新当前参数；其他模型始终不受影响。",
                "Also save these parameters as this model's dedicated defaults?\n\nSelecting No updates only the current parameters. Other models are never affected."),
            AppLanguageManager.Choose("保存该模型默认值", "Save Model Defaults"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (saveDefault)
        {
            updated = ModelProfileFactory.SaveCurrentParametersAsDefault(updated);
        }

        await SaveProfileArtifactsAsync(updated, cancellationToken);
        await ReloadProfilesAsync(_settings.LlamaRoot, cancellationToken);
        StatusText.Text = saveDefault
            ? AppLanguageManager.Choose(
                $"已应用 llama 建议，并设为 {updated.DisplayName} 的专用默认值。",
                $"Applied the llama recommendation and saved it as the dedicated defaults for {updated.DisplayName}.")
            : AppLanguageManager.Choose(
                $"已应用 llama 建议到 {updated.DisplayName}；专用默认值未改变。",
                $"Applied the llama recommendation to {updated.DisplayName}; dedicated defaults were not changed.");
    }

    private async Task SaveProfileArtifactsAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        if (!CanEditModelParameters(profile, forceClientRefresh: true))
        {
            throw new InvalidOperationException(
                AppLanguageManager.Choose(
                    "正在运行的本地模型不能保存参数。请先关闭 ChatGPT 客户端后再修改该模型。",
                    "Parameters cannot be saved for a running local model. Close the ChatGPT client before editing it."));
        }

        await _modelArtifactTransaction.ExecuteAsync(
            runtimeRoot,
            profile.Id,
            _paths.RouterPresetFile,
            includeRouterPreset: false,
            async transactionCancellationToken =>
            {
                BatchScriptGenerator.EnsureCanWrite(runtimeRoot, profile.Id);
                _ = BatchScriptGenerator.Generate(profile, runtimeRoot);
                await _profileStore.SaveAsync(runtimeRoot, profile, transactionCancellationToken);
                await BatchScriptGenerator.WriteOwnedAsync(runtimeRoot, profile, transactionCancellationToken);
                return true;
            },
            cancellationToken);
    }

    private async Task WriteRouterPresetAtomicallyAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(_paths.RouterPresetFile);
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("Router preset 缺少父目录。", "The Router preset has no parent directory."));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".router-preset.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                RouterPresetGenerator.Generate(profile, runtimeRoot, loadOnStartup: false),
                new System.Text.UTF8Encoding(false),
                cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the preset write result; Agent never reads temp files.
            }
        }
    }

    private bool CanEditModelParameters(ModelProfile? profile = null, bool forceClientRefresh = false)
    {
        profile ??= (ManagedModelsList.SelectedItem as ModelListItem)?.Profile;
        return profile is not null && CanEditProfile(profile, forceClientRefresh);
    }

    private bool IsManagedProfileInUse(ModelProfile profile, bool forceClientRefresh = false) =>
        _settings.SelectedMode == ProviderMode.Local
        && string.Equals(_settings.SelectedModelId, profile.Id, StringComparison.Ordinal)
        && (GetClientRunningSnapshot(forceClientRefresh)
            || IsAgentRunning()
            || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot));

    private void UpdateManagedModelMutationButtons()
    {
        var profile = (ManagedModelsList.SelectedItem as ModelListItem)?.Profile;
        var canRemove = !_busy && profile is not null && !IsManagedProfileInUse(profile);
        var canMutate = canRemove && profile is not null && !IsProfileUnavailable(profile);
        ToggleModelVisibilityButton.IsEnabled = canMutate;
        DeleteLocalModelButton.IsEnabled = canRemove;
        ChangeModelTypeButton.IsEnabled = canMutate;
        DetectReasoningCapabilityButton.IsEnabled = canMutate;
        LoadExternalMtpButton.IsEnabled = canMutate;
        RemoveExternalMtpButton.IsEnabled = canMutate
            && profile is { MtpSource: MtpSourceKind.External }
            && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath);
        LoadExternalVisionButton.IsEnabled = canMutate;
        RemoveExternalVisionButton.IsEnabled = canMutate
            && profile is { VisionSource: VisionSourceKind.External }
            && !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath);
    }

    private bool CanUseFitTool() =>
        CanEditModelParameters()
        && !string.IsNullOrWhiteSpace(_settings.LlamaRoot)
        && File.Exists(Path.Combine(_settings.LlamaRoot, "llama-fit-params.exe"));

    private async void ModelDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        await ShowProfileDetailsAsync(selected.Profile);
    }

    private async void ActivateLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var applyProfileOnLaunch = _applyLocalProfileOnNextLaunch;
        _applyLocalProfileOnNextLaunch = false;
        if (_busy
            || !_runtimeIsValid
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || string.IsNullOrWhiteSpace(_codexHome)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected
            || IsProfileUnavailable(selected.Profile))
        {
            return;
        }

        await ActivateLocalAsync(selected.Profile, applyProfileOnLaunch);
    }

    private async Task ActivateLocalAsync(
        ModelProfile activeProfile,
        bool applyProfileOnLaunch,
        bool busyStateOwnedByCaller = false)
    {
        await WaitForSettingsMutationsAsync();
        if (_busy && !busyStateOwnedByCaller)
        {
            return;
        }

        if (!busyStateOwnedByCaller)
        {
            SetBusy(true);
        }
        try
        {
            var runtimeRoot = _settings.LlamaRoot
                ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
            activeProfile = await InvalidateStaleMtpValidationAsync(
                activeProfile,
                runtimeRoot,
                _lifetime.Token);
            activeProfile = await InvalidateStaleVisionValidationAsync(
                activeProfile,
                runtimeRoot,
                _lifetime.Token);
            if (activeProfile.ExposeReasoningEffortInChatGpt)
            {
                // This is only a local signature guard for an enabled feature. Native capability
                // detection belongs to Local Model Management and must never block startup.
                activeProfile = await InvalidateStaleReasoningCapabilityAsync(
                    activeProfile,
                    runtimeRoot,
                    _lifetime.Token);
            }

            var clientRunning = GetClientRunningSnapshot(force: true);
            ModeSwitchGuard.EnsureCanSwitch(
                _settings.SelectedMode,
                ProviderMode.Local,
                clientRunning);
            var isSameLocalModel = _settings.SelectedMode == ProviderMode.Local
                && string.Equals(
                    _settings.SelectedModelId,
                    activeProfile.Id,
                    StringComparison.Ordinal);
            if (isSameLocalModel
                && clientRunning
                && string.IsNullOrWhiteSpace(activeProfile.ChatTemplateRelativePath))
            {
                throw new InvalidOperationException(
                    AppLanguageManager.Choose(
                        "当前 Local 模型正在运行，无法检查并应用模型专用 Chat Template。请先完全关闭 ChatGPT Desktop。",
                        "The current Local model is running, so its model-specific Chat Template cannot be checked or applied. Close ChatGPT Desktop completely first."));
            }

            var updateActivePreset = _settings.SelectedMode == ProviderMode.Local
                && string.Equals(_settings.SelectedModelId, activeProfile.Id, StringComparison.Ordinal);
            var compatibility = await _modelArtifactTransaction.ExecuteAsync(
                runtimeRoot,
                activeProfile.Id,
                _paths.RouterPresetFile,
                updateActivePreset,
                async transactionCancellationToken =>
                {
                    var result = await CodexChatTemplateCompatibility.EnsureAsync(
                        activeProfile,
                        runtimeRoot,
                        transactionCancellationToken);
                    if (!result.TemplateGenerated)
                    {
                        return result;
                    }

                    BatchScriptGenerator.EnsureCanWrite(runtimeRoot, result.Profile.Id);
                    _ = BatchScriptGenerator.Generate(result.Profile, runtimeRoot);
                    await _profileStore.SaveAsync(
                        runtimeRoot,
                        result.Profile,
                        transactionCancellationToken);
                    await BatchScriptGenerator.WriteOwnedAsync(
                        runtimeRoot,
                        result.Profile,
                        transactionCancellationToken);
                    if (updateActivePreset)
                    {
                        await WriteRouterPresetAtomicallyAsync(
                            result.Profile,
                            runtimeRoot,
                            transactionCancellationToken);
                    }

                    return result;
                },
                _lifetime.Token);
            if (compatibility.TemplateGenerated)
            {
                activeProfile = compatibility.Profile;
                await ReloadProfilesAsync(runtimeRoot, _lifetime.Token);
            }

            if (activeProfile.ContextSize < 1_024)
            {
                throw new InvalidOperationException(
                    AppLanguageManager.Choose(
                        "该模型尚未设置可供 ChatGPT Desktop 声明的 Context。请根据模型和硬件编辑 Profile；Egg Launcher 不会自动替你选择。",
                        "This model has no Context value for ChatGPT Desktop. Edit its profile based on the model and hardware; Egg Launcher will not choose one automatically."));
            }

            if (isSameLocalModel && !compatibility.TemplateGenerated && !applyProfileOnLaunch)
            {
                StatusText.Text = AppLanguageManager.Choose("正在确认 Local Router 与后台 Agent…", "Checking the Local Router and background Agent…");
                if (!await EnsureAgentRunningAsync(_lifetime.Token, activeProfile.Id))
                {
                    StatusText.Text = AppLanguageManager.Choose("找不到或无法启动 Launcher.Agent。", "Launcher.Agent was not found or could not be started.");
                    return;
                }

                var existingRuntimeReady = await WaitForLocalRouterAsync(
                    activeProfile.Alias,
                    _lifetime.Token);
                if (!existingRuntimeReady)
                {
                    var stopped = await StopAgentForExitAsync();
                    StatusText.Text = stopped
                        ? AppLanguageManager.Choose("Local Router 未在期限内就绪，后台服务已停止；请查看 Agent/Router 日志。", "The Local Router was not ready in time. Background services were stopped; check the Agent/Router logs.")
                        : AppLanguageManager.Choose("Local Router 未在期限内就绪，且后台服务未能完整停止；请查看诊断日志。", "The Local Router was not ready in time and background services did not stop completely; check the diagnostic logs.");
                    return;
                }

                activeProfile = await CaptureMtpValidationSuccessAsync(
                    activeProfile,
                    runtimeRoot,
                    _lifetime.Token);

                StatusText.Text = FormatLaunchResult(
                    await LaunchClientAndRefreshAsync(_lifetime.Token),
                    AppLanguageManager.Choose($"Local Router 已就绪：{activeProfile.DisplayName}", $"Local Router ready: {activeProfile.DisplayName}"));
                return;
            }

            var isChangingLocalModel = _settings.SelectedMode == ProviderMode.Local;
            if (isChangingLocalModel && clientRunning)
            {
                throw new InvalidOperationException(AppLanguageManager.Choose("请先完全关闭 ChatGPT Desktop 后再切换本地模型。", "Close ChatGPT Desktop completely before switching local models."));
            }

            StatusText.Text = isChangingLocalModel
                ? AppLanguageManager.Choose("正在事务性更新 Local 配置，并等待 Agent 重启 Router…", "Updating the Local configuration transactionally and waiting for the Agent to restart the Router…")
                : AppLanguageManager.Choose("正在启动后台 Agent 并准备 Local 配置…", "Starting the background Agent and preparing the Local configuration…");
            if (!await EnsureAgentRunningAsync(_lifetime.Token, activeProfile.Id))
            {
                StatusText.Text = AppLanguageManager.Choose("找不到或无法启动 Launcher.Agent；未修改 ChatGPT 配置。", "Launcher.Agent was not found or could not be started. The ChatGPT configuration was not changed.");
                return;
            }

            var coordinator = new ModeSwitchCoordinator(
                _settingsStore,
                _clientDetector,
                new ChatGptConfigTransactionService(_clientDetector),
                _paths);
            var result = await coordinator.SwitchToLocalAsync(
                new LocalModeSwitchRequest
                {
                    CodexHome = _codexHome,
                    RuntimeRoot = runtimeRoot,
                    Profile = activeProfile,
                    RouterPort = _settings.RouterPort,
                },
                _lifetime.Token);
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            await RefreshEnvironmentAsync(_lifetime.Token);
            if (!await EnsureAgentRunningAsync(_lifetime.Token, activeProfile.Id))
            {
                StatusText.Text = AppLanguageManager.Choose("Local 配置已应用，但后台 Agent 无法启动；请勿启动 ChatGPT，并检查 Agent 日志。", "The Local configuration was applied, but the background Agent could not start. Do not start ChatGPT; check the Agent logs.");
                return;
            }

            var runtimeReady = await WaitForLocalRouterAsync(
                activeProfile.Alias,
                _lifetime.Token);
            if (!runtimeReady)
            {
                var stopped = await StopAgentForExitAsync();
                StatusText.Text = stopped
                    ? AppLanguageManager.Choose("Local 配置已应用，但 Router 未在期限内就绪，后台服务已停止；请查看 Agent/Router 日志。", "The Local configuration was applied, but the Router was not ready in time. Background services were stopped; check the Agent/Router logs.")
                    : AppLanguageManager.Choose("Local Router 未就绪，且后台服务未能完整停止；请查看诊断日志。", "The Local Router was not ready and background services did not stop completely; check the diagnostic logs.");
                return;
            }

            activeProfile = await CaptureMtpValidationSuccessAsync(
                activeProfile,
                runtimeRoot,
                _lifetime.Token);

            StatusText.Text = FormatLaunchResult(
                await LaunchClientAndRefreshAsync(_lifetime.Token),
                AppLanguageManager.Choose($"已切换到 Local：{activeProfile.DisplayName}。Router 已就绪", $"Switched to Local: {activeProfile.DisplayName}. Router ready"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!GetClientRunningSnapshot(force: true) && IsAgentRunning())
            {
                _ = await StopAgentForExitAsync();
            }

            StatusText.Text = AppLanguageManager.Choose($"Local 模式切换失败：{exception.Message}", $"Failed to switch to Local mode: {exception.Message}");
        }
        finally
        {
            if (!busyStateOwnedByCaller)
            {
                SetBusy(false);
            }
        }
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy && !_launchInProgress)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse<ProviderMode>(tag, ignoreCase: true, out var targetMode))
        {
            return;
        }

        if (targetMode == ProviderMode.Local)
        {
            LauncherTabs.SelectedItem = LocalModelsTab;
            StatusText.Text = _profiles.Count == 0
                ? AppLanguageManager.Choose("请先配置 Runtime、扫描并添加本地模型。", "Configure a Runtime, scan, and add a local model first.")
                : AppLanguageManager.Choose("请选择一个已管理模型，再点击“应用 Local 模式”。", "Select a managed model, then click Apply Local Mode.");
            return;
        }

        await ActivateOpenAiAsync();
    }

    private async Task ActivateOpenAiAsync(bool busyStateOwnedByCaller = false)
    {
        await WaitForSettingsMutationsAsync();
        if (_busy && !busyStateOwnedByCaller)
        {
            return;
        }

        if (!busyStateOwnedByCaller)
        {
            SetBusy(true);
        }
        try
        {
            if (_settings.SelectedMode == ProviderMode.OpenAI)
            {
                if (IsAgentRunning() && !await StopAgentForExitAsync())
                {
                    StatusText.Text = AppLanguageManager.Choose("Local 后台服务未能安全停止，本次未启动 OpenAI 客户端。", "Local background services could not be stopped safely. The OpenAI client was not started.");
                    return;
                }

                StatusText.Text = FormatLaunchResult(
                    await LaunchClientAndRefreshAsync(_lifetime.Token),
                    AppLanguageManager.Choose("当前为 OpenAI 模式", "OpenAI mode is active"));
                return;
            }

            ModeSwitchGuard.EnsureCanSwitch(
                _settings.SelectedMode,
                ProviderMode.OpenAI,
                GetClientRunningSnapshot(force: true));
            var coordinator = new ModeSwitchCoordinator(
                _settingsStore,
                _clientDetector,
                new ChatGptConfigTransactionService(_clientDetector),
                _paths);
            await coordinator.SwitchToOpenAIAsync(_lifetime.Token);
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            await RefreshEnvironmentAsync(_lifetime.Token);
            if (IsAgentRunning() && !await StopAgentForExitAsync())
            {
                StatusText.Text = AppLanguageManager.Choose("OpenAI 配置已恢复，但 Local 后台服务未能安全停止；本次未启动客户端。", "The OpenAI configuration was restored, but Local background services could not be stopped safely. The client was not started.");
                return;
            }

            StatusText.Text = FormatLaunchResult(
                await LaunchClientAndRefreshAsync(_lifetime.Token),
                AppLanguageManager.Choose("OpenAI Provider 字段已恢复；Local 模型选择已保留", "OpenAI Provider fields restored; the Local model selection was preserved"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"OpenAI 模式恢复失败：{exception.Message}", $"Failed to restore OpenAI mode: {exception.Message}");
        }
        finally
        {
            if (!busyStateOwnedByCaller)
            {
                SetBusy(false);
            }
        }
    }

    private async void RefreshMonitoringButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshMonitoringAsync(forceHardwareRefresh: true);

    private async void RefreshHardwareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _monitorRefreshInProgress)
        {
            return;
        }

        RefreshHardwareButton.IsEnabled = false;
        UiMotion.RotateOnce(RefreshHardwareIcon);
        try
        {
            await RefreshMonitoringAsync(forceHardwareRefresh: true);
            StatusText.Text = AppLanguageManager.Choose("设备硬件信息已刷新。", "Hardware information refreshed.");
        }
        finally
        {
            RefreshHardwareButton.IsEnabled = true;
        }
    }

    private async Task RefreshMonitoringAsync(bool forceHardwareRefresh)
    {
        if (_monitorRefreshInProgress || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _monitorRefreshInProgress = true;
        try
        {
            if (forceHardwareRefresh || _hardwareInventory is null)
            {
                HardwareInventoryText.Text = AppLanguageManager.Choose(
                    "正在读取 Windows 硬件清单…",
                    "Reading Windows hardware inventory…");
                _hardwareInventory = await _hardwareInventoryReader.ReadAsync(_lifetime.Token);
            }

            HardwareInventoryText.Text = FormatHomeHardwareInventory(_hardwareInventory, _runtimeDeviceOutput);
            var performance = await Task.Run(
                () => _performanceSampler.Sample(_settings.LlamaRoot),
                _lifetime.Token);
            SystemPerformanceText.Text = FormatPerformance(performance);
            UpdateSidebarPerformance(performance);

            LoadCurrentModelButton.Tag = false;
            UnloadCurrentModelButton.Tag = false;
            var endpoint = await TryGetTrustedRouterEndpointAsync(_lifetime.Token);
            var profile = _profiles.FirstOrDefault(value => string.Equals(
                value.Id,
                _settings.SelectedModelId,
                StringComparison.Ordinal));
            var llamaRunning = IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot);
            UpdateLlamaBackendStatus(llamaRunning, profile);
            if (endpoint is null || profile is null)
            {
                SetTelemetryTarget(null, null);
                LlamaRuntimeText.Text = llamaRunning
                    ? AppLanguageManager.Text("Running")
                    : AppLanguageManager.Text("Stopped");
                LlamaRuntimeText.ToolTip = null;
                UpdateRuntimeActionButtons();
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            var management = new LlamaModelManagementClient(http);
            var models = await management.ListAsync(endpoint.BaseUri, cancellationToken: _lifetime.Token);
            var native = models.FirstOrDefault(model => string.Equals(model.Id, profile.Alias, StringComparison.Ordinal));
            if (native is null)
            {
                LlamaRuntimeText.Text = AppLanguageManager.Text("ModelStatusReading");
                LlamaRuntimeText.ToolTip = null;
                UpdateRuntimeActionButtons();
                return;
            }

            var stateText = native.Failed
                ? AppLanguageManager.Text("StatusAbnormal")
                : native.Status.ToLowerInvariant() switch
                {
                    "loaded" => AppLanguageManager.Text("ModelLoadedRunning"),
                    "loading" => AppLanguageManager.Text("ModelLoadingRunning"),
                    "sleeping" => AppLanguageManager.Text("ModelSleepingRunning"),
                    "unloaded" => AppLanguageManager.Text("ModelUnloadedRunning"),
                    _ => AppLanguageManager.Text("Running"),
                };
            LlamaRuntimeText.ToolTip = native.Failed
                ? AppLanguageManager.Choose(
                    $"模型 {native.Id} 上次加载失败，退出码 {native.ExitCode?.ToString() ?? "未知"}。",
                    $"The last load of model {native.Id} failed with exit code {native.ExitCode?.ToString() ?? "unknown"}.")
                : AppLanguageManager.Choose(
                    $"模型 {native.Id} · {TranslateModelStatus(native.Status)}",
                    $"Model {native.Id} · {TranslateModelStatus(native.Status)}");

            LoadCurrentModelButton.Tag = native.Status is "unloaded" or "sleeping";
            UnloadCurrentModelButton.Tag = native.Status is "loaded" or "loading" or "sleeping";
            if (string.Equals(native.Status, "loaded", StringComparison.OrdinalIgnoreCase))
            {
                SetTelemetryTarget(endpoint, profile.Alias);
            }
            else
            {
                SetTelemetryTarget(null, null);
            }

            LlamaRuntimeText.Text = stateText;
            UpdateRuntimeActionButtons();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LlamaRuntimeText.Text = AppLanguageManager.Text("StatusUnavailable");
            LlamaRuntimeText.ToolTip = exception.Message;
            UpdateLlamaBackendStatus(
                IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot),
                _profiles.FirstOrDefault(value => string.Equals(
                    value.Id,
                    _settings.SelectedModelId,
                    StringComparison.Ordinal)));
            LoadCurrentModelButton.Tag = false;
            UnloadCurrentModelButton.Tag = false;
            UpdateRuntimeActionButtons();
        }
        finally
        {
            _monitorRefreshInProgress = false;
        }
    }

    private void UpdateLlamaBackendStatus(bool llamaRunning, ModelProfile? profile)
    {
        var backend = llamaRunning && profile is not null
            ? LlamaComputeBackendResolver.Resolve(
                _runtimeDeviceOutput,
                profile.GpuLayers,
                profile.Device)
            : "–";
        LlamaBackendText.Text = backend;
        _trayLlamaBackend = backend;
    }

    private void SetTelemetryTarget(TrustedRouterEndpoint? endpoint, string? modelAlias)
    {
        if (Equals(_telemetryEndpoint, endpoint)
            && string.Equals(_telemetryModelAlias, modelAlias, StringComparison.Ordinal))
        {
            return;
        }

        _telemetryEndpoint = endpoint;
        _telemetryModelAlias = modelAlias;
        _telemetryClient?.ResetSamples();
        if (endpoint is null || string.IsNullOrWhiteSpace(modelAlias))
        {
            SidebarSpeedText.Text = "–";
            SidebarContextText.Text = "–";
            _telemetryRefreshTimer.Interval = TimeSpan.FromSeconds(2);
            return;
        }

        _telemetryHttpClient?.Dispose();
        _telemetryHttpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        _telemetryHttpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        _telemetryClient = new LlamaTelemetryClient(_telemetryHttpClient);
    }

    private async Task RefreshTelemetryAsync()
    {
        if (!GetClientRunningSnapshot())
        {
            ClearSidebarTelemetry();
            return;
        }

        if (_telemetryRefreshInProgress
            || _lifetime.IsCancellationRequested
            || _telemetryEndpoint is null
            || string.IsNullOrWhiteSpace(_telemetryModelAlias)
            || _telemetryClient is null)
        {
            return;
        }

        _telemetryRefreshInProgress = true;
        var endpoint = _telemetryEndpoint;
        var modelAlias = _telemetryModelAlias;
        var client = _telemetryClient;
        try
        {
            var telemetry = await client.ReadAsync(
                endpoint.BaseUri,
                modelAlias,
                _lifetime.Token);
            if (!Equals(_telemetryEndpoint, endpoint)
                || !string.Equals(_telemetryModelAlias, modelAlias, StringComparison.Ordinal)
                || !ReferenceEquals(_telemetryClient, client))
            {
                return;
            }

            if (!GetClientRunningSnapshot())
            {
                ClearSidebarTelemetry();
                return;
            }

            if (string.IsNullOrWhiteSpace(telemetry.Diagnostic))
            {
                UpdateSidebarTelemetry(telemetry);
                _telemetryRefreshTimer.Interval = telemetry.ProcessingSlots > 0
                    ? TimeSpan.FromMilliseconds(500)
                    : TimeSpan.FromSeconds(2);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _telemetryRefreshInProgress = false;
        }
    }

    private void UpdateRuntimeActionButtons()
    {
        LoadCurrentModelButton.IsEnabled = !_busy && LoadCurrentModelButton.Tag is true;
        UnloadCurrentModelButton.IsEnabled = !_busy && UnloadCurrentModelButton.Tag is true;
    }

    private async void LoadCurrentModelButton_Click(object sender, RoutedEventArgs e) =>
        await RunCurrentModelActionAsync(load: true);

    private async void UnloadCurrentModelButton_Click(object sender, RoutedEventArgs e) =>
        await RunCurrentModelActionAsync(load: false);

    private async Task RunCurrentModelActionAsync(bool load)
    {
        if (_busy)
        {
            return;
        }

        var profile = _profiles.FirstOrDefault(value => string.Equals(
            value.Id,
            _settings.SelectedModelId,
            StringComparison.Ordinal));
        var endpoint = await TryGetTrustedRouterEndpointAsync(_lifetime.Token);
        if (profile is null || endpoint is null)
        {
            StatusText.Text = AppLanguageManager.Choose("当前没有通过安全校验的 Local Router 与选中模型。", "No selected model and Local Router currently pass the security checks.");
            return;
        }

        SetBusy(true);
        try
        {
            StatusText.Text = load
                ? AppLanguageManager.Choose($"正在请求 llama.cpp 立即加载 {profile.DisplayName}…", $"Requesting llama.cpp to load {profile.DisplayName} now…")
                : AppLanguageManager.Choose($"正在请求 llama.cpp 立即释放 {profile.DisplayName}…", $"Requesting llama.cpp to unload {profile.DisplayName} now…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            var client = new LlamaModelManagementClient(http);
            var result = load
                ? await client.LoadAsync(endpoint.BaseUri, profile.Alias, _lifetime.Token)
                : await client.UnloadAsync(endpoint.BaseUri, profile.Alias, _lifetime.Token);
            StatusText.Text = result.Succeeded
                ? load
                    ? AppLanguageManager.Choose("llama.cpp 已接受加载请求；状态会持续刷新。", "llama.cpp accepted the load request; status will continue to refresh.")
                    : AppLanguageManager.Choose("llama.cpp 已接受释放请求；显存释放需要短暂时间。", "llama.cpp accepted the unload request; releasing VRAM may take a moment.")
                : result.Diagnostic ?? AppLanguageManager.Choose("llama.cpp 未接受操作。", "llama.cpp did not accept the operation.");
            await Task.Delay(500, _lifetime.Token);
            await RefreshMonitoringAsync(forceHardwareRefresh: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"llama 模型操作失败：{exception.Message}", $"llama model operation failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_runtimeIsValid)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await RefreshCacheAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CacheStatusText.Text = AppLanguageManager.Choose($"缓存读取失败：{exception.Message}", $"Failed to read the cache: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshCacheAsync(CancellationToken cancellationToken)
    {
        if (IsLocalRuntimeSessionActive(forceClientRefresh: true)
            || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            _cachedModels = [];
            CachedModelsList.ItemsSource = _cachedModels;
            CacheStatusText.Text = AppLanguageManager.Choose("Local 或其他 llama 服务正在使用当前 Runtime。请等待服务停止后再管理缓存。", "Local or another llama service is using this Runtime. Wait for it to stop before managing the cache.");
            return;
        }

        CacheStatusText.Text = AppLanguageManager.Choose("正在启动临时 llama Router 读取原生缓存清单；不会加载模型。", "Starting a temporary llama Router to read the native cache list; no model will be loaded.");
        var nativeModels = await WithTemporaryManagementRouterAsync(
            (client, endpoint, token) => client.ListAsync(endpoint, reload: true, token),
            cancellationToken);
        _cachedModels = nativeModels
            .Where(model => model.InCache && model.CanRemove)
            .Join(
                _profiles.Where(profile => profile.SourceKind == ModelSourceKind.LlamaCache),
                model => model.Id,
                profile => profile.RemoteModelId,
                (model, profile) => CacheModelListItem.Create(model, profile),
                StringComparer.Ordinal)
            .OrderBy(item => item.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CachedModelsList.ItemsSource = _cachedModels;
        DeleteCachedModelButton.IsEnabled = false;
        CacheStatusText.Text = _cachedModels.Count == 0
            ? AppLanguageManager.Choose(
                "没有同时满足“llama 可删除”和“Egg Launcher 已验证下载来源”的缓存。旧版或手动文件不会被猜测为可删除缓存。",
                "No cache is both removable by llama and verified by Egg Launcher as a download. Legacy or manually added files are not guessed to be removable caches.")
            : AppLanguageManager.Choose(
                $"llama.cpp 返回 {_cachedModels.Count} 个可安全管理的已登记缓存。",
                $"llama.cpp returned {_cachedModels.Count} registered caches that can be managed safely.");
    }

    private void CachedModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DeleteCachedModelButton.IsEnabled = !_busy && CachedModelsList.SelectedItem is CacheModelListItem;

    private async void DeleteCachedModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || CachedModelsList.SelectedItem is not CacheModelListItem selected)
        {
            return;
        }

        if (IsLocalRuntimeSessionActive(forceClientRefresh: true) || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            CacheStatusText.Text = AppLanguageManager.Choose("请先结束当前 Runtime 的 llama 服务，再删除缓存。", "Stop the llama service for the current Runtime before deleting cache files.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            AppLanguageManager.Choose(
                $"确定让 llama.cpp 删除缓存模型吗？\n\n{selected.Profile.DisplayName}\n{selected.Native.Id}\n{selected.Summary}\n\n"
                + "模型文件删除后无法由启动器恢复，需要重新下载；官方账户、项目和历史不会被触碰。",
                $"Delete this cached model through llama.cpp?\n\n{selected.Profile.DisplayName}\n{selected.Native.Id}\n{selected.Summary}\n\n"
                + "Egg Launcher cannot restore the model files after deletion; they must be downloaded again. Official accounts, projects, and history are not affected."),
            AppLanguageManager.Choose("删除 llama 模型缓存", "Delete llama Model Cache"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        var nativeModelDeleted = false;
        try
        {
            BatchScriptGenerator.EnsureCanWrite(_settings.LlamaRoot, selected.Profile.Id);
            var errors = ModelProfileValidator.Validate(selected.Profile, _settings.LlamaRoot);
            if (errors.Count > 0 || selected.Profile.SourceKind != ModelSourceKind.LlamaCache)
            {
                throw new InvalidDataException(AppLanguageManager.Choose("缓存 Profile 未通过删除前安全校验。", "The cache profile failed its pre-deletion safety check."));
            }

            var result = await WithTemporaryManagementRouterAsync(
                (client, endpoint, token) => client.RemoveCachedAsync(endpoint, selected.Native.Id, token),
                _lifetime.Token);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Diagnostic ?? AppLanguageManager.Choose("llama.cpp 拒绝删除该缓存。", "llama.cpp refused to delete this cache."));
            }

            nativeModelDeleted = true;
            var cleanupWarnings = new List<string>();
            try
            {
                BatchScriptGenerator.DeleteOwned(_settings.LlamaRoot, selected.Profile.Id);
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add(AppLanguageManager.Choose($"BAT 清理失败：{exception.Message}", $"BAT cleanup failed: {exception.Message}"));
            }

            try
            {
                _profileStore.DeleteLauncherMetadata(_settings.LlamaRoot, selected.Profile);
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add(AppLanguageManager.Choose($"Profile 清理失败：{exception.Message}", $"Profile cleanup failed: {exception.Message}"));
            }

            var clearPending = _settings.PendingMode == ProviderMode.Local
                && string.Equals(_settings.PendingModelId, selected.Profile.Id, StringComparison.Ordinal);
            var clearApplied = _settings.SelectedMode == ProviderMode.Local
                && string.Equals(_settings.SelectedModelId, selected.Profile.Id, StringComparison.Ordinal);
            if (clearPending || clearApplied)
            {
                try
                {
                    await MutateSettingsAsync(
                        settings => settings with
                        {
                            PendingMode = clearPending ? null : settings.PendingMode,
                            PendingModelId = clearPending ? null : settings.PendingModelId,
                            PendingModelRelativePath = clearPending ? null : settings.PendingModelRelativePath,
                            PendingModelDisplayName = clearPending ? null : settings.PendingModelDisplayName,
                            SelectedModelId = clearApplied ? null : settings.SelectedModelId,
                        },
                        _lifetime.Token);
                    if (clearApplied)
                    {
                        TryDeleteActiveRouterPreset();
                    }
                }
                catch (Exception exception)
                {
                    cleanupWarnings.Add(AppLanguageManager.Choose($"当前模型状态清理失败：{exception.Message}", $"Current model state cleanup failed: {exception.Message}"));
                }
            }

            try
            {
                await ReloadProfilesAsync(_settings.LlamaRoot!, _lifetime.Token);
                await RefreshCacheAsync(_lifetime.Token);
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add(AppLanguageManager.Choose($"界面刷新失败：{exception.Message}", $"UI refresh failed: {exception.Message}"));
            }

            if (cleanupWarnings.Count == 0)
            {
                StatusText.Text = AppLanguageManager.Choose(
                    $"已由 llama.cpp 删除 {selected.Profile.DisplayName} 的缓存，并清理对应 Egg Launcher Profile/BAT。",
                    $"llama.cpp deleted the cache for {selected.Profile.DisplayName}; the corresponding Egg Launcher profile and BAT were cleaned up.");
            }
            else
            {
                var warning = AppLanguageManager.Choose(
                    $"llama.cpp 已删除 {selected.Profile.DisplayName}，但 Egg Launcher 元数据清理不完整：" + string.Join("；", cleanupWarnings),
                    $"llama.cpp deleted {selected.Profile.DisplayName}, but Egg Launcher metadata cleanup was incomplete: " + string.Join("; ", cleanupWarnings));
                StatusText.Text = warning;
                CacheStatusText.Text = warning;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CacheStatusText.Text = nativeModelDeleted
                ? AppLanguageManager.Choose($"llama.cpp 已删除模型，但 Egg Launcher 后续清理未完成：{exception.Message}", $"llama.cpp deleted the model, but subsequent Egg Launcher cleanup did not finish: {exception.Message}")
                : AppLanguageManager.Choose($"缓存删除失败，模型未被确认删除：{exception.Message}", $"Cache deletion failed; the model was not confirmed deleted: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<T> WithTemporaryManagementRouterAsync<T>(
        Func<LlamaModelManagementClient, Uri, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        var modelsRoot = Path.Combine(runtimeRoot, "models");
        var emptyModelsRoot = Path.Combine(runtimeRoot, "scripts", "cache-manager-empty");
        Directory.CreateDirectory(modelsRoot);
        Directory.CreateDirectory(emptyModelsRoot);
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var healthHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        healthHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        await using var manager = new LlamaRouterProcessManager(new RouterHealthClient(healthHttp));
        var router = await manager.StartAsync(
            new LlamaRouterStartRequest
            {
                ExecutablePath = Path.Combine(runtimeRoot, "llama-server.exe"),
                WorkingDirectory = runtimeRoot,
                LogDirectory = _paths.LogsDirectory,
                ModelCacheDirectory = modelsRoot,
                Options = new LlamaRouterOptions
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = ReserveAvailableLoopbackPort(),
                    ModelsDirectory = emptyModelsRoot,
                    MaximumLoadedModels = 1,
                    AutoloadModels = false,
                    ApiKey = apiKey,
                    DisableMultimodalProjectorAutoDownload = true,
                },
            },
            cancellationToken);
        using var managementHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        managementHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return await action(new LlamaModelManagementClient(managementHttp), router.BaseUri, cancellationToken);
    }

    private async Task<TrustedRouterEndpoint?> TryGetTrustedRouterEndpointAsync(
        CancellationToken cancellationToken)
    {
        if (_settings.SelectedMode != ProviderMode.Local
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || !File.Exists(_paths.RuntimeStateFile))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _paths.RuntimeStateFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<RuntimeState>(stream, cancellationToken: cancellationToken);
            if (state is null
                || state.AgentProtocolVersion != RuntimeState.CurrentAgentProtocolVersion
                || state.SelectedMode != ProviderMode.Local
                || state.Phase != RuntimePhase.Running
                || state.RouterProcessId is not int routerPid
                || state.AgentProcessId is not int agentPid
                || state.AgentStartedAtUtc is not DateTimeOffset agentStartedAtUtc
                || state.RouterStartedAtUtc is not DateTimeOffset routerStartedAtUtc
                || string.IsNullOrWhiteSpace(state.ProtectedRouterApiKey)
                || !IsFreshRuntimeState(state.UpdatedAtUtc)
                || !IsExpectedProcess(agentPid, FindAgentExecutable(), agentStartedAtUtc)
                || !IsExpectedProcess(
                    routerPid,
                    Path.Combine(_settings.LlamaRoot, "llama-server.exe"),
                    routerStartedAtUtc)
                || !Uri.TryCreate(state.RouterBaseUri, UriKind.Absolute, out var endpoint))
            {
                return null;
            }

            LlamaModelManagementClient.ValidateBaseUri(endpoint);
            var apiKey = CurrentUserSecretProtector.UnprotectString(state.ProtectedRouterApiKey);
            if (string.IsNullOrWhiteSpace(apiKey)
                || apiKey.Length > 256
                || apiKey.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                return null;
            }

            return new TrustedRouterEndpoint(endpoint, apiKey);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or Win32Exception
                or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool IsExpectedProcess(
        int processId,
        string? expectedPath,
        DateTimeOffset? expectedStartedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(expectedPath)) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsExpectedProcess(process, expectedPath, expectedStartedAtUtc);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsExpectedProcess(
        Process process,
        string expectedPath,
        DateTimeOffset? expectedStartedAtUtc)
    {
        if (process.HasExited
            || !string.Equals(
                Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (expectedStartedAtUtc is null)
        {
            return true;
        }

        var actualStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        return (actualStartedAtUtc - expectedStartedAtUtc.Value).Duration() <= TimeSpan.FromSeconds(2);
    }

    private static bool IsFreshRuntimeState(DateTimeOffset updatedAtUtc)
    {
        var age = DateTimeOffset.UtcNow - updatedAtUtc;
        return age >= TimeSpan.FromMinutes(-2) && age <= TimeSpan.FromSeconds(30);
    }

    private static bool IsLlamaServerFromRuntimeRunning(string? runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot)) return false;
        var expected = Path.GetFullPath(Path.Combine(runtimeRoot, "llama-server.exe"));
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited
                        && string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return false;
    }

    private string? GetModelDownloadBlockReason()
    {
        if (string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            return AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured.");
        }

        var expected = Path.GetFullPath(Path.Combine(_settings.LlamaRoot, "llama-server.exe"));
        var couldNotInspect = false;
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited
                        && string.Equals(
                            Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                            expected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return AppLanguageManager.Choose(
                            "当前 Runtime 已有 llama-server 正在运行。为避免两个原生 Router 争用模型缓存，本次不启动下载服务。",
                            "llama-server is already running in the current Runtime. The download service will not start, preventing two native Routers from competing for the model cache.");
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception)
                {
                    couldNotInspect = true;
                }
            }
        }

        return couldNotInspect
            ? AppLanguageManager.Choose(
                "Windows 拒绝读取某个 llama-server 的程序路径。为避免误与现有服务并发，本次不启动下载服务。",
                "Windows denied access to a llama-server executable path. The download service will not start, avoiding accidental concurrency with an existing service.")
            : null;
    }

    private static int ReserveAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void RenderRuntimeUnavailable(string diagnostic)
    {
        _runtimeIsValid = false;
        _runtimeDeviceOutput = string.Empty;
        RuntimeStatusText.Text = diagnostic;
        DeviceText.Text = string.Empty;
        _profiles = Array.Empty<ModelProfile>();
        _discoveredModels = Array.Empty<GgufModelCandidate>();
        ManagedModelsList.ItemsSource = Array.Empty<ModelListItem>();
        DiscoveredModelsList.ItemsSource = Array.Empty<ModelListItem>();
        ScanModelsButton.IsEnabled = false;
        SearchDownloadButton.IsEnabled = false;
        AddModelButton.IsEnabled = false;
        EditModelButton.IsEnabled = false;
        ChangeModelTypeButton.IsEnabled = false;
        AutoFitModelButton.IsEnabled = false;
        ModelDetailsButton.IsEnabled = false;
        DetectReasoningCapabilityButton.IsEnabled = false;
        LoadExternalMtpButton.IsEnabled = false;
        RemoveExternalMtpButton.IsEnabled = false;
        LoadExternalVisionButton.IsEnabled = false;
        RemoveExternalVisionButton.IsEnabled = false;
        ActivateLocalButton.IsEnabled = false;
        ToggleModelVisibilityButton.IsEnabled = false;
        DeleteLocalModelButton.IsEnabled = false;
        StatusText.Text = AppLanguageManager.Choose("Runtime 尚不可用；真实 ChatGPT Desktop 配置保持不变。", "The Runtime is not available. The actual ChatGPT Desktop configuration remains unchanged.");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OpenAiModeButton.IsEnabled = !busy;
        LocalModeButton.IsEnabled = !busy;
        SelectRuntimeButton.IsEnabled = !busy && !IsLocalRuntimeSessionActive();
        ScanModelsButton.IsEnabled = !busy && _runtimeIsValid;
        SearchDownloadButton.IsEnabled = !busy && _runtimeIsValid;
        AddModelButton.IsEnabled = !busy && DiscoveredModelsList.SelectedIndex >= 0;
        EditModelButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } selected
            && CanEditModelParameters(selected.Profile);
        ChangeModelTypeButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } typeSelected
            && CanEditModelParameters(typeSelected.Profile);
        AutoFitModelButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0 && CanUseFitTool();
        ModelDetailsButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0;
        DetectReasoningCapabilityButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } reasoningSelected
            && CanEditModelParameters(reasoningSelected.Profile);
        LoadExternalMtpButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } mtpSelected
            && CanEditModelParameters(mtpSelected.Profile);
        RemoveExternalMtpButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem
            {
                Profile: { MtpSource: MtpSourceKind.External, MtpDraftModelRelativePath: not null } mtpRemoveSelected,
            }
            && CanEditModelParameters(mtpRemoveSelected);
        LoadExternalVisionButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } visionSelected
            && CanEditModelParameters(visionSelected.Profile);
        RemoveExternalVisionButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem
            {
                Profile: { VisionSource: VisionSourceKind.External, VisionProjectorRelativePath: not null } visionRemoveSelected,
            }
            && CanEditModelParameters(visionRemoveSelected);
        ActivateLocalButton.IsEnabled = !busy
            && ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } activationSelected
            && !IsProfileUnavailable(activationSelected.Profile);
        ToggleModelVisibilityButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0;
        DeleteLocalModelButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0;
        RefreshMonitoringButton.IsEnabled = !busy;
        RefreshCacheButton.IsEnabled = !busy && _runtimeIsValid;
        DeleteCachedModelButton.IsEnabled = !busy && CachedModelsList.SelectedItem is CacheModelListItem;
        LoadCurrentModelButton.IsEnabled = !busy && LoadCurrentModelButton.Tag is true;
        UnloadCurrentModelButton.IsEnabled = !busy && UnloadCurrentModelButton.Tag is true;
        StartAgentAtLoginCheckBox.IsEnabled = !busy && _appExecutablePath is not null;
        OpenLogsButton.IsEnabled = !busy;
        RefreshHardwareButton.IsEnabled = !busy && !_monitorRefreshInProgress;
        ThemeToggleButton.IsEnabled = _themeUiReady && !busy && !_themeChangeInProgress;
        LanguageToggleButton.IsEnabled = _themeUiReady && !busy && !_languageChangeInProgress;
        UpdateManagedModelMutationButtons();
        RefreshEggUiState();
        if (!busy)
        {
            RestartStatusNotificationTimer();
        }
    }

    private void NestedList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is DependencyObject source)
        {
            var nestedScrollViewer = FindVisualChild<ScrollViewer>(source);
            var canScrollInside = nestedScrollViewer is not null
                && (e.Delta < 0
                    ? nestedScrollViewer.VerticalOffset < nestedScrollViewer.ScrollableHeight
                    : nestedScrollViewer.VerticalOffset > 0);
            if (canScrollInside)
            {
                return;
            }
        }

        e.Handled = true;
        var targetOffset = Math.Clamp(
            LocalModelsScrollViewer.VerticalOffset - e.Delta,
            0,
            LocalModelsScrollViewer.ScrollableHeight);
        LocalModelsScrollViewer.ScrollToVerticalOffset(targetOffset);
    }

    private void RuntimeNestedList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        NestedScrollWheelRouter.Route(sender as DependencyObject, RuntimeMonitorScrollViewer, e);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void RefreshAgentStartupRegistration()
    {
        _agentExecutablePath = FindAgentExecutable();
        if (_appExecutablePath is null)
        {
            StartAgentAtLoginCheckBox.IsChecked = false;
            StartAgentAtLoginCheckBox.IsEnabled = false;
            StartAgentAtLoginCheckBox.ToolTip = AppLanguageManager.Choose("无法确定 Egg Launcher 程序路径，不能配置开机启动。", "The Egg Launcher executable path could not be determined, so startup cannot be configured.");
            StatusText.Text = StartAgentAtLoginCheckBox.ToolTip.ToString();
            return;
        }

        try
        {
            var state = _agentStartupRegistration.Inspect(_appExecutablePath, "--startup");
            if (_settings.StartAgentAtLogin != state.IsEnabled)
            {
                _agentStartupRegistration.SetEnabled(
                    _settings.StartAgentAtLogin,
                    _appExecutablePath,
                    "--startup");
                state = _agentStartupRegistration.Inspect(_appExecutablePath, "--startup");
            }
            StartAgentAtLoginCheckBox.IsChecked = state.IsEnabled;
            StartAgentAtLoginCheckBox.IsEnabled = !_busy;
            StartAgentAtLoginCheckBox.ToolTip = state.IsEnabled
                ? AppLanguageManager.Choose("已启用；登录 Windows 后静默进入系统托盘。", "Enabled; starts silently in the system tray after Windows sign-in.")
                : state.HasRegistration
                    ? AppLanguageManager.Choose("检测到旧的启动路径；重新勾选可更新为当前位置。", "An old startup path was detected. Toggle the option to update it to the current location.")
                    : AppLanguageManager.Choose("尚未启用；不影响正常使用 Egg Launcher。", "Not enabled; normal Egg Launcher use is unaffected.");
        }
        catch (Exception exception)
        {
            StartAgentAtLoginCheckBox.IsChecked = false;
            StartAgentAtLoginCheckBox.IsEnabled = false;
            StartAgentAtLoginCheckBox.ToolTip = AppLanguageManager.Choose("无法读取当前用户的开机启动项。", "Unable to read the current user's startup registration.");
            StatusText.Text = AppLanguageManager.Choose($"无法读取当前用户的开机启动项：{exception.Message}", $"Unable to read the current user's startup registration: {exception.Message}");
        }
    }

    private async Task<bool> EnsureAgentRunningAsync(
        CancellationToken cancellationToken,
        string? expectedModelId = null)
    {
        if (IsAgentRunning())
        {
            if (await WaitForCompatibleAgentAsync(TimeSpan.FromSeconds(2), cancellationToken))
            {
                return true;
            }

            if (!await StopAgentForExitAsync())
            {
                throw new InvalidOperationException(
                    AppLanguageManager.Choose(
                        "检测到旧版或其他目录的 Launcher.Agent 正在运行，但用户取消了结束旧 Agent。"
                        + "为避免不同版本同时管理配置，本次操作已停止。",
                        "A Launcher.Agent from an older version or another directory is running, but stopping it was canceled. "
                        + "This operation has stopped to prevent different versions from managing configuration simultaneously."));
            }
        }

        var executablePath = FindAgentExecutable();
        if (executablePath is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--await-local-switch");
        if (!string.IsNullOrWhiteSpace(expectedModelId))
        {
            startInfo.ArgumentList.Add("--await-model");
            startInfo.ArgumentList.Add(expectedModelId);
        }
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        if (await WaitForCompatibleAgentAsync(TimeSpan.FromSeconds(4), cancellationToken))
        {
            return true;
        }

        process.Refresh();
        return !process.HasExited && IsAgentRunning();
    }

    private async Task<bool> WaitForCompatibleAgentAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var expectedAgentPath = FindAgentExecutable();
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processIds = GetAgentProcessIds();
            if (processIds.Count == 0)
            {
                return false;
            }

            try
            {
                await using var stream = new FileStream(
                    _paths.RuntimeStateFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var state = await JsonSerializer.DeserializeAsync<RuntimeState>(
                    stream,
                    cancellationToken: cancellationToken);
                if (state?.AgentProtocolVersion == RuntimeState.CurrentAgentProtocolVersion
                    && state.AgentProcessId is int stateProcessId
                    && state.AgentStartedAtUtc is DateTimeOffset stateStartedAtUtc
                    && processIds.Contains(stateProcessId)
                    && string.Equals(
                        state.AgentExecutablePath,
                        expectedAgentPath,
                        StringComparison.OrdinalIgnoreCase)
                    && IsFreshRuntimeState(state.UpdatedAtUtc)
                    && IsExpectedProcess(stateProcessId, expectedAgentPath, stateStartedAtUtc))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Agent may be atomically replacing the diagnostic state; retry briefly.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    private async Task<bool> WaitForLocalRouterAsync(
        string expectedModelId,
        CancellationToken cancellationToken)
    {
        using var healthHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var client = new RouterHealthClient(healthHttpClient);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseUri = await TryGetTrustedPublicProxyBaseUriAsync(
                expectedModelId,
                cancellationToken);
            if (baseUri is not null)
            {
                var health = await client.ProbeAsync(baseUri, cancellationToken);
                if (health.IsHealthy
                    && health.ModelIds.Contains(expectedModelId, StringComparer.Ordinal))
                {
                    var responsesApi = await client.ProbeResponsesRouteAsync(baseUri, cancellationToken);
                    if (responsesApi.IsAvailable)
                    {
                        return true;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return false;
    }

    private async Task<ModelProfile> InvalidateStaleReasoningCapabilityAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var signature = ComputeReasoningCapabilitySignature(profile, runtimeRoot);
        if (string.Equals(profile.ReasoningCapabilitySignature, signature, StringComparison.Ordinal))
        {
            return profile;
        }

        var reset = profile with
        {
            ReasoningCapabilityStatus = ReasoningCapabilityStatus.Unknown,
            SupportedReasoningLevels = Array.Empty<string>(),
            DefaultReasoningLevel = null,
            ExposeReasoningEffortInChatGpt = false,
            ReasoningCapabilitySignature = signature,
            ReasoningCapabilityCheckedAtUtc = null,
        };
        await _profileStore.SaveAsync(runtimeRoot, reset, cancellationToken);
        return reset;
    }

    private async Task<ModelProfile> InvalidateStaleMtpValidationAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (profile.MtpCapabilityStatus != MtpCapabilityStatus.Verified)
        {
            return profile;
        }

        if (MtpValidationFingerprint.IsCurrent(profile, runtimeRoot))
        {
            return profile;
        }

        var reset = profile with
        {
            MtpCapabilityStatus = DetectUnverifiedMtpStatus(profile, runtimeRoot),
            MtpEnabled = false,
            MtpValidationSignature = null,
            MtpValidatedAtUtc = null,
        };
        await _profileStore.SaveAsync(runtimeRoot, reset, cancellationToken);
        return reset;
    }

    private async Task<ModelProfile> CaptureMtpValidationSuccessAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (!profile.MtpEnabled)
        {
            return profile;
        }

        var signature = MtpValidationFingerprint.Compute(profile, runtimeRoot);
        if (profile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
            && string.Equals(profile.MtpValidationSignature, signature, StringComparison.Ordinal)
            && profile.MtpValidatedAtUtc is not null)
        {
            return profile;
        }

        // Reaching this point means the native Router accepted the generated MTP preset,
        // loaded the target model and exposed it through the health/models endpoints.
        var verified = profile with
        {
            MtpCapabilityStatus = MtpCapabilityStatus.Verified,
            MtpValidationSignature = signature,
            MtpValidatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _profileStore.SaveAsync(runtimeRoot, verified, cancellationToken);
        return verified;
    }

    private async Task<ModelProfile> InvalidateStaleVisionValidationAsync(
        ModelProfile profile,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (profile.VisionCapabilityStatus != VisionCapabilityStatus.Verified
            || VisionValidationFingerprint.IsCurrent(profile, runtimeRoot))
        {
            return profile;
        }

        var reset = profile with
        {
            VisionEnabled = false,
            VisionCapabilityStatus = profile.VisionSource == VisionSourceKind.External
                && !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath)
                ? VisionCapabilityStatus.ExternalConfigured
                : DetectBuiltInVision(profile, runtimeRoot)
                    ? VisionCapabilityStatus.BuiltInCandidate
                    : VisionCapabilityStatus.Unknown,
            VisionValidationSignature = null,
            VisionValidatedAtUtc = null,
        };
        await _profileStore.SaveAsync(runtimeRoot, reset, cancellationToken);
        return reset;
    }

    private async Task<IReadOnlyList<ModelProfile>> ReconcileVisionAvailabilityAsync(
        string runtimeRoot,
        IReadOnlyList<ModelProfile> profiles,
        CancellationToken cancellationToken)
    {
        List<ModelProfile>? reconciled = null;
        for (var index = 0; index < profiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = profiles[index];
            if (IsProfileUnavailable(profile)
                || profile.VisionCapabilityStatus != VisionCapabilityStatus.Verified
                || VisionValidationFingerprint.IsCurrent(profile, runtimeRoot))
            {
                continue;
            }

            var invalidated = await InvalidateStaleVisionValidationAsync(profile, runtimeRoot, cancellationToken);
            reconciled ??= profiles.ToList();
            reconciled[index] = invalidated;
        }

        return reconciled ?? profiles;
    }

    private static MtpCapabilityStatus DetectUnverifiedMtpStatus(
        ModelProfile profile,
        string runtimeRoot)
    {
        if (profile.MtpSource == MtpSourceKind.External
            && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath))
        {
            return MtpCapabilityStatus.ExternalConfigured;
        }

        try
        {
            var modelPath = Path.GetFullPath(Path.Combine(runtimeRoot, profile.ModelRelativePath));
            return File.Exists(modelPath)
                   && GgufContextMetadataReader.ReadMetadata(modelPath).HasEmbeddedMtp
                ? MtpCapabilityStatus.EmbeddedCandidate
                : MtpCapabilityStatus.Unknown;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException
                                          or OverflowException)
        {
            return MtpCapabilityStatus.Unknown;
        }
    }

    private async Task<IReadOnlyList<ModelProfile>> ReconcileMtpAvailabilityAsync(
        string runtimeRoot,
        IReadOnlyList<ModelProfile> profiles,
        CancellationToken cancellationToken)
    {
        List<ModelProfile>? reconciled = null;
        for (var index = 0; index < profiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = profiles[index];
            if (IsProfileUnavailable(profile)
                || profile.MtpCapabilityStatus != MtpCapabilityStatus.Verified
                || MtpValidationFingerprint.IsCurrent(profile, runtimeRoot))
            {
                continue;
            }

            var invalidated = profile with
            {
                MtpEnabled = false,
                MtpCapabilityStatus = DetectUnverifiedMtpStatus(profile, runtimeRoot),
                MtpValidationSignature = null,
                MtpValidatedAtUtc = null,
            };
            await _profileStore.SaveAsync(runtimeRoot, invalidated, cancellationToken);
            reconciled ??= profiles.ToList();
            reconciled[index] = invalidated;
        }

        return reconciled ?? profiles;
    }

    private static string ComputeReasoningCapabilitySignature(ModelProfile profile, string runtimeRoot)
    {
        static string FileSignature(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : $"{Path.GetFullPath(path)}|missing";
        }

        var modelPath = Path.Combine(runtimeRoot, profile.ModelRelativePath);
        var templateSignature = string.IsNullOrWhiteSpace(profile.ChatTemplateRelativePath)
            ? "embedded-template"
            : FileSignature(Path.Combine(runtimeRoot, profile.ChatTemplateRelativePath));
        var runtimeSignature = FileSignature(Path.Combine(runtimeRoot, "llama-server.exe"));
        var material = $"{FileSignature(modelPath)}|{profile.Jinja}|{templateSignature}|{runtimeSignature}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private async Task<Uri?> TryGetTrustedPublicProxyBaseUriAsync(
        string expectedModelId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.RuntimeStateFile))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _paths.RuntimeStateFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<RuntimeState>(
                stream,
                cancellationToken: cancellationToken);
            var expectedAgentPath = FindAgentExecutable();
            if (state?.AgentProtocolVersion != RuntimeState.CurrentAgentProtocolVersion
                || state.SelectedMode != ProviderMode.Local
                || state.Phase != RuntimePhase.Running
                || !string.Equals(state.SelectedModelId, expectedModelId, StringComparison.Ordinal)
                || state.AgentProcessId is not int agentProcessId
                || state.AgentStartedAtUtc is not DateTimeOffset agentStartedAtUtc
                || string.IsNullOrWhiteSpace(expectedAgentPath)
                || !string.Equals(state.AgentExecutablePath, expectedAgentPath, StringComparison.OrdinalIgnoreCase)
                || !IsFreshRuntimeState(state.UpdatedAtUtc)
                || !IsExpectedProcess(agentProcessId, expectedAgentPath, agentStartedAtUtc)
                || !Uri.TryCreate(state.PublicProxyBaseUri, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme != Uri.UriSchemeHttp
                || !IPAddress.TryParse(baseUri.Host, out var host)
                || !IPAddress.IsLoopback(host)
                || baseUri.Port is < 1 or > 65535)
            {
                return null;
            }

            return baseUri;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            return null;
        }
    }

    private static string FormatLaunchResult(ChatGptClientLaunchResult result, string successPrefix)
    {
        if (!result.Succeeded)
        {
            return AppLanguageManager.Choose(
                $"{successPrefix}，但 ChatGPT Desktop 启动失败：{result.Diagnostic}",
                $"{successPrefix}, but ChatGPT Desktop failed to start: {result.Diagnostic}");
        }

        return result.AlreadyRunning
            ? AppLanguageManager.Choose($"{successPrefix}；ChatGPT Desktop 已在运行。", $"{successPrefix}; ChatGPT Desktop is already running.")
            : AppLanguageManager.Choose($"{successPrefix}；已请求 Windows 启动 ChatGPT Desktop。", $"{successPrefix}; Windows was asked to start ChatGPT Desktop.");
    }

    private async Task<ChatGptClientLaunchResult> LaunchClientAndRefreshAsync(
        CancellationToken cancellationToken)
    {
        if (_settings.SelectedMode == ProviderMode.OpenAI
            && !_clientDetector.IsRunning()
            && !string.IsNullOrWhiteSpace(_codexHome))
        {
            await new ChatGptConfigTransactionService(_clientDetector)
                .EnsureOfficialCompatibilityAsync(
                    _paths.RecoveryFile,
                    Path.Combine(_codexHome, "config.toml"),
                    cancellationToken);
        }

        var result = new ChatGptClientLauncher(
            _clientDetector,
            new ChatGptClientInstallationLocator()).Launch();
        if (result.Succeeded && !result.AlreadyRunning)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && !GetClientRunningSnapshot(force: true))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }

        InvalidateClientRunningSnapshot();
        await RefreshEnvironmentAsync(cancellationToken);
        return result;
    }

    private static bool IsAgentRunning()
        => GetAgentProcessIds().Count > 0;

    private static IReadOnlySet<int> GetAgentProcessIds()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        var processes = Process.GetProcessesByName("Launcher.Agent");
        try
        {
            var processIds = new HashSet<int>();
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId == currentSessionId)
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while the list was being inspected.
                }
                catch (Win32Exception)
                {
                    // A process in another security context is not owned by this launcher session.
                }
            }

            return processIds;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private async Task<AgentProcessIdentity?> TryGetVerifiedAgentIdentityAsync(
        IReadOnlySet<int> candidateProcessIds,
        CancellationToken cancellationToken)
    {
        if (candidateProcessIds.Count == 0 || !File.Exists(_paths.RuntimeStateFile))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _paths.RuntimeStateFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<RuntimeState>(
                stream,
                cancellationToken: cancellationToken);
            var expectedPath = FindAgentExecutable();
            if (state?.AgentProtocolVersion != RuntimeState.CurrentAgentProtocolVersion
                || state.AgentProcessId is not int processId
                || state.AgentStartedAtUtc is not DateTimeOffset startedAtUtc
                || string.IsNullOrWhiteSpace(expectedPath)
                || !candidateProcessIds.Contains(processId)
                || !string.Equals(state.AgentExecutablePath, expectedPath, StringComparison.OrdinalIgnoreCase)
                || !IsFreshRuntimeState(state.UpdatedAtUtc)
                || !IsExpectedProcess(processId, expectedPath, startedAtUtc))
            {
                return null;
            }

            return new AgentProcessIdentity(processId, expectedPath, startedAtUtc);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            return null;
        }
    }

    private static string? FindAgentExecutable()
    {
        var packagedPath = Path.Combine(AppContext.BaseDirectory, "Launcher.Agent.exe");
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var developmentPath = Path.Combine(
                sourceRoot,
                "Launcher.Agent",
                "bin",
                configuration,
                "net10.0-windows10.0.19041.0",
                "Launcher.Agent.exe");
            if (File.Exists(developmentPath))
            {
                return developmentPath;
            }
        }

        return null;
    }

    private static string CompactDeviceOutput(string output)
    {
        var lines = output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("register_backend", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        return lines.Length == 0
            ? AppLanguageManager.Choose("设备：未返回可显示的信息。", "Devices: no displayable information returned.")
            : AppLanguageManager.Choose("设备：", "Devices: ") + string.Join(" · ", lines);
    }

    private static string FormatProfileSummary(ModelProfile profile)
    {
        var source = profile.SourceKind == ModelSourceKind.LlamaCache
            ? AppLanguageManager.Choose(
                $"llama 下载缓存 · {profile.RemoteRepositoryId ?? "仓库未知"}",
                $"llama download cache · {profile.RemoteRepositoryId ?? "unknown repository"}")
            : AppLanguageManager.Choose("本地文件", "local file");
        var mtp = profile.MtpSource == MtpSourceKind.External
            ? profile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
                ? AppLanguageManager.Choose("外置 MTP 已验证", "external MTP verified")
                : AppLanguageManager.Choose("外置 MTP 未验证", "external MTP unverified")
            : profile.MtpCapabilityStatus is MtpCapabilityStatus.EmbeddedCandidate or MtpCapabilityStatus.Verified
                ? AppLanguageManager.Choose("内置 MTP", "embedded MTP")
                : AppLanguageManager.Choose("无可用 MTP", "no available MTP");
        var vision = profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
            ? profile.VisionEnabled
                ? AppLanguageManager.Choose("视觉已识别并启用", "vision recognized and enabled")
                : AppLanguageManager.Choose("视觉已识别（未启用）", "vision recognized (disabled)")
            : profile.VisionSource == VisionSourceKind.External
              && !string.IsNullOrWhiteSpace(profile.VisionProjectorRelativePath)
                ? AppLanguageManager.Choose("外置视觉关联待重新识别", "external vision association needs recognition")
                : AppLanguageManager.Choose("无已识别视觉能力", "no recognized vision capability");
        var reasoning = profile.ReasoningCapabilityStatus switch
        {
            ReasoningCapabilityStatus.Verified => AppLanguageManager.Choose(
                $"思考档位 {string.Join("/", profile.SupportedReasoningLevels)}",
                $"reasoning levels {string.Join("/", profile.SupportedReasoningLevels)}"),
            ReasoningCapabilityStatus.Unsupported => AppLanguageManager.Choose(
                "不支持思考档位",
                "reasoning levels unsupported"),
            ReasoningCapabilityStatus.SupportedLevelsUnknown => AppLanguageManager.Choose(
                "思考档位不完整",
                "reasoning levels incomplete"),
            _ => AppLanguageManager.Choose("思考档位未检测", "reasoning levels not detected"),
        };
        return AppLanguageManager.IsEnglish
            ? $"Type: {FormatModelType(profile.ModelType)} · {source} · {vision} · {mtp} · {reasoning} · {FormatSize(profile.KnownSizeBytes ?? 0)} · {profile.KnownShardCount?.ToString() ?? "?"} GGUF shards · Alias {profile.Alias}"
            : $"类型：{FormatModelType(profile.ModelType)} · {source} · {vision} · {mtp} · {reasoning} · {FormatSize(profile.KnownSizeBytes ?? 0)} · {profile.KnownShardCount?.ToString() ?? "?"} 个 GGUF 分片 · Alias {profile.Alias}";
    }

    private string FormatProfileDetails(ModelProfile profile)
    {
        var path = string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            ? profile.ModelRelativePath
            : Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath));
        var currentSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        var defaults = profile.DefaultParameters is null
            ? AppLanguageManager.Choose("未设置", "Not set")
            : AppLanguageManager.Choose(
                $"Context {profile.DefaultParameters.ContextSize:N0} / 压缩安全余量 {profile.DefaultParameters.CompactionSafetyReserve:N0} / GPU Layers {profile.DefaultParameters.GpuLayers}",
                $"Context {profile.DefaultParameters.ContextSize:N0} / Compaction Reserve {profile.DefaultParameters.CompactionSafetyReserve:N0} / GPU Layers {profile.DefaultParameters.GpuLayers}");
        return AppLanguageManager.IsEnglish
            ? $"Source: {(profile.SourceKind == ModelSourceKind.LlamaCache ? "llama download cache" : "local file")}\nPath: {path}\nFile Status: {(File.Exists(path) ? "Exists" : "Missing")}\nKnown Total Size: {FormatSize(profile.KnownSizeBytes ?? currentSize)}\nKnown Shards: {profile.KnownShardCount?.ToString() ?? "Unknown"}\nRemote ID: {profile.RemoteModelId ?? "None"}\nCurrent Parameters: Context {profile.ContextSize:N0} / Compaction Reserve {profile.CompactionSafetyReserve:N0} / Codex Compaction Threshold {profile.AutoCompactTokenLimit:N0} / GPU Layers {profile.GpuLayers} / KV {profile.CacheTypeK}/{profile.CacheTypeV} / Parallel {profile.Parallel}\nModel Defaults: {defaults}"
            : $"来源：{(profile.SourceKind == ModelSourceKind.LlamaCache ? "llama 下载缓存" : "本地文件")}\n路径：{path}\n文件状态：{(File.Exists(path) ? "存在" : "不存在")}\n已知总大小：{FormatSize(profile.KnownSizeBytes ?? currentSize)}\n已知分片：{profile.KnownShardCount?.ToString() ?? "未知"}\n远程 ID：{profile.RemoteModelId ?? "无"}\n当前参数：Context {profile.ContextSize:N0} / 压缩安全余量 {profile.CompactionSafetyReserve:N0} / Codex 压缩线 {profile.AutoCompactTokenLimit:N0} / GPU Layers {profile.GpuLayers} / KV {profile.CacheTypeK}/{profile.CacheTypeV} / Parallel {profile.Parallel}\n该模型专用默认：{defaults}";
    }

    private static string FormatNativeModelDetails(LlamaModelRuntimeInfo model) => AppLanguageManager.IsEnglish
        ? $"Status: {TranslateModelStatus(model.Status)}\nSource: {model.Source} · Removable Cache: {(model.CanRemove ? "Yes" : "No")}\nParameters: {FormatCount(model.ParameterCount)}\nTraining Context: {model.TrainingContextSize?.ToString("N0") ?? "Not returned"}\nInput Modalities: {(model.InputModalities.Count > 0 ? string.Join(", ", model.InputModalities) : "Not returned")}"
        : $"状态：{TranslateModelStatus(model.Status)}\n来源：{model.Source} · 可删除缓存：{(model.CanRemove ? "是" : "否")}\n参数量：{FormatCount(model.ParameterCount)}\n训练上下文：{model.TrainingContextSize?.ToString("N0") ?? "未返回"}\n输入模态：{(model.InputModalities.Count > 0 ? string.Join("、", model.InputModalities) : "未返回")}";

    private static string FormatHardwareInventory(HardwareInventory? inventory, string llamaDevices)
    {
        if (inventory is null) return AppLanguageManager.Choose("硬件清单尚未读取。", "Hardware inventory has not been read.");
        if (AppLanguageManager.IsEnglish)
        {
            var englishMemory = inventory.MemoryModules.Count == 0
                ? $"Memory: {FormatSize(inventory.InstalledMemoryBytes)} (module brand/generation not provided by Windows)"
                : "Memory: " + string.Join("; ", inventory.MemoryModules.Select(module =>
                    $"{module.Manufacturer} {module.PartNumber} {FormatSize(module.CapacityBytes)} {module.MemoryType}"
                    + (module.SpeedMHz is > 0 ? $" {module.SpeedMHz:N0} MHz" : string.Empty)));
            var englishGpu = inventory.GraphicsAdapters.Count == 0
                ? "Windows GPUs: not returned"
                : "Windows GPUs: " + string.Join("; ", inventory.GraphicsAdapters.Select(adapter =>
                    $"{adapter.Manufacturer} {adapter.Name}"
                    + (adapter.DedicatedBytes is > 0 ? $" (Windows reports {FormatSize(adapter.DedicatedBytes.Value)})" : string.Empty)));
            var englishLlama = string.IsNullOrWhiteSpace(llamaDevices)
                ? "llama compute devices: not returned"
                : CompactDeviceOutput(llamaDevices).Replace("Devices: ", "llama compute devices: ", StringComparison.Ordinal);
            return $"CPU: {inventory.CpuName} · {inventory.LogicalProcessorCount} logical processors\n" +
                $"{englishMemory}\n{englishGpu}\n{englishLlama}" +
                (string.IsNullOrWhiteSpace(inventory.Diagnostic) ? string.Empty : $"\nDetails: {inventory.Diagnostic}");
        }

        var memory = inventory.MemoryModules.Count == 0
            ? $"内存：{FormatSize(inventory.InstalledMemoryBytes)}（模块品牌/代数未由 Windows 提供）"
            : "内存：" + string.Join("；", inventory.MemoryModules.Select(module =>
                $"{module.Manufacturer} {module.PartNumber} {FormatSize(module.CapacityBytes)} {module.MemoryType}"
                + (module.SpeedMHz is > 0 ? $" {module.SpeedMHz:N0} MHz" : string.Empty)));
        var gpu = inventory.GraphicsAdapters.Count == 0
            ? "Windows 显卡：未返回"
            : "Windows 显卡：" + string.Join("；", inventory.GraphicsAdapters.Select(adapter =>
                $"{adapter.Manufacturer} {adapter.Name}"
                + (adapter.DedicatedBytes is > 0 ? $"（Windows 报告 {FormatSize(adapter.DedicatedBytes.Value)}）" : string.Empty)));
        var llama = string.IsNullOrWhiteSpace(llamaDevices)
            ? "llama 计算设备：未返回"
            : CompactDeviceOutput(llamaDevices).Replace("设备：", "llama 计算设备：", StringComparison.Ordinal);
        return
            $"CPU：{inventory.CpuName} · {inventory.LogicalProcessorCount} 逻辑处理器\n" +
            $"{memory}\n{gpu}\n{llama}" +
            (string.IsNullOrWhiteSpace(inventory.Diagnostic) ? string.Empty : $"\n说明：{inventory.Diagnostic}");
    }

    private static string FormatHomeHardwareInventory(HardwareInventory? inventory, string llamaDeviceOutput)
    {
        if (inventory is null)
        {
            return AppLanguageManager.Choose("硬件信息暂不可用", "Hardware information is unavailable");
        }

        var memory = inventory.MemoryModules.FirstOrDefault();
        var memoryText = memory is null
            ? FormatSize(inventory.InstalledMemoryBytes)
            : $"{memory.Manufacturer} {memory.MemoryType} {FormatSize(inventory.InstalledMemoryBytes)}"
                + (memory.SpeedMHz is > 0 ? $" {memory.SpeedMHz:N0} MHz" : string.Empty);
        var gpu = SelectPrimaryGraphicsAdapter(inventory.GraphicsAdapters);
        var totalVram = ResolveDisplayVramBytes(inventory, llamaDeviceOutput);
        return AppLanguageManager.IsEnglish
            ? $"CPU  {inventory.CpuName}\nMemory  {memoryText}\nGPU  {gpu?.Name ?? "Not detected"}\nVRAM  {(totalVram is > 0 ? FormatSize(totalVram.Value) : "Not returned")}"
            : $"CPU　{inventory.CpuName}\n内存　{memoryText}\nGPU　{gpu?.Name ?? "未检测到"}\n显存　{(totalVram is > 0 ? FormatSize(totalVram.Value) : "未返回")}";
    }

    private void UpdateSidebarPerformance(WindowsPerformanceSnapshot value)
    {
        UpdateSidebarPerformanceRing(SidebarCpuRing, SidebarCpuText, value.CpuUsagePercent);
        UpdateSidebarPerformanceRing(
            SidebarMemoryRing,
            SidebarMemoryText,
            value.TotalMemoryBytes > 0 ? 100d * value.UsedMemoryBytes / value.TotalMemoryBytes : null);
        UpdateSidebarPerformanceRing(SidebarGpuRing, SidebarGpuText, value.GpuUsagePercent);
        var totalVram = ResolveDisplayVramBytes(_hardwareInventory, _runtimeDeviceOutput) ?? 0;
        UpdateSidebarPerformanceRing(
            SidebarVramRing,
            SidebarVramText,
            totalVram is > 0 && value.DedicatedGpuMemoryBytes is not null
                ? 100d * value.DedicatedGpuMemoryBytes.Value / totalVram
                : null);
    }

    private static void UpdateSidebarPerformanceRing(
        CircularProgressRing ring,
        TextBlock text,
        double? value)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            ring.Value = double.NaN;
            text.Text = "—";
            return;
        }

        var percentage = Math.Clamp(value.Value, 0d, 100d);
        ring.Value = percentage;
        text.Text = $"{percentage:0}%";
    }

    private static GraphicsAdapterInfo? SelectPrimaryGraphicsAdapter(
        IReadOnlyList<GraphicsAdapterInfo> adapters) =>
        SelectDisplayGraphicsAdapters(adapters)
            .OrderByDescending(adapter => adapter.DedicatedBytes ?? 0)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static long? ResolveDisplayVramBytes(HardwareInventory? inventory, string llamaDeviceOutput)
    {
        if (inventory is null)
        {
            return null;
        }

        var primary = SelectPrimaryGraphicsAdapter(inventory.GraphicsAdapters);
        var llamaDevice = LlamaDeviceMemoryParser.FindBestMatch(llamaDeviceOutput, primary?.Name);
        if (llamaDevice?.TotalBytes is > 0)
        {
            return llamaDevice.TotalBytes;
        }

        return primary?.DedicatedBytes is > 0 ? primary.DedicatedBytes : null;
    }

    private static IReadOnlyList<GraphicsAdapterInfo> SelectDisplayGraphicsAdapters(
        IReadOnlyList<GraphicsAdapterInfo> adapters)
    {
        var physical = adapters.Where(adapter => !IsVirtualGraphicsAdapter(adapter)).ToArray();
        return physical.Length > 0 ? physical : adapters;
    }

    private static bool IsVirtualGraphicsAdapter(GraphicsAdapterInfo adapter)
    {
        var identity = $"{adapter.Name} {adapter.Manufacturer}";
        return identity.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("remote", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("GameViewer", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Parsec", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("IddSample", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Render Only", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSidebarTelemetry(LlamaTelemetrySnapshot value)
    {
        SidebarSpeedText.Text = value.PredictedTokensPerSecond is null
            ? "–"
            : $"{value.PredictedTokensPerSecond:0.0} token/s";
        SidebarContextText.Text = value.ContextCapacityTokens is > 0 && value.ContextUsedTokens is not null
            ? $"{FormatContextTokens(value.ContextUsedTokens.Value)}/{FormatContextTokens(value.ContextCapacityTokens.Value)}"
            : "–";
    }

    private void ClearSidebarTelemetry()
    {
        _telemetryClient?.ResetSamples();
        SidebarSpeedText.Text = "–";
        SidebarContextText.Text = "–";
        _telemetryRefreshTimer.Interval = TimeSpan.FromSeconds(2);
    }

    private static string FormatContextTokens(int tokens) =>
        $"{tokens / 1024d:0.#}K";

    private static string FormatPerformance(WindowsPerformanceSnapshot value)
    {
        if (AppLanguageManager.IsEnglish)
        {
            var englishGpu = value.GpuUsagePercent is null
                ? value.GpuDiagnostic ?? "GPU usage is unavailable."
                : $"GPU {value.GpuUsagePercent:0.0}% · Compute/CUDA {FormatPercent(value.ComputeUsagePercent)} · " +
                  $"llama GPU {FormatPercent(value.LlamaGpuUsagePercent)} · VRAM used {FormatSize(value.DedicatedGpuMemoryBytes ?? 0)}" +
                  (value.LlamaDedicatedGpuMemoryBytes is > 0
                      ? $" (llama {FormatSize(value.LlamaDedicatedGpuMemoryBytes.Value)})"
                      : string.Empty);
            return $"System: CPU {FormatPercent(value.CpuUsagePercent)} · Memory {FormatSize(value.UsedMemoryBytes)} / {FormatSize(value.TotalMemoryBytes)}\n" +
                $"llama in current Runtime: {value.LlamaProcessCount} processes · CPU {FormatPercent(value.LlamaCpuUsagePercent)} · RAM {FormatSize(value.LlamaWorkingSetBytes)}\n" +
                englishGpu;
        }

        var gpu = value.GpuUsagePercent is null
            ? value.GpuDiagnostic ?? "GPU 使用率不可用。"
            : $"GPU {value.GpuUsagePercent:0.0}% · Compute/CUDA {FormatPercent(value.ComputeUsagePercent)} · " +
              $"llama GPU {FormatPercent(value.LlamaGpuUsagePercent)} · 显存已用 {FormatSize(value.DedicatedGpuMemoryBytes ?? 0)}" +
              (value.LlamaDedicatedGpuMemoryBytes is > 0
                  ? $"（llama {FormatSize(value.LlamaDedicatedGpuMemoryBytes.Value)}）"
                  : string.Empty);
        return
            $"整机：CPU {FormatPercent(value.CpuUsagePercent)} · 内存 {FormatSize(value.UsedMemoryBytes)} / {FormatSize(value.TotalMemoryBytes)}\n" +
            $"当前 Runtime 的 llama：{value.LlamaProcessCount} 个进程 · CPU {FormatPercent(value.LlamaCpuUsagePercent)} · RAM {FormatSize(value.LlamaWorkingSetBytes)}\n" +
            gpu;
    }

    private static string FormatTelemetry(LlamaTelemetrySnapshot value)
    {
        if (!string.IsNullOrWhiteSpace(value.Diagnostic)) return value.Diagnostic;
        if (AppLanguageManager.IsEnglish)
        {
            var englishContext = value.ContextCapacityTokens is > 0 && value.ContextUsedTokens is not null
                ? $"Actual slot context {value.ContextUsedTokens:N0} / {value.ContextCapacityTokens:N0} " +
                  $"({100d * value.ContextUsedTokens.Value / value.ContextCapacityTokens.Value:0.0}%)"
                : "Actual slot context: llama did not return used tokens";
            return $"{englishContext} · Processing slots {value.ProcessingSlots}/{value.SlotCount}\n" +
                $"Cumulative prompt {FormatCount(value.PromptTokensTotal)} · Output {FormatCount(value.PredictedTokensTotal)} · " +
                $"Prompt {FormatRate(value.PromptTokensPerSecond)} · Output {FormatRate(value.PredictedTokensPerSecond)}\n" +
                $"KV usage: {(value.KvCacheUsageRatio is null ? "Not returned" : $"{value.KvCacheUsageRatio.Value * 100:0.0}%")} · " +
                $"Requests: processing {value.RequestsProcessing?.ToString() ?? "?"} / queued {value.RequestsDeferred?.ToString() ?? "?"}\n" +
                "Note: Slot tokens include system prompts, project context, and tool messages; they are not the same as visible conversation words.";
        }

        var context = value.ContextCapacityTokens is > 0 && value.ContextUsedTokens is not null
            ? $"实际 Slot 上下文 {value.ContextUsedTokens:N0} / {value.ContextCapacityTokens:N0} " +
              $"({100d * value.ContextUsedTokens.Value / value.ContextCapacityTokens.Value:0.0}%)"
            : "实际 Slot 上下文：llama 未返回已用 token";
        return
            $"{context} · 处理中 Slot {value.ProcessingSlots}/{value.SlotCount}\n" +
            $"累计 Prompt {FormatCount(value.PromptTokensTotal)} · 输出 {FormatCount(value.PredictedTokensTotal)} · " +
            $"Prompt {FormatRate(value.PromptTokensPerSecond)} · 输出 {FormatRate(value.PredictedTokensPerSecond)}\n" +
            $"KV 占用：{(value.KvCacheUsageRatio is null ? "未返回" : $"{value.KvCacheUsageRatio.Value * 100:0.0}%")} · " +
            $"请求：处理中 {value.RequestsProcessing?.ToString() ?? "?"} / 排队 {value.RequestsDeferred?.ToString() ?? "?"}\n" +
            "说明：Slot token 包含系统提示、项目上下文与工具消息，不等同于窗口中可见的对话字数。";
    }

    private static string TranslateModelStatus(string value) => value.ToLowerInvariant() switch
    {
        "loaded" => AppLanguageManager.Choose("已加载", "Loaded"),
        "loading" => AppLanguageManager.Choose("加载中", "Loading"),
        "sleeping" => AppLanguageManager.Choose("空闲休眠", "Sleeping"),
        "unloaded" => AppLanguageManager.Choose("已释放", "Unloaded"),
        "downloading" => AppLanguageManager.Choose("下载中", "Downloading"),
        _ => value,
    };

    private static string FormatPercent(double? value) => value is null ? AppLanguageManager.Choose("不可用", "Unavailable") : $"{value:0.0}%";

    private static string FormatRate(double? value) => value is null ? AppLanguageManager.Choose("速率未返回", "Rate not returned") : $"{value:0.0} token/s";

    private static string FormatCount(long? value) => value is null ? AppLanguageManager.Choose("未返回", "Not returned") : value.Value.ToString("N0");

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return AppLanguageManager.Choose("大小未知", "Size unknown");
        }

        const double gibibyte = 1024d * 1024d * 1024d;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.00} GiB"
            : $"{bytes / (1024d * 1024d):0.00} MiB";
    }

    private static string FormatModelType(ModelType modelType) => modelType switch
    {
        ModelType.Dense => "Dense",
        ModelType.MoE => "MoE",
        _ => AppLanguageManager.Choose("未指定", "Unspecified"),
    };

    private sealed record ModelListItem(
        string Summary,
        GgufModelCandidate? Candidate,
        ModelProfile? Profile,
        bool IsUnavailable)
    {
        public static ModelListItem FromCandidate(GgufModelCandidate candidate) => new(
            $"{candidate.DisplayName} · {FormatSize(candidate.TotalSizeBytes)}" +
            (candidate.IsSharded ? AppLanguageManager.Choose($" · {candidate.ShardCount} 分片", $" · {candidate.ShardCount} shards") : string.Empty),
            candidate,
            null,
            false);

        public static ModelListItem FromProfile(ModelProfile profile, bool isUnavailable = false) => new(
            $"{(isUnavailable || profile.ContextSize is > 0 and < ModelProfile.RecommendedMinimumCodexContext ? "⚠ " : string.Empty)}" +
            AppLanguageManager.Choose(
                $"【{FormatModelType(profile.ModelType)}】{(VisionValidationFingerprint.HasUsableSource(profile) ? "【Vision】" : string.Empty)}{(MtpValidationFingerprint.HasUsableSource(profile) ? "【MTP】" : string.Empty)}{profile.DisplayName} · {(isUnavailable ? "模型文件已删除或待重新添加" : $"Context {profile.ContextSize:N0} · 安全余量 {profile.CompactionSafetyReserve:N0} · {profile.CacheTypeK}/{profile.CacheTypeV}")}",
                $"[{FormatModelType(profile.ModelType)}]{(VisionValidationFingerprint.HasUsableSource(profile) ? " [Vision]" : string.Empty)}{(MtpValidationFingerprint.HasUsableSource(profile) ? " [MTP]" : string.Empty)} {profile.DisplayName} · {(isUnavailable ? "model files missing or awaiting re-add" : $"Context {profile.ContextSize:N0} · Reserve {profile.CompactionSafetyReserve:N0} · {profile.CacheTypeK}/{profile.CacheTypeV}")}"),
            null,
            profile,
            isUnavailable);
    }

    private sealed record CacheModelListItem(
        string Summary,
        LlamaModelRuntimeInfo Native,
        ModelProfile Profile)
    {
        public static CacheModelListItem Create(LlamaModelRuntimeInfo native, ModelProfile profile) => new(
            $"{profile.DisplayName} · {FormatSize(native.SizeBytes ?? profile.KnownSizeBytes ?? 0)} · {TranslateModelStatus(native.Status)}",
            native,
            profile);
    }

    private sealed record AgentProcessIdentity(
        int ProcessId,
        string ExecutablePath,
        DateTimeOffset StartedAtUtc);

    private sealed record TrustedRouterEndpoint(Uri BaseUri, string ApiKey);
}
