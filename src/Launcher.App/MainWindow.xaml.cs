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
    private readonly DispatcherTimer _statusRefreshTimer;
    private readonly AgentStartupRegistration _agentStartupRegistration =
        new(new WindowsUserRunEntryStore());
    private LauncherSettings _settings = new();
    private IReadOnlyList<ModelProfile> _profiles = Array.Empty<ModelProfile>();
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

    public MainWindow()
    {
        InitializeComponent();
        var displayVersion = GetDisplayVersion();
        HeaderVersionText.Text = $"{displayVersion} 公测版";
        AboutVersionText.Text = displayVersion;
        _statusRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _statusRefreshTimer.Tick += StatusRefreshTimer_Tick;
    }

    private static string GetDisplayVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is null
            ? "未知"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
            var configTransactionService = new ChatGptConfigTransactionService(_clientDetector);
            var recovery = await new ModeRecoveryCoordinator(
                settingsStore,
                configTransactionService,
                _paths).RecoverAsync(_lifetime.Token);
            _settings = await settingsStore.LoadAsync(_lifetime.Token);
            RefreshAgentStartupRegistration();
            await RefreshEnvironmentAsync(_lifetime.Token);
            _statusRefreshTimer.Start();

            await OfferLegacyBackupMigrationAsync(configTransactionService, _lifetime.Token);

            if (recovery.Changed)
            {
                StatusText.Text = "检测到上次未完成的模式切换，配置与 Launcher 状态已安全收敛。";
            }

            if (!string.IsNullOrWhiteSpace(_settings.LlamaRoot))
            {
                await ConfigureRuntimeAsync(
                    _settings.LlamaRoot,
                    saveSelection: false,
                    offerToCreateFolders: false,
                    _lifetime.Token);
            }
            else
            {
                RenderRuntimeUnavailable("尚未配置 Runtime。");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取环境失败：{exception.Message}";
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
            $"检测到 {legacyCount} 份 Launcher 旧版本留下的明文 config.toml 备份。推荐选择“是”，现在使用当前 Windows 用户的 DPAPI 加密迁移。\n\n"
            + "该操作只保护 Launcher 备份，不修改当前 ChatGPT 配置或 auth.json。"
            + "每份密文回读校验成功并更新恢复记录后，才会删除对应明文。",
            "保护旧版配置备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "旧版明文配置备份尚未迁移；可在下次启动 Launcher 时处理。";
            return;
        }

        var result = await configTransactionService.MigrateLegacyBackupsAsync(_paths, cancellationToken);
        StatusText.Text = result.RemainingPlaintextCount == 0
            ? $"已安全迁移 {result.MigratedCount} 份旧版配置备份。"
            : $"已迁移 {result.MigratedCount} 份，仍有 {result.RemainingPlaintextCount} 份明文备份需要处理。";
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _statusRefreshTimer.Stop();
        _statusRefreshTimer.Tick -= StatusRefreshTimer_Tick;
        _lifetime.Cancel();
        _performanceSampler.Dispose();
        _lifetime.Dispose();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
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
            MessageBox.Show(
                this,
                "启动器正在保存或切换配置。请等待当前操作完成后再关闭，以免中断事务。",
                "暂时无法关闭",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!IsAgentRunning())
        {
            return;
        }

        e.Cancel = true;
        if (_settings.SelectedMode == ProviderMode.Local)
        {
            var dialog = new LocalServiceCloseDialog { Owner = this };
            _ = dialog.ShowDialog();
            if (dialog.Choice == LocalServiceCloseChoice.Cancel)
            {
                return;
            }

            if (dialog.Choice == LocalServiceCloseChoice.KeepRunning)
            {
                _allowClose = true;
                e.Cancel = false;
                return;
            }
        }

        _closeInProgress = true;
        IsEnabled = false;
        StatusText.Text = _settings.SelectedMode == ProviderMode.Local
            ? "正在正常停止 Local 后台服务…"
            : "正在结束当前无需驻留的后台 Agent…";
        try
        {
            if (!await StopAgentForExitAsync())
            {
                StatusText.Text = "后台 Agent 未退出；程序目录仍可能被占用。";
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
            StatusText.Text = $"结束后台 Agent 失败：{exception.Message}";
            MessageBox.Show(
                this,
                StatusText.Text,
                "无法完整退出",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!_allowClose)
            {
                IsEnabled = true;
                _closeInProgress = false;
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
            ? "Agent 在等待期内没有退出。"
            : controlResult.Diagnostic;
        var verifiedAgent = await TryGetVerifiedAgentIdentityAsync(processIds, _lifetime.Token);
        if (verifiedAgent is null)
        {
            MessageBox.Show(
                this,
                $"后台 Agent 未能正常退出：{diagnostic}\n\n"
                + "启动器无法用运行状态文件确认该进程属于当前程序包，因此不会强制结束同名进程。"
                + "请在任务管理器中核对路径后手动结束，或重新启动 Windows。",
                "无法确认后台进程归属",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var answer = MessageBox.Show(
            this,
            $"后台 Agent 未能正常退出：{diagnostic}\n"
            + $"已确认 PID {verifiedAgent.ProcessId} 的路径、启动时间和状态协议均属于当前程序包。\n\n"
            + "是否强制结束该 Agent？如果当前处于 Local 模式，回环代理和 llama.cpp Router 也会停止，但模式配置不会切换。",
            "结束当前后台 Agent",
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
                $"无法结束 Launcher.Agent PID {verifiedAgent.ProcessId}：{exception.Message}",
                "后台 Agent 仍在运行",
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
        RenderEnvironmentStatus();
        if (ReferenceEquals(LauncherTabs.SelectedItem, RuntimeMonitorTab))
        {
            await RefreshMonitoringAsync(forceHardwareRefresh: false);
        }
    }

    private void RenderEnvironmentStatus()
    {
        CurrentModeText.Text = _settings.SelectedMode == ProviderMode.OpenAI ? "OpenAI" : "Local LLM";
        EnvironmentText.Text =
            $"Codex 配置：{(_configExists ? "已发现" : "未发现")}   ·   " +
            $"历史状态：{(_historyStateExists ? "已发现" : "未发现")}   ·   " +
            $"ChatGPT Desktop：{(_clientDetector.IsRunning() ? "检测到运行进程" : "已关闭")}\n" +
            "账户凭据内容不会被读取。";
    }

    private async void SelectRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_settings.SelectedMode == ProviderMode.Local)
        {
            StatusText.Text = "Local 模式正在使用当前 Runtime；请先切换到 OpenAI 模式再更换文件夹。";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择 llama.cpp Runtime 文件夹",
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
            StatusText.Text = $"Runtime 配置失败：{exception.Message}";
        }
    }

    private async void StartAgentAtLoginCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(_agentExecutablePath))
        {
            RefreshAgentStartupRegistration();
            return;
        }

        var before = _agentStartupRegistration.Inspect(_agentExecutablePath);
        var enabled = StartAgentAtLoginCheckBox.IsChecked == true;
        try
        {
            SetBusy(true);
            _agentStartupRegistration.SetEnabled(enabled, _agentExecutablePath);
            using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
            var updated = _settings with { StartAgentAtLogin = enabled };
            await settingsStore.SaveAsync(updated, _lifetime.Token);
            _settings = updated;
            StatusText.Text = enabled
                ? "已启用登录启动；以后可直接打开 ChatGPT Desktop 并沿用当前模式。"
                : "已关闭登录启动；需要 Local 模式时请先启动 Launcher 或 Agent。";
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
                        $"Agent 登录启动项更新失败，且回滚也失败：{exception.Message}；{rollbackException.Message}";
                }

                return;
            }

            if (!_lifetime.IsCancellationRequested)
            {
                StatusText.Text = $"Agent 登录启动项更新失败，已回滚：{exception.Message}";
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
            StatusText.Text = "已打开诊断日志文件夹；proxy.jsonl 不记录提示词正文或认证头。llama 原生 stdout/stderr 内容由 Runtime 决定。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法打开诊断日志文件夹：{exception.Message}";
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
            RuntimeStatusText.Text = "正在验证 llama-server 版本与能力…";
            DeviceText.Text = string.Empty;
            StatusText.Text = "正在验证 Runtime；不会加载任何 GGUF 模型。";

            var probe = await _runtimeProbe.ProbeAsync(root, cancellationToken);
            if (!probe.IsValid)
            {
                var diagnostic = probe.Diagnostics.Count == 0
                    ? "llama-server 验证失败。"
                    : string.Join(" ", probe.Diagnostics);
                RenderRuntimeUnavailable(diagnostic);
                return;
            }

            if (!probe.Capabilities.SupportsRouter)
            {
                RenderRuntimeUnavailable(
                    "当前 llama.cpp Runtime 缺少完整 Router 参数，本版本不会在 Launcher 内模拟该能力。"
                    + "请升级或选择兼容的 llama.cpp Runtime。");
                return;
            }

            if (!probe.Capabilities.SupportsIdleSleep)
            {
                RenderRuntimeUnavailable(
                    "当前 llama.cpp Runtime 缺少 --sleep-idle-seconds，无法由 llama.cpp 原生管理空闲显存释放。"
                    + "请升级或选择兼容的 llama.cpp Runtime。");
                return;
            }

            if (!probe.Capabilities.SupportsChatTemplateFile)
            {
                RenderRuntimeUnavailable(
                    "当前 llama.cpp Runtime 缺少 --chat-template-file，无法由 llama.cpp 加载模型专用 Codex 兼容模板。"
                    + "请升级或选择兼容的 llama.cpp Runtime。");
                return;
            }

            if (offerToCreateFolders)
            {
                EnsureRuntimeFolders(root);
            }

            var updatedSettings = RuntimeSelectionPolicy.ApplyProbeResult(
                _settings,
                root,
                saveSelection,
                probe.Capabilities.SupportsMetrics);
            if (updatedSettings != _settings)
            {
                using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
                await settingsStore.SaveAsync(updatedSettings, cancellationToken);
                _settings = updatedSettings;
            }

            _runtimeIsValid = true;
            _runtimeDeviceOutput = probe.DeviceOutput;
            RuntimeStatusText.Text =
                $"验证通过 · {probe.VersionText ?? "版本未知"} · Router / 空闲休眠 / 模板文件：支持 · " +
                $"Metrics：{(probe.Capabilities.SupportsMetrics ? "支持" : "不可用（不影响推理）")}";
            DeviceText.Text = CompactDeviceOutput(probe.DeviceOutput);
            await ReloadProfilesAsync(root, cancellationToken);
            StatusText.Text = saveSelection
                ? "Runtime 已保存。可以扫描模型；尚未加载模型或修改 ChatGPT 配置。"
                : "已读取上次保存的 Runtime；尚未加载模型或修改 ChatGPT 配置。";
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
            $"缺少目录：{string.Join("、", missingDirectories)}。是否现在创建？",
            "初始化 llama.cpp 目录",
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
            StatusText.Text = "正在递归扫描 GGUF；模型不会被加载或自动添加。";
            var modelsRoot = Path.Combine(_settings.LlamaRoot, "models");
            var scan = await Task.Run(
                () => _modelScanner.Scan(modelsRoot, _lifetime.Token),
                _lifetime.Token);
            var managedPaths = _profiles
                .Select(profile => Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _discoveredModels = scan.Models
                .Where(model => !managedPaths.Contains(Path.GetFullPath(model.PrimaryPath)))
                .ToArray();
            DiscoveredModelsList.ItemsSource = _discoveredModels.Select(ModelListItem.FromCandidate).ToArray();
            AddModelButton.IsEnabled = false;

            StatusText.Text =
                $"扫描完成：{scan.Models.Count} 个主模型，{scan.ExcludedFiles.Count} 个文件被排除，" +
                $"{_discoveredModels.Count} 个尚未添加。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"扫描失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SearchDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_runtimeIsValid || string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            return;
        }

        var dialog = new ModelDownloadWindow(_settings.LlamaRoot, GetModelDownloadBlockReason)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.DownloadedModel is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var downloadedPath = Path.GetFullPath(dialog.DownloadedModel.PrimaryPath);
            var existing = _profiles.FirstOrDefault(profile => string.Equals(
                Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath)),
                downloadedPath,
                StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                StatusText.Text = $"{existing.DisplayName} 已经是已管理模型，无需重复添加。";
                return;
            }

            var added = await AddCandidateAsync(dialog.DownloadedModel, _lifetime.Token);
            StatusText.Text = added.Status;
            await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            await OfferAutoFitAfterAddAsync(added.Profile);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"登记下载模型失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void DiscoveredModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddModelButton.IsEnabled = !_busy && DiscoveredModelsList.SelectedIndex >= 0;
    }

    private void ManagedModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = ManagedModelsList.SelectedItem is ModelListItem { Profile: not null };
        EditModelButton.IsEnabled = !_busy && hasSelection && CanEditModelParameters();
        AutoFitModelButton.IsEnabled = !_busy && hasSelection && CanUseFitTool();
        ModelDetailsButton.IsEnabled = !_busy && hasSelection;
        ActivateLocalButton.IsEnabled = !_busy && hasSelection;
        SelectedModelDetailsText.Text = ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } selected
            ? FormatProfileSummary(selected.Profile)
            : "请选择模型以查看文件、来源和参数摘要。";
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
            await OfferAutoFitAfterAddAsync(added.Profile);

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
            StatusText.Text = $"添加模型失败：{exception.Message}";
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
            ?? throw new InvalidOperationException("尚未配置 llama.cpp Runtime。");
        var profile = ModelProfileFactory.CreateDefault(candidate, runtimeRoot, _profiles);
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

        var contextReminder = profile.ContextSize < 1_024
            ? " 请先按模型和硬件编辑 Context，之后才能切换到 ChatGPT Desktop。"
            : string.Empty;
        var status = compatibility.TemplateGenerated
            ? $"已添加 {profile.DisplayName}；已从该 GGUF 生成模型专用 Codex 模板并交由 llama.cpp 加载。{contextReminder}"
            : $"已添加 {profile.DisplayName}，并生成可独立运行的 BAT；尚未加载模型或修改 ChatGPT 配置。{contextReminder}";
        return (status, profile);
    }

    private async Task ReloadProfilesAsync(string runtimeRoot, CancellationToken cancellationToken)
    {
        var result = await _profileStore.LoadAsync(runtimeRoot, cancellationToken);
        _profiles = result.Profiles;
        ManagedModelsList.ItemsSource = _profiles.Select(ModelListItem.FromProfile).ToArray();
        EditModelButton.IsEnabled = false;
        AutoFitModelButton.IsEnabled = false;
        ModelDetailsButton.IsEnabled = false;
        SelectedModelDetailsText.Text = "请选择模型以查看文件、来源和参数摘要。";
        if (result.Diagnostics.Count > 0)
        {
            StatusText.Text = $"部分 Profile 未载入：{string.Join(" ", result.Diagnostics)}";
        }
    }

    private async void EditModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        if (!CanEditModelParameters())
        {
            StatusText.Text = "Local 客户端、后台 Agent 或当前 Runtime 的 llama 服务正在运行；请先停止 Local 服务，再编辑任何模型参数。";
            return;
        }

        var editor = new ProfileEditorWindow(selected.Profile, _settings.LlamaRoot)
        {
            Owner = this,
        };
        if (editor.ShowDialog() != true)
        {
            StatusText.Text = "已取消编辑，Profile 未改变。";
            return;
        }

        SetBusy(true);
        try
        {
            await SaveProfileArtifactsAsync(editor.UpdatedProfile, _lifetime.Token);
            await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            StatusText.Text = editor.SavedAsModelDefault
                ? $"已保存 {editor.UpdatedProfile.DisplayName}，并将当前参数设为该模型的专用默认；其他模型不受影响。"
                : $"已保存 {editor.UpdatedProfile.DisplayName} 并重新生成 BAT；新参数将在下一次模型加载时生效。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存 Profile 失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void AutoFitModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        if (!CanEditModelParameters())
        {
            StatusText.Text = "Local 客户端、后台 Agent 或 llama 服务运行期间不能修改任何模型参数；请先停止 Local 服务。";
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
            StatusText.Text = $"llama 自动适配失败：{exception.Message}";
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
            $"是否让 llama.cpp 为“{profile.DisplayName}”计算一次可选参数建议？\n\n"
            + "这会运行 llama-fit-params，但不会启动 ChatGPT、不会修改其他模型，也不会强制采用结果。",
            "可选的 llama 参数适配",
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
            throw new InvalidOperationException("尚未配置 llama.cpp Runtime。");
        }

        if (_settings.SelectedMode != ProviderMode.OpenAI)
        {
            throw new InvalidOperationException("请先切换到 OpenAI，再运行 llama 参数适配，避免与 Local Router 争用硬件。");
        }

        if (IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            throw new InvalidOperationException("llama 服务正在运行。请先释放并结束 Local 服务，再执行参数适配。");
        }

        StatusText.Text = $"正在由 llama-fit-params 分析 {profile.DisplayName}；不会加载模型权重。";
        var modelPath = Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath));
        var recommendation = await _fitParamsRunner.RecommendAsync(
            _settings.LlamaRoot,
            modelPath,
            minimumContextSize: 4096,
            cancellationToken);
        var details =
            $"llama.cpp 建议：\nContext：{recommendation.ContextSize:N0}\nGPU Layers：{recommendation.GpuLayers:N0}" +
            (recommendation.TensorSplit is null ? string.Empty : $"\nTensor Split：{recommendation.TensorSplit}") +
            (recommendation.TensorBufferOverrides is null ? string.Empty : "\nTensor Buffer：由 llama 自动生成") +
            $"\n\n当前：Context {profile.ContextSize:N0}，GPU Layers {profile.GpuLayers}\n\n是否应用到该模型当前参数？";
        if (MessageBox.Show(
                this,
                details,
                "llama 参数建议",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            StatusText.Text = "已保留原参数；llama 建议未应用。";
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
            "是否同时把这组参数设为该模型的专用默认值？\n\n选择“否”只更新当前参数；其他模型始终不受影响。",
            "保存该模型默认值",
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
            ? $"已应用 llama 建议，并设为 {updated.DisplayName} 的专用默认值。"
            : $"已应用 llama 建议到 {updated.DisplayName}；专用默认值未改变。";
    }

    private async Task SaveProfileArtifactsAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException("尚未配置 llama.cpp Runtime。");
        if (!CanEditModelParameters())
        {
            throw new InvalidOperationException(
                "Local 客户端、后台 Agent 或 llama 服务运行期间不能保存任何模型参数。请先停止 Local 服务。");
        }

        var updateActiveLocalConfiguration = _settings.SelectedMode == ProviderMode.Local
            && string.Equals(_settings.SelectedModelId, profile.Id, StringComparison.Ordinal);
        await _modelArtifactTransaction.ExecuteAsync(
            runtimeRoot,
            profile.Id,
            _paths.RouterPresetFile,
            includeRouterPreset: updateActiveLocalConfiguration,
            async transactionCancellationToken =>
            {
                BatchScriptGenerator.EnsureCanWrite(runtimeRoot, profile.Id);
                _ = BatchScriptGenerator.Generate(profile, runtimeRoot);
                await _profileStore.SaveAsync(runtimeRoot, profile, transactionCancellationToken);
                await BatchScriptGenerator.WriteOwnedAsync(runtimeRoot, profile, transactionCancellationToken);
                if (updateActiveLocalConfiguration)
                {
                    using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
                    var coordinator = new ModeSwitchCoordinator(
                        settingsStore,
                        _clientDetector,
                        new ChatGptConfigTransactionService(_clientDetector),
                        _paths);
                    await coordinator.SwitchToLocalAsync(
                        new LocalModeSwitchRequest
                        {
                            CodexHome = _codexHome,
                            RuntimeRoot = runtimeRoot,
                            Profile = profile,
                            RouterPort = _settings.RouterPort,
                        },
                        transactionCancellationToken);
                    _settings = await settingsStore.LoadAsync(transactionCancellationToken);
                }

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
            ?? throw new InvalidOperationException("Router preset 缺少父目录。");
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

    private bool CanEditModelParameters() =>
        _runtimeIsValid
        && !IsAgentRunning()
        && !IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot)
        && !(_settings.SelectedMode == ProviderMode.Local && _clientDetector.IsRunning());

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

        SetBusy(true);
        try
        {
            var text = FormatProfileDetails(selected.Profile);
            var endpoint = await TryGetTrustedRouterEndpointAsync(_lifetime.Token);
            if (endpoint is not null)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                var models = await new LlamaModelManagementClient(http).ListAsync(
                    endpoint.BaseUri,
                    cancellationToken: _lifetime.Token);
                var native = models.FirstOrDefault(model =>
                    string.Equals(model.Id, selected.Profile.Alias, StringComparison.Ordinal)
                    || string.Equals(model.Id, selected.Profile.RemoteModelId, StringComparison.Ordinal));
                if (native is not null)
                {
                    text += "\n\nllama.cpp 当前数据\n" + FormatNativeModelDetails(native);
                }
            }

            MessageBox.Show(this, text, selected.Profile.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "模型详情读取完成；未加载或切换模型。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取模型详情失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ActivateLocalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy
            || !_runtimeIsValid
            || string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            || string.IsNullOrWhiteSpace(_codexHome)
            || ManagedModelsList.SelectedItem is not ModelListItem { Profile: not null } selected)
        {
            return;
        }

        try
        {
            var clientRunning = _clientDetector.IsRunning();
            var activeProfile = selected.Profile;
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
                    "当前 Local 模型正在运行，无法检查并应用模型专用 Chat Template。请先完全关闭 ChatGPT Desktop。");
            }

            var updateActivePreset = _settings.SelectedMode == ProviderMode.Local
                && string.Equals(_settings.SelectedModelId, activeProfile.Id, StringComparison.Ordinal);
            var compatibility = await _modelArtifactTransaction.ExecuteAsync(
                _settings.LlamaRoot,
                activeProfile.Id,
                _paths.RouterPresetFile,
                updateActivePreset,
                async transactionCancellationToken =>
                {
                    var result = await CodexChatTemplateCompatibility.EnsureAsync(
                        activeProfile,
                        _settings.LlamaRoot,
                        transactionCancellationToken);
                    if (!result.TemplateGenerated)
                    {
                        return result;
                    }

                    BatchScriptGenerator.EnsureCanWrite(_settings.LlamaRoot, result.Profile.Id);
                    _ = BatchScriptGenerator.Generate(result.Profile, _settings.LlamaRoot);
                    await _profileStore.SaveAsync(
                        _settings.LlamaRoot,
                        result.Profile,
                        transactionCancellationToken);
                    await BatchScriptGenerator.WriteOwnedAsync(
                        _settings.LlamaRoot,
                        result.Profile,
                        transactionCancellationToken);
                    if (updateActivePreset)
                    {
                        await WriteRouterPresetAtomicallyAsync(
                            result.Profile,
                            _settings.LlamaRoot,
                            transactionCancellationToken);
                    }

                    return result;
                },
                _lifetime.Token);
            if (compatibility.TemplateGenerated)
            {
                activeProfile = compatibility.Profile;
                await ReloadProfilesAsync(_settings.LlamaRoot, _lifetime.Token);
            }

            if (activeProfile.ContextSize < 1_024)
            {
                throw new InvalidOperationException(
                    "该模型尚未设置可供 ChatGPT Desktop 声明的 Context。请根据模型和硬件编辑 Profile；Launcher 不会自动替你选择。");
            }

            var lowContextWarning = activeProfile.ContextSize < ModelProfile.RecommendedMinimumCodexContext
                ? "\n\n⚠ 当前 Context 低于 16K，可能无法完整容纳 Codex 的系统指令、项目上下文和工具定义，"
                    + "容易在首次对话或工具执行时出现上下文不足。建议使用 16K 或更高；本提醒不会阻止启动。"
                : string.Empty;

            if (isSameLocalModel && !compatibility.TemplateGenerated)
            {
                if (!string.IsNullOrEmpty(lowContextWarning))
                {
                    MessageBox.Show(
                        this,
                        lowContextWarning.Trim(),
                        "Context 容量风险提醒",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                SetBusy(true);
                StatusText.Text = "正在确认 Local Router 与后台 Agent…";
                if (!await EnsureAgentRunningAsync(_lifetime.Token))
                {
                    StatusText.Text = "找不到或无法启动 Launcher.Agent。";
                    return;
                }

                var existingRuntimeReady = await WaitForLocalRouterAsync(
                    activeProfile.Alias,
                    _lifetime.Token);
                StatusText.Text = existingRuntimeReady
                    ? FormatLaunchResult(
                        await LaunchClientAndRefreshAsync(_lifetime.Token),
                        $"Local Router 已就绪：{activeProfile.DisplayName}")
                    : "Local Router 未在期限内就绪；请查看 Agent/Router 日志。";
                return;
            }

            var isChangingLocalModel = _settings.SelectedMode == ProviderMode.Local;
            if (isChangingLocalModel && clientRunning)
            {
                throw new InvalidOperationException("请先完全关闭 ChatGPT Desktop 后再切换本地模型。");
            }

            var answer = MessageBox.Show(
                this,
                $"即将{(isChangingLocalModel ? "更新" : "切换")} ChatGPT Desktop 的本地模型：\n\n" +
                $"{activeProfile.DisplayName} · Context {activeProfile.ContextSize:N0}" + lowContextWarning + "\n\n" +
                "Local 模式保留 Desktop 的官方账户外壳以共享项目和历史，但代理不会把官方凭据转发给 llama.cpp。"
                + "模型参数和 Chat Template 均由 llama.cpp 执行；启动器会保存并恢复完整的官方模型预设。"
                + "权限仍在 ChatGPT Desktop 输入框下方设置。是否继续？",
                isChangingLocalModel ? "切换本地模型" : "应用 Local 模式",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "已取消 Local 模式切换。";
                return;
            }

            SetBusy(true);
            StatusText.Text = isChangingLocalModel
                ? "正在事务性更新 Local 配置，并等待 Agent 重启 Router…"
                : "正在启动后台 Agent 并准备 Local 配置…";
            if (!await EnsureAgentRunningAsync(_lifetime.Token))
            {
                StatusText.Text = "找不到或无法启动 Launcher.Agent；未修改 ChatGPT 配置。";
                return;
            }

            using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
            var coordinator = new ModeSwitchCoordinator(
                settingsStore,
                _clientDetector,
                new ChatGptConfigTransactionService(_clientDetector),
                _paths);
            var result = await coordinator.SwitchToLocalAsync(
                new LocalModeSwitchRequest
                {
                    CodexHome = _codexHome,
                    RuntimeRoot = _settings.LlamaRoot,
                    Profile = activeProfile,
                    RouterPort = _settings.RouterPort,
                },
                _lifetime.Token);
            _settings = await settingsStore.LoadAsync(_lifetime.Token);
            await RefreshEnvironmentAsync(_lifetime.Token);
            if (!await EnsureAgentRunningAsync(_lifetime.Token))
            {
                StatusText.Text = "Local 配置已应用，但后台 Agent 无法启动；请勿启动 ChatGPT，并检查 Agent 日志。";
                return;
            }

            var runtimeReady = await WaitForLocalRouterAsync(
                activeProfile.Alias,
                _lifetime.Token);
            StatusText.Text = runtimeReady
                ? FormatLaunchResult(
                    await LaunchClientAndRefreshAsync(_lifetime.Token),
                    $"已切换到 Local：{activeProfile.DisplayName}。Router 已就绪")
                : "Local 配置已应用，但 Router 未在期限内就绪；请查看 Agent/Router 日志后再启动 ChatGPT。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Local 模式切换失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse<ProviderMode>(tag, ignoreCase: true, out var targetMode))
        {
            return;
        }

        if (targetMode == ProviderMode.Local)
        {
            LauncherTabs.SelectedItem = LocalModelsTab;
            StatusText.Text = _profiles.Count == 0
                ? "请先配置 Runtime、扫描并添加本地模型。"
                : "请选择一个已管理模型，再点击“应用 Local 模式”。";
            return;
        }

        if (_settings.SelectedMode == ProviderMode.OpenAI)
        {
            StatusText.Text = FormatLaunchResult(
                await LaunchClientAndRefreshAsync(_lifetime.Token),
                "当前为 OpenAI 模式");
            return;
        }

        try
        {
            ModeSwitchGuard.EnsureCanSwitch(
                _settings.SelectedMode,
                ProviderMode.OpenAI,
                _clientDetector.IsRunning());
            var answer = MessageBox.Show(
                this,
                "即将恢复切换前保存的 OpenAI Provider 字段。账户、项目和历史文件不会被替换。是否继续？",
                "恢复 OpenAI 模式",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "已取消 OpenAI 模式切换。";
                return;
            }

            SetBusy(true);
            using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
            var coordinator = new ModeSwitchCoordinator(
                settingsStore,
                _clientDetector,
                new ChatGptConfigTransactionService(_clientDetector),
                _paths);
            await coordinator.SwitchToOpenAIAsync(_lifetime.Token);
            _settings = await settingsStore.LoadAsync(_lifetime.Token);
            await RefreshEnvironmentAsync(_lifetime.Token);
            StatusText.Text = FormatLaunchResult(
                await LaunchClientAndRefreshAsync(_lifetime.Token),
                "OpenAI Provider 字段已恢复；Local 模型选择已保留");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"OpenAI 模式恢复失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshMonitoringButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshMonitoringAsync(forceHardwareRefresh: true);

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
                HardwareInventoryText.Text = "正在读取 Windows 硬件清单…";
                _hardwareInventory = await _hardwareInventoryReader.ReadAsync(_lifetime.Token);
            }

            HardwareInventoryText.Text = FormatHardwareInventory(_hardwareInventory, _runtimeDeviceOutput);
            var performance = await Task.Run(
                () => _performanceSampler.Sample(_settings.LlamaRoot),
                _lifetime.Token);
            SystemPerformanceText.Text = FormatPerformance(performance);

            LoadCurrentModelButton.Tag = false;
            UnloadCurrentModelButton.Tag = false;
            var endpoint = await TryGetTrustedRouterEndpointAsync(_lifetime.Token);
            var profile = _profiles.FirstOrDefault(value => string.Equals(
                value.Id,
                _settings.SelectedModelId,
                StringComparison.Ordinal));
            if (endpoint is null || profile is null)
            {
                LlamaRuntimeText.Text = _settings.SelectedMode == ProviderMode.Local
                    ? "Local 模式已保存，但当前 Agent/Router 未运行或状态尚未通过校验。"
                    : "当前为 OpenAI 模式；没有 Local Router 可监控。";
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
                LlamaRuntimeText.Text = $"llama Router 正在运行，但未返回当前模型 {profile.Alias}。";
                UpdateRuntimeActionButtons();
                return;
            }

            var stateText = $"llama 模型：{native.Id} · 状态 {TranslateModelStatus(native.Status)}";
            if (native.Failed)
            {
                stateText += $" · 上次加载失败（退出码 {native.ExitCode?.ToString() ?? "未知"}）";
            }

            LoadCurrentModelButton.Tag = native.Status is "unloaded" or "sleeping";
            UnloadCurrentModelButton.Tag = native.Status is "loaded" or "loading" or "sleeping";
            if (string.Equals(native.Status, "loaded", StringComparison.OrdinalIgnoreCase))
            {
                var telemetry = await new LlamaTelemetryClient(http).ReadAsync(
                    endpoint.BaseUri,
                    profile.Alias,
                    _lifetime.Token);
                stateText += "\n" + FormatTelemetry(telemetry);
            }
            else if (string.Equals(native.Status, "sleeping", StringComparison.OrdinalIgnoreCase))
            {
                stateText += "\n模型已由 llama 原生空闲休眠释放；监控不会为了读取指标将它唤醒。";
            }

            LlamaRuntimeText.Text = stateText;
            UpdateRuntimeActionButtons();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LlamaRuntimeText.Text = $"运行状态暂不可用：{exception.Message}";
            LoadCurrentModelButton.Tag = false;
            UnloadCurrentModelButton.Tag = false;
            UpdateRuntimeActionButtons();
        }
        finally
        {
            _monitorRefreshInProgress = false;
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
            StatusText.Text = "当前没有通过安全校验的 Local Router 与选中模型。";
            return;
        }

        SetBusy(true);
        try
        {
            StatusText.Text = load
                ? $"正在请求 llama.cpp 立即加载 {profile.DisplayName}…"
                : $"正在请求 llama.cpp 立即释放 {profile.DisplayName}…";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            var client = new LlamaModelManagementClient(http);
            var result = load
                ? await client.LoadAsync(endpoint.BaseUri, profile.Alias, _lifetime.Token)
                : await client.UnloadAsync(endpoint.BaseUri, profile.Alias, _lifetime.Token);
            StatusText.Text = result.Succeeded
                ? load ? "llama.cpp 已接受加载请求；状态会持续刷新。" : "llama.cpp 已接受释放请求；显存释放需要短暂时间。"
                : result.Diagnostic ?? "llama.cpp 未接受操作。";
            await Task.Delay(500, _lifetime.Token);
            await RefreshMonitoringAsync(forceHardwareRefresh: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"llama 模型操作失败：{exception.Message}";
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
            CacheStatusText.Text = $"缓存读取失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshCacheAsync(CancellationToken cancellationToken)
    {
        if (_settings.SelectedMode == ProviderMode.Local
            || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            _cachedModels = [];
            CachedModelsList.ItemsSource = _cachedModels;
            CacheStatusText.Text = "Local 服务运行或模式仍为 Local。为避免与正在使用的模型文件冲突，请先切换到 OpenAI 再管理缓存。";
            return;
        }

        CacheStatusText.Text = "正在启动临时 llama Router 读取原生缓存清单；不会加载模型。";
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
            ? "没有同时满足“llama 可删除”和“Launcher 已验证下载来源”的缓存。旧版或手动文件不会被猜测为可删除缓存。"
            : $"llama.cpp 返回 {_cachedModels.Count} 个可安全管理的已登记缓存。";
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

        if (_settings.SelectedMode == ProviderMode.Local || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            CacheStatusText.Text = "请先切换到 OpenAI 并结束 Local 服务，再删除缓存。";
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定让 llama.cpp 删除缓存模型吗？\n\n{selected.Profile.DisplayName}\n{selected.Native.Id}\n{selected.Summary}\n\n"
            + "模型文件删除后无法由启动器恢复，需要重新下载；官方账户、项目和历史不会被触碰。",
            "删除 llama 模型缓存",
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
                throw new InvalidDataException("缓存 Profile 未通过删除前安全校验。");
            }

            var result = await WithTemporaryManagementRouterAsync(
                (client, endpoint, token) => client.RemoveCachedAsync(endpoint, selected.Native.Id, token),
                _lifetime.Token);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Diagnostic ?? "llama.cpp 拒绝删除该缓存。");
            }

            nativeModelDeleted = true;
            var cleanupWarnings = new List<string>();
            try
            {
                BatchScriptGenerator.DeleteOwned(_settings.LlamaRoot, selected.Profile.Id);
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add($"BAT 清理失败：{exception.Message}");
            }

            try
            {
                _profileStore.DeleteLauncherMetadata(_settings.LlamaRoot, selected.Profile);
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add($"Profile 清理失败：{exception.Message}");
            }

            if (string.Equals(_settings.SelectedModelId, selected.Profile.Id, StringComparison.Ordinal))
            {
                try
                {
                    using var settingsStore = new JsonSettingsStore(_paths.SettingsFile);
                    var updatedSettings = _settings with { SelectedModelId = null };
                    await settingsStore.SaveAsync(updatedSettings, _lifetime.Token);
                    _settings = updatedSettings;
                }
                catch (Exception exception)
                {
                    cleanupWarnings.Add($"当前模型状态清理失败：{exception.Message}");
                }
            }

            try
            {
                await ReloadProfilesAsync(_settings.LlamaRoot!, _lifetime.Token);
                await RefreshCacheAsync(_lifetime.Token);
            }
            catch (Exception exception)
            {
                cleanupWarnings.Add($"界面刷新失败：{exception.Message}");
            }

            if (cleanupWarnings.Count == 0)
            {
                StatusText.Text = $"已由 llama.cpp 删除 {selected.Profile.DisplayName} 的缓存，并清理对应 Launcher Profile/BAT。";
            }
            else
            {
                var warning = $"llama.cpp 已删除 {selected.Profile.DisplayName}，但 Launcher 元数据清理不完整："
                    + string.Join("；", cleanupWarnings);
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
                ? $"llama.cpp 已删除模型，但 Launcher 后续清理未完成：{exception.Message}"
                : $"缓存删除失败，模型未被确认删除：{exception.Message}";
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
            ?? throw new InvalidOperationException("尚未配置 llama.cpp Runtime。");
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
        if (_settings.SelectedMode == ProviderMode.Local)
        {
            return "当前处于 Local 模式，持久 Router 正在管理模型。请先切换到 OpenAI 模式再下载；搜索和查看模型详情仍可使用。";
        }

        if (string.IsNullOrWhiteSpace(_settings.LlamaRoot))
        {
            return "尚未配置 llama.cpp Runtime。";
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
                        return "当前 Runtime 已有 llama-server 正在运行。为避免两个原生 Router 争用模型缓存，本次不启动下载服务。";
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
            ? "Windows 拒绝读取某个 llama-server 的程序路径。为避免误与现有服务并发，本次不启动下载服务。"
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
        AutoFitModelButton.IsEnabled = false;
        ModelDetailsButton.IsEnabled = false;
        ActivateLocalButton.IsEnabled = false;
        StatusText.Text = "Runtime 尚不可用；真实 ChatGPT Desktop 配置保持不变。";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OpenAiModeButton.IsEnabled = !busy;
        LocalModeButton.IsEnabled = !busy;
        SelectRuntimeButton.IsEnabled = !busy && _settings.SelectedMode == ProviderMode.OpenAI;
        ScanModelsButton.IsEnabled = !busy && _runtimeIsValid;
        SearchDownloadButton.IsEnabled = !busy && _runtimeIsValid;
        AddModelButton.IsEnabled = !busy && DiscoveredModelsList.SelectedIndex >= 0;
        EditModelButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0 && CanEditModelParameters();
        AutoFitModelButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0 && CanUseFitTool();
        ModelDetailsButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0;
        ActivateLocalButton.IsEnabled = !busy && ManagedModelsList.SelectedIndex >= 0;
        RefreshMonitoringButton.IsEnabled = !busy;
        RefreshCacheButton.IsEnabled = !busy && _runtimeIsValid;
        DeleteCachedModelButton.IsEnabled = !busy && CachedModelsList.SelectedItem is CacheModelListItem;
        LoadCurrentModelButton.IsEnabled = !busy && LoadCurrentModelButton.Tag is true;
        UnloadCurrentModelButton.IsEnabled = !busy && UnloadCurrentModelButton.Tag is true;
        StartAgentAtLoginCheckBox.IsEnabled = !busy && _agentExecutablePath is not null;
        OpenLogsButton.IsEnabled = !busy;
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
        if (sender is DependencyObject source)
        {
            var nestedScrollViewer = FindVisualChild<ScrollViewer>(source);
            var canScrollInside = nestedScrollViewer is not null
                && (e.Delta < 0
                    ? nestedScrollViewer.VerticalOffset < nestedScrollViewer.ScrollableHeight
                    : nestedScrollViewer.VerticalOffset > 0);
            if (canScrollInside) return;
        }

        e.Handled = true;
        RuntimeMonitorScrollViewer.ScrollToVerticalOffset(Math.Clamp(
            RuntimeMonitorScrollViewer.VerticalOffset - e.Delta,
            0,
            RuntimeMonitorScrollViewer.ScrollableHeight));
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
        if (_agentExecutablePath is null)
        {
            StartAgentAtLoginCheckBox.IsChecked = false;
            StartAgentAtLoginCheckBox.IsEnabled = false;
            AgentStartupText.Text = "当前目录中找不到 Launcher.Agent.exe，无法配置登录启动。";
            return;
        }

        try
        {
            var state = _agentStartupRegistration.Inspect(_agentExecutablePath);
            StartAgentAtLoginCheckBox.IsChecked = state.IsEnabled;
            StartAgentAtLoginCheckBox.IsEnabled = !_busy;
            AgentStartupText.Text = state.IsEnabled
                ? "已注册为当前用户登录启动项；关闭 ChatGPT 不会改变当前模式。"
                : state.HasRegistration
                    ? "检测到 Agent 的旧启动路径；重新勾选可更新为当前程序位置。"
                    : "尚未启用；不影响通过 Launcher 启动 Local 模式。";
        }
        catch (Exception exception)
        {
            StartAgentAtLoginCheckBox.IsChecked = false;
            StartAgentAtLoginCheckBox.IsEnabled = false;
            AgentStartupText.Text = $"无法读取当前用户的登录启动项：{exception.Message}";
        }
    }

    private async Task<bool> EnsureAgentRunningAsync(CancellationToken cancellationToken)
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
                    "检测到旧版或其他目录的 Launcher.Agent 正在运行，但用户取消了结束旧 Agent。"
                    + "为避免不同版本同时管理配置，本次操作已停止。");
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
            return $"{successPrefix}，但 ChatGPT Desktop 启动失败：{result.Diagnostic}";
        }

        return result.AlreadyRunning
            ? $"{successPrefix}；ChatGPT Desktop 已在运行。"
            : $"{successPrefix}；已请求 Windows 启动 ChatGPT Desktop。";
    }

    private async Task<ChatGptClientLaunchResult> LaunchClientAndRefreshAsync(
        CancellationToken cancellationToken)
    {
        var result = new ChatGptClientLauncher(
            _clientDetector,
            new ChatGptClientInstallationLocator()).Launch();
        if (result.Succeeded && !result.AlreadyRunning)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && !_clientDetector.IsRunning())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }

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
        return lines.Length == 0 ? "设备：未返回可显示的信息。" : "设备：" + string.Join(" · ", lines);
    }

    private static string FormatProfileSummary(ModelProfile profile)
    {
        var source = profile.SourceKind == ModelSourceKind.LlamaCache
            ? $"llama 下载缓存 · {profile.RemoteRepositoryId ?? "仓库未知"}"
            : "本地文件";
        return $"{source} · {FormatSize(profile.KnownSizeBytes ?? 0)} · "
            + $"{profile.KnownShardCount?.ToString() ?? "?"} 个 GGUF 分片 · Alias {profile.Alias}";
    }

    private string FormatProfileDetails(ModelProfile profile)
    {
        var path = string.IsNullOrWhiteSpace(_settings.LlamaRoot)
            ? profile.ModelRelativePath
            : Path.GetFullPath(Path.Combine(_settings.LlamaRoot, profile.ModelRelativePath));
        var currentSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        var defaults = profile.DefaultParameters is null
            ? "未设置"
            : $"Context {profile.DefaultParameters.ContextSize:N0} / 压缩安全余量 {profile.DefaultParameters.CompactionSafetyReserve:N0} / GPU Layers {profile.DefaultParameters.GpuLayers}";
        return
            $"来源：{(profile.SourceKind == ModelSourceKind.LlamaCache ? "llama 下载缓存" : "本地文件")}\n" +
            $"路径：{path}\n" +
            $"文件状态：{(File.Exists(path) ? "存在" : "不存在")}\n" +
            $"已知总大小：{FormatSize(profile.KnownSizeBytes ?? currentSize)}\n" +
            $"已知分片：{profile.KnownShardCount?.ToString() ?? "未知"}\n" +
            $"远程 ID：{profile.RemoteModelId ?? "无"}\n" +
            $"当前参数：Context {profile.ContextSize:N0} / 压缩安全余量 {profile.CompactionSafetyReserve:N0} / " +
            $"Codex 压缩线 {profile.AutoCompactTokenLimit:N0} / GPU Layers {profile.GpuLayers} / " +
            $"KV {profile.CacheTypeK}/{profile.CacheTypeV} / Parallel {profile.Parallel}\n" +
            $"该模型专用默认：{defaults}";
    }

    private static string FormatNativeModelDetails(LlamaModelRuntimeInfo model) =>
        $"状态：{TranslateModelStatus(model.Status)}\n" +
        $"来源：{model.Source} · 可删除缓存：{(model.CanRemove ? "是" : "否")}\n" +
        $"参数量：{FormatCount(model.ParameterCount)}\n" +
        $"训练上下文：{model.TrainingContextSize?.ToString("N0") ?? "未返回"}\n" +
        $"输入模态：{(model.InputModalities.Count > 0 ? string.Join("、", model.InputModalities) : "未返回")}";

    private static string FormatHardwareInventory(HardwareInventory? inventory, string llamaDevices)
    {
        if (inventory is null) return "硬件清单尚未读取。";
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

    private static string FormatPerformance(WindowsPerformanceSnapshot value)
    {
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
        "loaded" => "已加载",
        "loading" => "加载中",
        "sleeping" => "空闲休眠",
        "unloaded" => "已释放",
        "downloading" => "下载中",
        _ => value,
    };

    private static string FormatPercent(double? value) => value is null ? "不可用" : $"{value:0.0}%";

    private static string FormatRate(double? value) => value is null ? "速率未返回" : $"{value:0.0} token/s";

    private static string FormatCount(long? value) => value is null ? "未返回" : value.Value.ToString("N0");

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "大小未知";
        }

        const double gibibyte = 1024d * 1024d * 1024d;
        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.00} GiB"
            : $"{bytes / (1024d * 1024d):0.00} MiB";
    }

    private sealed record ModelListItem(
        string Summary,
        GgufModelCandidate? Candidate,
        ModelProfile? Profile)
    {
        public static ModelListItem FromCandidate(GgufModelCandidate candidate) => new(
            $"{candidate.DisplayName} · {FormatSize(candidate.TotalSizeBytes)}" +
            (candidate.IsSharded ? $" · {candidate.ShardCount} 分片" : string.Empty),
            candidate,
            null);

        public static ModelListItem FromProfile(ModelProfile profile) => new(
            $"{(profile.ContextSize is > 0 and < ModelProfile.RecommendedMinimumCodexContext ? "⚠ " : string.Empty)}" +
            $"{profile.DisplayName} · Context {profile.ContextSize:N0} · 安全余量 {profile.CompactionSafetyReserve:N0} · {profile.CacheTypeK}/{profile.CacheTypeV}",
            null,
            profile);
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
