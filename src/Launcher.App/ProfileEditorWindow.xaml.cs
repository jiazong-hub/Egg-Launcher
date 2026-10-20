using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Launcher.Models.Profiles;
using Launcher.Core.Configuration;
using Launcher.Models.Scanning;
using Launcher.Runtime.Detection;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace Launcher.App;

public partial class ProfileEditorWindow : Window
{
    private const int CustomContextSizeChoice = -1;
    private const string CustomGpuLayersChoice = "__custom__";
    private const int FallbackContextLimit = 128 * 1024;
    private const int ContextAlignment = 256;
    private bool _updatingConversationLimits;
    private bool _populatingConversationLimits;
    private int? _choiceContext;
    private int? _choiceReserve;

    private static readonly IReadOnlyList<Choice<int>> CompactionSafetyReserveChoices =
    [
        new("1", 1024),
        new("2", 2048),
        new("4", 4096),
        new("8", 8192),
        new("12", 12288),
        new("16", 16384),
        new("24", 24576),
        new("32", 32768),
    ];

    private readonly IReadOnlyList<Choice<int>> ToolOutputTokenLimitChoices =
    [
        new(AppLanguageManager.Choose("沿用 Codex 设置", "Inherit Codex setting"), 0),
        new("1", 1024), new("2", 2048), new("4", 4096),
        new("8", 8192), new("12", 12288), new("16", 16384), new("24", 24576),
    ];

    private readonly IReadOnlyList<Choice<string>> GpuLayerChoices =
    [
        new("Auto", "auto"),
        new("All", "all"),
        new("0", "0"),
        new(AppLanguageManager.Choose("自定义…", "Custom…"), CustomGpuLayersChoice),
    ];

    private readonly IReadOnlyList<Choice<string>> DefaultOnOffChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty),
        new(AppLanguageManager.Choose("开启", "On"), "on"),
        new(AppLanguageManager.Choose("关闭", "Off"), "off"),
    ];

    private readonly IReadOnlyList<Choice<string>> DefaultOnChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty),
        new(AppLanguageManager.Choose("开启", "On"), "on"),
    ];

    private readonly IReadOnlyList<Choice<string>> LoadModeChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty), new("Auto", "auto"), new("mmap", "mmap"),
        new("none", "none"), new("mlock", "mlock"), new("mmap + mlock", "mmap+mlock"),
        new("DirectIO", "dio"),
    ];

    private readonly IReadOnlyList<Choice<string>> LazyModeChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty), new("Auto", "auto"), new("On", "on"), new("Off", "off"),
    ];

    private readonly IReadOnlyList<Choice<string>> SplitModeChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty), new(AppLanguageManager.Choose("单 GPU", "Single GPU"), "none"), new(AppLanguageManager.Choose("按层", "Layer"), "layer"),
        new(AppLanguageManager.Choose("按行", "Row"), "row"), new(AppLanguageManager.Choose("按 Tensor（实验性）", "Tensor (Experimental)"), "tensor"),
    ];

    private readonly IReadOnlyList<Choice<string>> NumaChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty), new("Distribute", "distribute"),
        new("Isolate", "isolate"), new("numactl", "numactl"),
    ];

    private readonly IReadOnlyList<Choice<string>> RopeScalingChoices =
    [
        new(AppLanguageManager.Choose("默认", "Default"), string.Empty), new("None", "none"), new("Linear", "linear"), new("YaRN", "yarn"),
    ];

    private readonly IReadOnlyList<Choice<string>> MoePlacementChoices =
    [
        new(AppLanguageManager.Choose("默认（由 llama.cpp 决定）", "Default (llama.cpp decides)"), string.Empty),
        new(AppLanguageManager.Choose("尽可能使用 GPU（由 GPU Offload 控制）", "Prefer GPU (controlled by GPU Offload)"), "gpu"),
        new(AppLanguageManager.Choose("全部专家放在 CPU", "All experts on CPU"), "cpu-all"),
        new(AppLanguageManager.Choose("前 N 层专家放在 CPU", "First N expert layers on CPU"), "cpu-first"),
    ];

    private static readonly string[] ManagedAdvancedArgumentKeys =
    [
        "threads", "threads-batch", "kv-offload", "no-kv-offload", "load-mode", "lazy-mode",
        "fit", "fit-target", "fit-ctx", "n-cpu-ffn", "split-mode", "tensor-split", "main-gpu",
        "numa", "swa-full", "ctx-checkpoints", "swa-checkpoints", "repack", "no-repack", "op-offload", "no-op-offload", "no-host",
        "context-shift", "no-context-shift", "keep",
        "rope-scaling", "rope-scale", "rope-freq-base", "rope-freq-scale", "yarn-orig-ctx",
        "yarn-ext-factor", "yarn-attn-factor", "yarn-beta-slow", "yarn-beta-fast", "override-tensor",
        "cpu-moe", "n-cpu-moe",
    ];

    private static readonly IReadOnlyList<Choice<string>> FlashAttentionChoices =
    [
        new("Auto", "auto"),
        new("On", "on"),
        new("Off", "off"),
    ];

    private static readonly IReadOnlyList<Choice<string>> CacheTypeChoices =
    [
        new("F16", "f16"),
        new("BF16", "bf16"),
        new("Q8_0", "q8_0"),
        new("Q4_0", "q4_0"),
        new("Q4_1", "q4_1"),
        new("IQ4_NL", "iq4_nl"),
        new("Q5_0", "q5_0"),
        new("Q5_1", "q5_1"),
        new("F32", "f32"),
    ];

    private static readonly IReadOnlyList<Choice<int>> ParallelChoices =
    [
        new("1", 1),
        new("2", 2),
        new("4", 4),
    ];

    private static readonly IReadOnlyList<Choice<int>> BatchChoices =
    [
        new("Default", 0),
        new("128", 128),
        new("256", 256),
        new("512", 512),
        new("1,024", 1024),
        new("2,048", 2048),
        new("4,096", 4096),
        new("8,192", 8192),
        new("16,384", 16384),
    ];

    private static readonly IReadOnlyList<Choice<int>> MicroBatchChoices =
    [
        new("Default", 0),
        new("64", 64),
        new("128", 128),
        new("256", 256),
        new("512", 512),
        new("1,024", 1024),
        new("2,048", 2048),
        new("4,096", 4096),
        new("8,192", 8192),
    ];

    private readonly IReadOnlyList<Choice<int>> IdleSleepChoices =
    [
        new(AppLanguageManager.Choose("30 秒", "30 seconds"), 30),
        new(AppLanguageManager.Choose("1 分钟", "1 minute"), 60),
        new(AppLanguageManager.Choose("5 分钟", "5 minutes"), 300),
        new(AppLanguageManager.Choose("15 分钟", "15 minutes"), 900),
        new(AppLanguageManager.Choose("禁用", "Disabled"), -1),
    ];

    private readonly Func<ModelProfile, CancellationToken, Task<ContextShiftCapabilityResult>>? _contextShiftProbe;
    private CancellationTokenSource? _contextShiftCancellation;
    private bool _changingContextShift;
    private bool _populatingProfile;
    private ModelProfile? _probeProfile;
    private ContextShiftCapabilityResult? _shiftCapability;
    private readonly string _runtimeRoot;
    private ModelProfile _originalProfile;
    private readonly int? _modelContextLimit;
    private readonly IReadOnlyList<Choice<int>> _contextChoices;
    private readonly bool _hasEmbeddedMtpCandidate;
    private bool? _runtimeSupportsMtp;
    private bool? _runtimeSupportsExternalMtp;
    private bool? _runtimeSupportsContextCheckpoints;
    private bool? _runtimeSupportsContextShift;
    private bool? _runtimeSupportsKeep;
    private bool _isPopulatingMtp;
    private MtpCapabilityStatus _displayedMtpCapabilityStatus = MtpCapabilityStatus.Unknown;
    private bool? _runtimeSupportsVision;
    private bool _isPopulatingVision;
    private bool _isPopulatingSandbox;
    private bool _sandboxSettingsEdited;
    private readonly List<SandboxPathRow> _sandboxPathRows = [];

    public ProfileEditorWindow(
        ModelProfile profile,
        string runtimeRoot,
        IReadOnlySet<string>? runtimeCapabilities = null,
        Func<ModelProfile, CancellationToken, Task<ContextShiftCapabilityResult>>? contextShiftProbe = null,
        Func<ModelProfile, CancellationToken, Task<ModelProfile>>? templateProbe = null,
        Func<ModelProfile, CancellationToken, Task<ModelProfile>>? reasoningProbe = null,
        Func<ModelProfile, bool>? canEdit = null, Func<bool>? canDetect = null, Launcher.Core.Diagnostics.DiagnosticJournal? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (profile.ConfigurationError is not null || !profile.ConfigurationInitialized
            || string.IsNullOrWhiteSpace(profile.PreferredConfigurationPath) || !File.Exists(profile.PreferredConfigurationPath))
            profile = ModelConfigurationFile.CreateUnloadedDraft(profile);
        _diagnostics = diagnostics ?? Launcher.Core.Diagnostics.DiagnosticJournal.None;
        _canEditConfiguration = canEdit ?? (_ => true);
        _canDetectConfiguration = canDetect ?? (() => true);
        _contextShiftProbe = contextShiftProbe;
        _templateProbe = templateProbe;
        _reasoningProbe = reasoningProbe;
        _reasoningProfile = profile;
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        // Migrate a still-valid original proof before edits or Restore Defaults change resource parameters.
        profile = EnsureCurrentReasoningValidation(profile);
        _originalProfile = profile;
        _reasoningProfile = profile;
        _modelContextLimit = TryReadModelContextLimit(profile, _runtimeRoot);
        _hasEmbeddedMtpCandidate = TryReadModelMetadata(profile, _runtimeRoot)?.HasEmbeddedMtp == true;
        _contextChoices = BuildContextChoices(_modelContextLimit);
        InitializeComponent();
        CommonSandboxPathsButton.Content = AppLanguageManager.Choose("添加常用目录 ▾", "Add common directories ▾");
        CompactionSafetyReserveComboBox.AddHandler(WpfTextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateLongConversationSummary()));
        ToolOutputTokenLimitComboBox.AddHandler(WpfTextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateLongConversationSummary()));
        ToolOutputTokenLimitComboBox.SelectionChanged += (_, _) => UpdateLongConversationSummary();
        Closed += (_, _) => { _contextShiftCancellation?.Cancel(); _templateCancellation?.Cancel(); _reasoningCancellation?.Cancel(); };
        ProfileEditorTabs.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, e) => InvalidateShiftForParameterChange(e.OriginalSource)));
        ProfileEditorTabs.AddHandler(WpfTextBox.TextChangedEvent,
            new TextChangedEventHandler((_, e) => InvalidateShiftForParameterChange(e.OriginalSource)));
        ProfileEditorTabs.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, e) => InvalidateShiftForParameterChange(e.OriginalSource)));
        ProfileEditorTabs.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, e) => InvalidateShiftForParameterChange(e.OriginalSource)));
        UiMotion.AttachWindowEntrance(this);
        InitializeAdvancedChoices();
        InitializeMtpChoices();
        InitializeNgramControls();
        InitializeAdditionalParameterControls();
        VisionOffloadComboBox.ItemsSource = DefaultOnOffChoices;
        SourceInitialized += (_, _) =>
            AdaptiveWindowSizing.FitDialog(this, 820, 720, 640, 480);
        UpdatedProfile = profile;
        RestoreModelDefaultsButton.IsEnabled = profile.DefaultParameters is not null;
        Populate(profile);
        ApplyRuntimeCapabilities(runtimeCapabilities);
        UpdateTemplateStatus();
        InitializeConfigurationFiles();
    }

    public ModelProfile UpdatedProfile { get; private set; }

    public bool SavedAsModelDefault { get; private set; }

    private async void ProfileEditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded
            || !ReferenceEquals(e.Source, ProfileEditorTabs)
            || ProfileEditorTabs.SelectedItem is not TabItem { Content: FrameworkElement content })
        {
            return;
        }

        await UiMotion.AnimateEntranceAsync(
            content,
            offsetY: 5,
            durationMilliseconds: UiMotion.StandardMilliseconds);
    }

    private void RestoreModelDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = ModelProfileFactory.RestoreModelDefaults(_originalProfile);
        Populate(defaults, populateSandbox: false);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await PersistConfigurationAsync(saveAs: false, apply: false)) return;
        _allowConfigurationClose = true;
        Close();
    }

    private async void SaveAsModelDefaultButton_Click(object sender, RoutedEventArgs e)
        => await PersistConfigurationAsync(saveAs: true, apply: false);

    private void ContextSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContextRiskTextBlock is null || CustomContextSizeTextBox is null)
        {
            return;
        }

        var isCustom = ContextSizeComboBox.SelectedValue is CustomContextSizeChoice;
        CustomContextSizeTextBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        UpdateContextRisk();
        UpdateLongConversationSummary();
    }

    private void CustomContextSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateContextRisk();
        UpdateLongConversationSummary();
    }

    private void CompactionSafetyReserveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLongConversationSummary();

    private void GpuLayersComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomGpuLayersTextBox is not null)
        {
            CustomGpuLayersTextBox.Visibility = GpuLayersComboBox.SelectedValue as string == CustomGpuLayersChoice
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void MoePlacementComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CpuMoeLayersTextBox is not null)
        {
            CpuMoeLayersTextBox.IsEnabled = MoePlacementComboBox.SelectedValue as string == "cpu-first";
        }
    }

    private void MtpEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        MarkMtpConfigurationEdited();
        UpdateMtpUiState();
    }

    private void MtpParameter_Changed(object sender, RoutedEventArgs e)
    {
        MarkMtpConfigurationEdited();
        UpdateMtpUiState();
    }

    private void VisionEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateVisionUiState();
        UpdateContextShiftUiState();
    }

    private void InvalidateShiftForParameterChange(object source)
    {
        if (!IsLoaded || _populatingProfile || _changingContextShift || _contextShiftCancellation is not null) return;
        // Sandbox controls do not change llama.cpp context-shift capability.
        // Exclude the entire tab, including dynamically generated path controls.
        for (var ancestor = source as FrameworkElement; ancestor is not null;
             ancestor = ancestor.Parent as FrameworkElement
                 ?? System.Windows.Media.VisualTreeHelper.GetParent(ancestor) as FrameworkElement)
            if (ReferenceEquals(ancestor, SandboxSettingsTab) || ReferenceEquals(ancestor, CustomParametersTab)
                || ReferenceEquals(ancestor, TemplateSettingsTab) || ReferenceEquals(ancestor, ThinkingSettingsTab)
                || ReferenceEquals(ancestor, VisionSettingsTab) || ReferenceEquals(ancestor, NgramRoot)) return;
        if (source is System.Windows.Controls.Primitives.ToggleButton && source is not WpfCheckBox) return;
        var element = source as FrameworkElement;
        while (element?.TemplatedParent is FrameworkElement owner) element = owner;
        while (element is not null && (string.IsNullOrWhiteSpace(element.Name) || element.Name.StartsWith("PART_", StringComparison.Ordinal)))
            element = System.Windows.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element is null || element.Name is "ContextShiftCheckBox" or "ProfileEditorTabs"
            or "CompactionSafetyReserveComboBox" or "ToolOutputTokenLimitComboBox" or "CodexStreamIdleTimeoutComboBox"
            or "ThinkingEnabledCheckBox" or "ShowThinkingProcessCheckBox" or "ReasoningEffortCheckBox" or "ReverseReasoningOrderCheckBox" or "PreferredReasoningLevelComboBox") return;
        if (element.Name == "IdleSleepSecondsComboBox") return;
        if (element.Name.StartsWith("Mtp", StringComparison.Ordinal) && element.Name != "MtpEnableCheckBox" && MtpEnableCheckBox.IsChecked != true) return;
        if (_runtimeInputs.Any(pair => ReferenceEquals(pair.Value, source)
            && (pair.Key is "cache-ram" or "cache-prompt" or "cache-idle-slots" or "backend-sampling"
                || pair.Key.StartsWith("spec-draft-", StringComparison.Ordinal) && MtpEnableCheckBox.IsChecked != true))) return;
        // Capability evidence can expire without changing the user's requested switch.
        _shiftCapability = null;
        UpdateContextShiftUiState();
    }

    private async void ContextShiftCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingContextShift || !IsLoaded) return;
        if (ContextShiftCheckBox.IsChecked != true) { UpdateContextShiftUiState(); return; }
        _changingContextShift = true;
        ContextShiftCheckBox.IsChecked = false;
        _changingContextShift = false;
        await DetectContextShiftAsync(force: false);
    }

    private async void ContextShiftDetectButton_Click(object sender, RoutedEventArgs e) => await DetectContextShiftAsync(force: true);
    private void ContextShiftCancelButton_Click(object sender, RoutedEventArgs e) => _contextShiftCancellation?.Cancel();

    private async Task DetectContextShiftAsync(bool force)
    {
        if (_contextShiftCancellation is not null || !_canDetectConfiguration()) return;
        _changingContextShift = true;
        ContextShiftCheckBox.IsChecked = false;
        _changingContextShift = false;
        if (!TryBuildContextShiftCandidate(out var candidate)) return;
        _shiftCapability = force ? null : ContextShiftCapabilityCache.Read(candidate, _runtimeRoot);
        if (_shiftCapability?.Supported is not null)
        {
            _changingContextShift = true;
            ContextShiftCheckBox.IsChecked = _shiftCapability.Supported == true;
            _changingContextShift = false;
            UpdateContextShiftUiState();
            return;
        }
        if (_contextShiftProbe is null || _runtimeSupportsContextShift != true || candidate.VisionEnabled)
        {
            _shiftCapability = new(false, AppLanguageManager.Choose("当前 Runtime 或视觉配置不支持滚动。", "The runtime or vision configuration does not support shifting."), DateTimeOffset.UtcNow);
            UpdateContextShiftUiState();
            return;
        }
        _contextShiftCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        ProfileEditorTabs.IsEnabled = false;
        SaveProfileButton.IsEnabled = SaveModelDefaultButton.IsEnabled = RestoreModelDefaultsButton.IsEnabled = false;
        ApplyConfigurationButton.IsEnabled = LoadConfigurationButton.IsEnabled = false;
        ContextShiftCancelButton.Visibility = Visibility.Visible;
        UpdateContextShiftUiState();
        try
        {
            _shiftCapability = await _contextShiftProbe(candidate, _contextShiftCancellation.Token);
            _changingContextShift = true;
            ContextShiftCheckBox.IsChecked = _shiftCapability.Supported == true;
            _changingContextShift = false;
        }
        catch (OperationCanceledException)
        {
            _shiftCapability = new(null, AppLanguageManager.Choose("检测已取消或超时；滚动保持关闭。", "Detection canceled or timed out; shifting remains off."), DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            _shiftCapability = ContextShiftCapabilityCache.Read(candidate, _runtimeRoot) is { Supported: false } disabled
                ? disabled : new(null, exception.Message, DateTimeOffset.UtcNow);
        }
        finally
        {
            _contextShiftCancellation.Dispose();
            _contextShiftCancellation = null;
            ProfileEditorTabs.IsEnabled = true;
            ApplyConfigurationButton.IsEnabled = LoadConfigurationButton.IsEnabled = true;
            UpdateLongConversationSummary();
            RestoreModelDefaultsButton.IsEnabled = _originalProfile.DefaultParameters is not null;
            ContextShiftCancelButton.Visibility = Visibility.Collapsed;
            UpdateContextShiftUiState();
        }
    }

    private void KeepTokensTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateKeepUiState();

    private void VisionParameter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isPopulatingVision)
        {
            UpdateVisionUiState();
        }
    }

    private void AddSandboxPathButton_Click(object sender, RoutedEventArgs e) => AddSandboxPathRow(markEdited: true);

    private void CompleteSave(bool saveAsModelDefault, bool captureForProbe = false)
    {
        var contextShiftEnabled = ContextShiftCheckBox.IsChecked == true;
        var visionEnabled = VisionEnableCheckBox.IsChecked == true;
        if (visionEnabled && contextShiftEnabled)
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Text("ContextShiftVisionConflictError"),
                AppLanguageManager.Text("ContextShiftErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryGetConversationLimits(out var reserve, out var toolLimit, out var limitsError))
        {
            MessageBox.Show(this, limitsError, AppLanguageManager.Choose("长对话参数无效", "Invalid Conversation Limits"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryGetCodexStreamIdleTimeout(out var streamIdleTimeoutMinutes))
        {
            MessageBox.Show(this, AppLanguageManager.Choose(
                "SSE 空闲等待时间请输入 5 分钟的正整数倍，或选择沿用 Codex 默认。",
                "Enter a positive multiple of 5 minutes, or inherit the Codex default."),
                AppLanguageManager.Text("CodexStreamIdleTimeout"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryGetSelectedContextSize(out var contextSize, out var contextError))
        {
            MessageBox.Show(
                this,
                contextError,
                AppLanguageManager.Choose("Context 参数无效", "Invalid Context Parameter"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryGetGpuLayers(out var gpuLayers, out var gpuLayersError))
        {
            MessageBox.Show(this, gpuLayersError, AppLanguageManager.Choose("GPU Offload 参数无效", "Invalid GPU Offload Parameter"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetBatchSizes(out var batchSize, out var microBatchSize, out var batchError))
        {
            MessageBox.Show(this, batchError, AppLanguageManager.Choose("批处理参数无效", "Invalid Batch Parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryBuildAdvancedArguments(out var extraArguments, out var advancedError))
        {
            MessageBox.Show(this, advancedError, AppLanguageManager.Choose("高级参数无效", "Invalid Advanced Parameter"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetMoePlacement(out var moePlacement, out var cpuMoeLayers, out var moeError))
        {
            MessageBox.Show(this, moeError, AppLanguageManager.Choose("MoE 参数无效", "Invalid MoE Parameter"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryBuildMtpSettings(out var mtp, out var mtpError))
        {
            MessageBox.Show(this, mtpError, AppLanguageManager.Choose("MTP 参数无效", "Invalid MTP Parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryBuildAdditionalParameters(out var runtimeParameters, out var runtimeError))
        {
            MessageBox.Show(this, runtimeError, AppLanguageManager.Choose("参数无效", "Invalid parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryBuildNgramSettings(out var ngram, out var ngramError))
        {
            MessageBox.Show(this, ngramError, AppLanguageManager.Choose("n-gram 参数无效", "Invalid n-gram Parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryBuildVisionSettings(out var vision, out var visionError))
        {
            MessageBox.Show(this, visionError, AppLanguageManager.Choose("视觉参数无效", "Invalid Vision Parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryBuildSandboxSettings(out var sandboxSettings, out var sandboxError))
        {
            MessageBox.Show(
                this,
                sandboxError,
                AppLanguageManager.Choose("沙箱设置无效", "Invalid Sandbox Settings"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!captureForProbe && mtp.Enabled
            && SelectedValue<int>(ParallelComboBox) > 1
            && MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    "当前并发槽位大于 1。MTP 在部分 llama.cpp 版本或 GPU 后端上的多槽位兼容性仍可能有限。是否保留当前设置？",
                    "Parallel Slots is greater than 1. Multi-slot MTP compatibility can still be limited in some llama.cpp versions or GPU backends. Keep this setting?"),
                AppLanguageManager.Choose("确认 MTP 并发设置", "Confirm MTP Concurrency"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!captureForProbe) NormalizePreferredReasoningLevelForSave();
        var updated = _originalProfile with
        {
            SchemaVersion = ModelProfile.CurrentSchemaVersion,
            ContextSize = contextSize,
            CompactionSafetyReserve = reserve,
            ToolOutputTokenLimit = toolLimit,
            CodexStreamIdleTimeoutMinutes = streamIdleTimeoutMinutes,
            GpuLayers = gpuLayers,
            Device = NullWhenWhiteSpace(DeviceTextBox.Text),
            MoeExpertPlacement = moePlacement,
            CpuMoeLayers = cpuMoeLayers,
            FlashAttention = SelectedValue<string>(FlashAttentionComboBox),
            CacheTypeK = SelectedValue<string>(CacheTypeKComboBox),
            CacheTypeV = SelectedValue<string>(CacheTypeVComboBox),
            Parallel = SelectedValue<int>(ParallelComboBox),
            BatchSize = batchSize,
            MicroBatchSize = microBatchSize,
            IdleSleepSeconds = SelectedValue<int>(IdleSleepSecondsComboBox),
            Jinja = JinjaCheckBox.IsChecked == true,
            ChatTemplateRelativePath = NullWhenWhiteSpace(ChatTemplatePathTextBox.Text),
            ExposeReasoningEffortInChatGpt = ReasoningEffortCheckBox.IsChecked == true,
            ReverseReasoningLevelDisplayOrder = ReverseReasoningOrderCheckBox.IsChecked == true,
            ThinkingEnabled = _reasoningProfile.ThinkingEnabled,
            ShowThinkingProcess = ShowThinkingProcessCheckBox.IsChecked == true,
            SupportsThinkingSwitch = _reasoningProfile.SupportsThinkingSwitch,
            DefaultThinkingEnabled = _reasoningProfile.DefaultThinkingEnabled,
            ReasoningCapabilityStatus = _reasoningProfile.ReasoningCapabilityStatus,
            SupportedReasoningLevels = _reasoningProfile.SupportedReasoningLevels,
            DefaultReasoningLevel = _reasoningProfile.DefaultReasoningLevel,
            PreferredReasoningLevel = _reasoningProfile.PreferredReasoningLevel,
            ReasoningLevelAliases = _reasoningProfile.ReasoningLevelAliases,
            ReasoningResponsesVerified = _reasoningProfile.ReasoningResponsesVerified,
            ReasoningClientCompatible = _reasoningProfile.ReasoningClientCompatible,
            ReasoningClientExecutablePath = _reasoningProfile.ReasoningClientExecutablePath,
            ReasoningValidationDetails = _reasoningProfile.ReasoningValidationDetails,
            ReasoningCapabilitySignature = _reasoningProfile.ReasoningCapabilitySignature,
            ReasoningValidationBasis = _reasoningProfile.ReasoningValidationBasis,
            ReasoningCapabilityCheckedAtUtc = _reasoningProfile.ReasoningCapabilityCheckedAtUtc,
            ContextShiftEnabled = contextShiftEnabled,
            MtpEnabled = mtp.Enabled,
            Ngram = ngram,
            RuntimeParameters = runtimeParameters,
            CustomParameters = ReadCustomParameters(),
            MtpSource = mtp.Source,
            MtpCapabilityStatus = mtp.CapabilityStatus,
            MtpDraftModelRelativePath = mtp.DraftModelRelativePath,
            MtpDraftMaxTokens = mtp.DraftMaxTokens,
            MtpDraftMinTokens = mtp.DraftMinTokens,
            MtpDraftMinimumProbability = mtp.DraftMinimumProbability,
            MtpDraftSplitProbability = mtp.DraftSplitProbability,
            MtpBackendSampling = mtp.BackendSampling,
            MtpDraftGpuLayers = mtp.DraftGpuLayers,
            MtpDraftDevice = mtp.DraftDevice,
            MtpDraftCacheTypeK = mtp.DraftCacheTypeK,
            MtpDraftCacheTypeV = mtp.DraftCacheTypeV,
            MtpDraftThreads = mtp.DraftThreads,
            MtpDraftBatchThreads = mtp.DraftBatchThreads,
            MtpValidationSignature = mtp.KeepValidation ? _originalProfile.MtpValidationSignature : null,
            MtpValidatedAtUtc = mtp.KeepValidation ? _originalProfile.MtpValidatedAtUtc : null,
            VisionEnabled = vision.Enabled,
            VisionProjectorOffload = vision.ProjectorOffload,
            VisionProjectorDevice = vision.ProjectorDevice,
            VisionImageMinTokens = vision.ImageMinTokens,
            VisionImageMaxTokens = vision.ImageMaxTokens,
            VisionBatchMaxTokens = vision.BatchMaxTokens,
            SandboxSettings = sandboxSettings,
            ExtraArguments = extraArguments,
        };
        updated = Launcher.Scripts.Templates.ModelResourceCatalog.Resolve(updated, _runtimeRoot);
        updated = EnsureCurrentReasoningValidation(updated);
        var errors = ModelProfileValidator.Validate(updated, _runtimeRoot, requireRuntimeEvidence: false);
        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, errors),
                AppLanguageManager.Choose("Profile 参数无效", "Invalid Profile Parameters"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (captureForProbe) { _probeProfile = updated; return; }
        if (saveAsModelDefault)
        {
            var answer = MessageBox.Show(
                this,
                AppLanguageManager.Choose(
                    $"把当前参数保存为“{updated.DisplayName}”的专用默认值？\n\n以后只有该模型执行“恢复此模型默认”时会使用；其他模型不会继承或改变。",
                    $"Save the current parameters as dedicated defaults for \"{updated.DisplayName}\"?\n\nOnly Restore Model Defaults for this model will use them. Other models will not inherit or change."),
                AppLanguageManager.Choose("设置此模型的默认参数", "Set Model Defaults"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            updated = ModelProfileFactory.SaveCurrentParametersAsDefault(updated);
        }

        UpdatedProfile = updated;
        SavedAsModelDefault = saveAsModelDefault;
        DialogResult = true;
    }

    private void Populate(ModelProfile profile, bool populateSandbox = true)
    {
        _populatingConversationLimits = true;
        _populatingProfile = true;
        MoeBasicPanel.Visibility = profile.ModelType == ModelType.MoE ? Visibility.Visible : Visibility.Collapsed;
        DenseLoadModePanel.Visibility = profile.ModelType == ModelType.Dense ? Visibility.Visible : Visibility.Collapsed;
        DenseFitPanel.Visibility = profile.ModelType == ModelType.Dense ? Visibility.Visible : Visibility.Collapsed;
        DenseCpuFfnPanel.Visibility = profile.ModelType == ModelType.Dense ? Visibility.Visible : Visibility.Collapsed;

        ContextSizeComboBox.ItemsSource = _contextChoices;
        if (_contextChoices.Any(choice => choice.Value == profile.ContextSize))
        {
            ContextSizeComboBox.SelectedValue = profile.ContextSize;
            CustomContextSizeTextBox.Text = string.Empty;
        }
        else
        {
            ContextSizeComboBox.SelectedValue = CustomContextSizeChoice;
            CustomContextSizeTextBox.Text = FormatKInput(profile.ContextSize);
        }

        ContextLimitTextBlock.Text = _modelContextLimit is int contextLimit
            ? AppLanguageManager.Choose(
                $"模型声明上限：{FormatContextSize(contextLimit)}（{contextLimit:N0} tokens）",
                $"Declared model limit: {FormatContextSize(contextLimit)} ({contextLimit:N0} tokens)")
            : AppLanguageManager.Choose(
                "未能读取模型上限；预设暂显示到 128K，自定义值请以模型说明为准。",
                "The model limit could not be read. Presets are shown up to 128K; verify custom values against the model documentation.");
        PopulateChoices(
            CompactionSafetyReserveComboBox,
            CompactionSafetyReserveChoices,
            profile.CompactionSafetyReserve,
            FormatKInput(profile.CompactionSafetyReserve));
        PopulateChoices(
            ToolOutputTokenLimitComboBox,
            ToolOutputTokenLimitChoices,
            profile.ToolOutputTokenLimit ?? 0,
            profile.ToolOutputTokenLimit is int limit ? FormatKInput(limit) : AppLanguageManager.Choose("沿用 Codex 设置", "Inherit Codex setting"));
        CompactionSafetyReserveComboBox.Text = FormatKInput(profile.CompactionSafetyReserve);
        var timeoutChoices = new List<Choice<int>>
        {
            new(AppLanguageManager.Text("CodexStreamIdleTimeoutDefault"), 0),
        };
        timeoutChoices.AddRange(Enumerable.Range(1, 24).Select(index =>
            new Choice<int>((index * 5).ToString(CultureInfo.CurrentCulture), index * 5)));
        PopulateChoices(CodexStreamIdleTimeoutComboBox, timeoutChoices,
            profile.CodexStreamIdleTimeoutMinutes ?? 0,
            profile.CodexStreamIdleTimeoutMinutes?.ToString(CultureInfo.CurrentCulture)
                ?? AppLanguageManager.Text("CodexStreamIdleTimeoutDefault"));
        CodexStreamIdleTimeoutComboBox.Text = profile.CodexStreamIdleTimeoutMinutes?.ToString(CultureInfo.CurrentCulture)
            ?? AppLanguageManager.Text("CodexStreamIdleTimeoutDefault");
        ToolOutputTokenLimitComboBox.Text = profile.ToolOutputTokenLimit is int storedLimit
            ? FormatKInput(storedLimit) : AppLanguageManager.Choose("沿用 Codex 设置", "Inherit Codex setting");
        _choiceContext = _choiceReserve = null;
        _shiftCapability = ContextShiftCapabilityCache.Read(profile, _runtimeRoot);
        _probeProfile = profile;
        _changingContextShift = true;
        ContextShiftCheckBox.IsChecked = profile.ContextShiftEnabled;
        _changingContextShift = false;
        UpdateContextShiftUiState();
        if (GpuLayerChoices.Any(choice => choice.Value == profile.GpuLayers))
        {
            GpuLayersComboBox.ItemsSource = GpuLayerChoices;
            GpuLayersComboBox.SelectedValue = profile.GpuLayers;
            CustomGpuLayersTextBox.Text = string.Empty;
        }
        else
        {
            GpuLayersComboBox.ItemsSource = GpuLayerChoices;
            GpuLayersComboBox.SelectedValue = CustomGpuLayersChoice;
            CustomGpuLayersTextBox.Text = profile.GpuLayers;
        }
        PopulateChoices(FlashAttentionComboBox, FlashAttentionChoices, profile.FlashAttention, profile.FlashAttention);
        PopulateChoices(CacheTypeKComboBox, CacheTypeChoices, profile.CacheTypeK, profile.CacheTypeK);
        PopulateChoices(CacheTypeVComboBox, CacheTypeChoices, profile.CacheTypeV, profile.CacheTypeV);
        PopulateChoices(ParallelComboBox, ParallelChoices, profile.Parallel, profile.Parallel.ToString());
        PopulateBatchChoices(BatchSizeComboBox, BatchChoices, profile.BatchSize);
        PopulateBatchChoices(MicroBatchSizeComboBox, MicroBatchChoices, profile.MicroBatchSize);
        PopulateChoices(
            IdleSleepSecondsComboBox,
            IdleSleepChoices,
            profile.IdleSleepSeconds,
            profile.IdleSleepSeconds == -1
                ? AppLanguageManager.Choose("禁用", "Disabled")
                : AppLanguageManager.Choose($"{profile.IdleSleepSeconds:N0} 秒", $"{profile.IdleSleepSeconds:N0} seconds"));
        JinjaCheckBox.IsChecked = profile.Jinja;
        ChatTemplatePathTextBox.Text = profile.ChatTemplateRelativePath ?? string.Empty;
        PopulateReasoningCapability(profile);
        PopulateVision(profile);
        PopulateMtp(profile);
        PopulateNgram(profile.Ngram);
        PopulateAdditionalParameters(profile);
        PopulateAdvanced(profile);
        if (populateSandbox)
        {
            PopulateSandbox(profile);
        }

        UpdateContextRisk();
        _populatingConversationLimits = false;
        UpdateLongConversationSummary();
        _populatingProfile = false;
    }

    private void PopulateSandbox(ModelProfile profile)
    {
        _isPopulatingSandbox = true;
        SandboxNetworkCheckBox.IsChecked = profile.SandboxSettings?.NetworkAccess switch
        {
            SandboxNetworkAccess.Full => true,
            SandboxNetworkAccess.Disabled => false,
            _ => null,
        };
        SandboxCompatibilityCheckBox.IsChecked = profile.SandboxSettings?.NetworkCompatibilityEnabled == true;
        UpdateSandboxNetworkState();
        SandboxPathsPanel.Children.Clear();
        _sandboxPathRows.Clear();
        foreach (var permission in profile.SandboxSettings?.AdditionalPaths ?? Array.Empty<SandboxPathPermission>())
        {
            AddSandboxPathRow(permission, markEdited: false);
        }

        _isPopulatingSandbox = false;
    }

    private SandboxPathRow AddSandboxPathRow(
        SandboxPathPermission? permission = null,
        bool markEdited = false)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pathTextBox = new WpfTextBox
        {
            Text = permission?.Path ?? string.Empty,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = AppLanguageManager.Text("ParameterHelp_SandboxPath"),
        };
        pathTextBox.TextChanged += (_, _) => MarkSandboxSettingsEdited();
        row.Children.Add(pathTextBox);

        var allowWriteCheckBox = new WpfCheckBox
        {
            Content = AppLanguageManager.Choose("允许写入", "Allow write"),
            IsChecked = permission?.AllowWrite == true,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = AppLanguageManager.Text("ParameterHelp_SandboxWrite"),
        };
        allowWriteCheckBox.Checked += (_, _) => MarkSandboxSettingsEdited();
        allowWriteCheckBox.Unchecked += (_, _) => MarkSandboxSettingsEdited();
        Grid.SetColumn(allowWriteCheckBox, 1);
        row.Children.Add(allowWriteCheckBox);

        var browseButton = new Button
        {
            Content = AppLanguageManager.Choose("浏览…", "Browse…"),
            MinHeight = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        browseButton.Click += (_, _) => BrowseSandboxPath(pathTextBox);
        Grid.SetColumn(browseButton, 2);
        row.Children.Add(browseButton);

        var removeButton = new Button
        {
            Content = AppLanguageManager.Choose("移除", "Remove"),
            MinHeight = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 6, 10, 6),
        };
        var gradleCheckBox = new WpfCheckBox
        {
            ToolTip = AppLanguageManager.Text("ParameterHelp_SandboxGradle"),
            Content = AppLanguageManager.Choose("同时设为 Gradle 用户目录", "Also use as Gradle user home"),
            IsChecked = permission?.UseAsGradleUserHome == true,
            IsEnabled = allowWriteCheckBox.IsChecked == true,
        };
        var replaceGradleCheckBox = new WpfCheckBox
        {
            ToolTip = AppLanguageManager.Text("ParameterHelp_SandboxGradleReplace"),
            Content = AppLanguageManager.Choose("允许替换已有 Gradle 用户目录（仅本地模式）", "Allow replacing existing Gradle user home (Local mode only)"),
            IsChecked = permission?.ReplaceExistingGradleUserHome == true,
            IsEnabled = gradleCheckBox.IsChecked == true,
            Margin = new Thickness(0, 6, 0, 0),
        };
        ToolTipService.SetShowOnDisabled(pathTextBox, true);
        ToolTipService.SetShowOnDisabled(allowWriteCheckBox, true);
        ToolTipService.SetShowOnDisabled(gradleCheckBox, true);
        ToolTipService.SetShowOnDisabled(replaceGradleCheckBox, true);
        var isGradleDirectory = permission?.IsGradleDirectory == true || permission?.UseAsGradleUserHome == true;
        gradleCheckBox.Checked += (_, _) => { replaceGradleCheckBox.IsEnabled = true; MarkSandboxSettingsEdited(); };
        gradleCheckBox.Unchecked += (_, _) => { replaceGradleCheckBox.IsChecked = false; replaceGradleCheckBox.IsEnabled = false; MarkSandboxSettingsEdited(); };
        replaceGradleCheckBox.Checked += (_, _) => MarkSandboxSettingsEdited();
        replaceGradleCheckBox.Unchecked += (_, _) => MarkSandboxSettingsEdited();
        allowWriteCheckBox.Checked += (_, _) => gradleCheckBox.IsEnabled = true;
        allowWriteCheckBox.Unchecked += (_, _) =>
        {
            if (gradleCheckBox.IsChecked == true && !_isPopulatingSandbox)
                System.Windows.MessageBox.Show(this, AppLanguageManager.Choose("改为只读后，已取消 Gradle 用户目录配置。", "Gradle user home configuration was disabled when access became read-only."));
            gradleCheckBox.IsChecked = false;
            gradleCheckBox.IsEnabled = false;
        };
        var pathRow = new SandboxPathRow(row, pathTextBox, allowWriteCheckBox, gradleCheckBox, replaceGradleCheckBox, isGradleDirectory);
        if (isGradleDirectory) AddGradleDirectoryControls(pathRow);
        removeButton.Click += (_, _) =>
        {
            if (gradleCheckBox.IsChecked == true)
                System.Windows.MessageBox.Show(this, AppLanguageManager.Choose("移除此路径将同时取消 Gradle 用户目录配置；保存并重新启动后生效。", "Removing this path also disables Gradle user home configuration; save and restart to apply."));
            SandboxPathsPanel.Children.Remove(row);
            _sandboxPathRows.RemoveAll(candidate => ReferenceEquals(candidate.Container, row));
            MarkSandboxSettingsEdited();
        };
        Grid.SetColumn(removeButton, 3);
        row.Children.Add(removeButton);

        SandboxPathsPanel.Children.Add(row);
        _sandboxPathRows.Add(pathRow);
        if (markEdited)
        {
            MarkSandboxSettingsEdited();
        }

        return pathRow;
    }

    private static void AddGradleDirectoryControls(SandboxPathRow pathRow)
    {
        var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        options.Children.Add(pathRow.GradleCheckBox);
        options.Children.Add(pathRow.ReplaceGradleCheckBox);
        options.Children.Add(new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0),
            Text = AppLanguageManager.Choose("用于缓存、用户配置及初始化脚本。保存后重启客户端，并新建聊天。", "Used for caches, user configuration and init scripts. Save, restart the client and start a new chat.")
        });
        Grid.SetRow(options, 1);
        Grid.SetColumnSpan(options, 4);
        pathRow.Container.Children.Add(options);
    }

    private void BrowseSandboxPath(WpfTextBox pathTextBox)
    {
        var initialDirectory = Directory.Exists(pathTextBox.Text) ? pathTextBox.Text : null;
        var dialog = new OpenFolderDialog
        {
            Title = AppLanguageManager.Choose("选择沙箱路径", "Select Sandbox Path"),
            Multiselect = false,
            InitialDirectory = initialDirectory,
        };
        if (dialog.ShowDialog(this) == true)
        {
            pathTextBox.Text = dialog.FolderName;
        }
    }

    private void MarkSandboxSettingsEdited()
    {
        if (!_isPopulatingSandbox)
        {
            _sandboxSettingsEdited = true;
        }
    }

    private bool TryBuildSandboxSettings(out ModelSandboxSettings? settings, out string error)
    {
        if (!_sandboxSettingsEdited)
        {
            settings = _originalProfile.SandboxSettings;
            error = string.Empty;
            return true;
        }

        var networkAccess = CurrentSandboxNetworkAccess;
        var paths = _sandboxPathRows
            .Where(row => !string.IsNullOrWhiteSpace(row.PathTextBox.Text))
            .Select(row => new SandboxPathPermission
            {
                Path = row.PathTextBox.Text.Trim(),
                AllowWrite = row.AllowWriteCheckBox.IsChecked == true,
                IsGradleDirectory = row.IsGradleDirectory,
                UseAsGradleUserHome = row.GradleCheckBox.IsChecked == true,
                ReplaceExistingGradleUserHome = row.ReplaceGradleCheckBox.IsChecked == true && row.GradleCheckBox.IsChecked == true,
            })
            .ToArray();

        if (networkAccess == SandboxNetworkAccess.InheritCodexSettings && paths.Length == 0
            && SandboxCompatibilityCheckBox.IsChecked != true)
        {
            settings = null;
            error = string.Empty;
            return true;
        }

        settings = new ModelSandboxSettings
        {
            NetworkAccess = networkAccess,
            NetworkCompatibilityEnabled = SandboxCompatibilityCheckBox.IsChecked == true,
            AdditionalPaths = paths,
        };
        var validationErrors = ModelSandboxSettingsValidator.Validate(settings);
        if (validationErrors.Count > 0)
        {
            error = string.Join(Environment.NewLine, validationErrors);
            return false;
        }

        settings = settings with
        {
            AdditionalPaths = paths.Select(permission => permission with
            {
                Path = Path.GetFullPath(permission.Path),
            }).ToArray(),
        };
        error = string.Empty;
        return true;
    }

    private void InitializeAdvancedChoices()
    {
        KvOffloadComboBox.ItemsSource = DefaultOnOffChoices;
        DenseLoadModeComboBox.ItemsSource = LoadModeChoices;
        MoeLoadModeComboBox.ItemsSource = LoadModeChoices;
        LazyModeComboBox.ItemsSource = LazyModeChoices;
        DenseFitComboBox.ItemsSource = DefaultOnOffChoices;
        MoeFitComboBox.ItemsSource = DefaultOnOffChoices;
        SplitModeComboBox.ItemsSource = SplitModeChoices;
        NumaComboBox.ItemsSource = NumaChoices;
        SwaFullComboBox.ItemsSource = DefaultOnChoices;
        RepackComboBox.ItemsSource = DefaultOnOffChoices;
        OpOffloadComboBox.ItemsSource = DefaultOnOffChoices;
        NoHostComboBox.ItemsSource = DefaultOnChoices;
        RopeScalingComboBox.ItemsSource = RopeScalingChoices;
        MoePlacementComboBox.ItemsSource = MoePlacementChoices;
    }

    private void InitializeMtpChoices()
    {
        MtpBackendSamplingComboBox.ItemsSource = DefaultOnOffChoices;
        var cacheChoices = new[]
            {
                new Choice<string>(AppLanguageManager.Choose("默认", "Default"), string.Empty),
            }
            .Concat(CacheTypeChoices)
            .ToArray();
        MtpCacheTypeKComboBox.ItemsSource = cacheChoices;
        MtpCacheTypeVComboBox.ItemsSource = cacheChoices;
    }

    private void ApplyRuntimeCapabilities(IReadOnlySet<string>? capabilities)
    {
        _ngramRuntimeCapabilities = capabilities;
        _additionalRuntimeCapabilities = capabilities;
        UpdateAdditionalCapabilities();
        UpdateNgramUiState();
        if (capabilities is null)
        {
            _runtimeSupportsMtp = null;
            _runtimeSupportsExternalMtp = null;
            _runtimeSupportsContextCheckpoints = null;
            _runtimeSupportsContextShift = null;
            _runtimeSupportsKeep = null;
            UpdateContextCheckpointsUiState();
            UpdateContextShiftUiState();
            UpdateKeepUiState();
            _runtimeSupportsVision = null;
            UpdateMtpUiState();
            UpdateVisionUiState();
            return;
        }

        _runtimeSupportsMtp = capabilities.Contains("spec-type");
        _runtimeSupportsExternalMtp = capabilities.Contains("spec-draft-model");
        _runtimeSupportsContextCheckpoints = capabilities.Contains("ctx-checkpoints")
            || capabilities.Contains("swa-checkpoints");
        _runtimeSupportsContextShift = capabilities.Contains("context-shift");
        _runtimeSupportsKeep = capabilities.Contains("keep");
        _runtimeSupportsVision = capabilities.Contains("mmproj");

        UpdateContextCheckpointsUiState();
        UpdateContextShiftUiState();
        UpdateKeepUiState();

        foreach (var (control, option) in new (FrameworkElement, string)[]
                 {
                     (DeviceTextBox, "device"), (ThreadsTextBox, "threads"),
                     (ThreadsBatchTextBox, "threads-batch"), (KvOffloadComboBox, "kv-offload"),
                     (DenseLoadModeComboBox, "load-mode"), (MoeLoadModeComboBox, "load-mode"),
                     (LazyModeComboBox, "lazy-mode"), (DenseFitComboBox, "fit"), (MoeFitComboBox, "fit"),
                     (FitTargetTextBox, "fit-target"), (FitContextTextBox, "fit-ctx"),
                     (DenseCpuFfnPanel, "n-cpu-ffn"), (MoePlacementComboBox, "cpu-moe"),
                     (SplitModeComboBox, "split-mode"), (TensorSplitTextBox, "tensor-split"),
                     (MainGpuTextBox, "main-gpu"), (NumaComboBox, "numa"),
                     (SwaFullComboBox, "swa-full"), (RepackComboBox, "repack"),
                     (OpOffloadComboBox, "op-offload"), (NoHostComboBox, "no-host"),
                     (RopeScalingComboBox, "rope-scaling"), (RopeScaleTextBox, "rope-scale"),
                     (RopeFreqBaseTextBox, "rope-freq-base"), (RopeFreqScaleTextBox, "rope-freq-scale"),
                     (OverrideTensorTextBox, "override-tensor"),
                 })
        {
            if (capabilities.Contains(option))
            {
                continue;
            }

            control.IsEnabled = false;
            control.ToolTip = control.ToolTip + Environment.NewLine + AppLanguageManager.Choose(
                $"当前 llama.cpp Runtime 不支持 --{option}。",
                $"The current llama.cpp Runtime does not support --{option}.");
        }

        foreach (var (control, option) in new (FrameworkElement, string)[]
                 {
                     (MtpNMaxTextBox, "spec-draft-n-max"),
                     (MtpNMinTextBox, "spec-draft-n-min"),
                     (MtpPMinTextBox, "spec-draft-p-min"),
                     (MtpPSplitTextBox, "spec-draft-p-split"),
                     (MtpBackendSamplingComboBox, "spec-draft-backend-sampling"),
                     (MtpGpuLayersTextBox, "spec-draft-ngl"),
                     (MtpDeviceTextBox, "spec-draft-device"),
                     (MtpCacheTypeKComboBox, "spec-draft-type-k"),
                     (MtpCacheTypeVComboBox, "spec-draft-type-v"),
                     (MtpThreadsTextBox, "spec-draft-threads"),
                     (MtpBatchThreadsTextBox, "spec-draft-threads-batch"),
                 })
        {
            if (capabilities.Contains(option))
            {
                continue;
            }

            control.IsEnabled = false;
            control.ToolTip = control.ToolTip + Environment.NewLine + AppLanguageManager.Choose(
                $"当前 llama.cpp Runtime 不支持 --{option}。",
                $"The current llama.cpp Runtime does not support --{option}.");
        }

        UpdateMtpUiState();
        foreach (var (control, option) in new (FrameworkElement, string)[]
                 {
                     (VisionOffloadComboBox, "mmproj-offload"),
                     (VisionDeviceTextBox, "mmproj-device"),
                     (VisionImageMinTokensTextBox, "image-min-tokens"),
                     (VisionImageMaxTokensTextBox, "image-max-tokens"),
                     (VisionBatchMaxTokensTextBox, "mtmd-batch-max-tokens"),
                 })
        {
            if (!capabilities.Contains(option))
            {
                control.IsEnabled = false;
                control.ToolTip = control.ToolTip + Environment.NewLine + AppLanguageManager.Choose(
                    $"当前 llama.cpp Runtime 不支持 --{option}。",
                    $"The current llama.cpp Runtime does not support --{option}.");
            }
        }

        UpdateVisionUiState();
    }

    private void PopulateVision(ModelProfile profile)
    {
        _isPopulatingVision = true;
        VisionEnableCheckBox.IsChecked = profile.VisionEnabled;
        VisionSourceValueTextBlock.Text = profile.VisionSource == VisionSourceKind.External
            ? AppLanguageManager.Choose("外置视觉投影器（由本地模型管理加载）", "External vision projector (loaded in Local Model Management)")
            : AppLanguageManager.Choose("模型内置或由 llama.cpp 管理的视觉能力", "Built-in or llama.cpp-managed vision capability");
        VisionProjectorPathTextBox.Text = profile.VisionProjectorRelativePath ?? string.Empty;
        Select(VisionOffloadComboBox, profile.VisionProjectorOffload is true ? "on" : profile.VisionProjectorOffload is false ? "off" : string.Empty);
        VisionDeviceTextBox.Text = profile.VisionProjectorDevice ?? string.Empty;
        VisionImageMinTokensTextBox.Text = FormatOptional(profile.VisionImageMinTokens);
        VisionImageMaxTokensTextBox.Text = FormatOptional(profile.VisionImageMaxTokens);
        VisionBatchMaxTokensTextBox.Text = FormatOptional(profile.VisionBatchMaxTokens);
        VisionExternalProjectorPanel.Visibility = profile.VisionSource == VisionSourceKind.External
            ? Visibility.Visible
            : Visibility.Collapsed;
        _isPopulatingVision = false;
        UpdateVisionUiState();
    }

    private void UpdateVisionUiState()
    {
        if (VisionSettingsPanel is null || VisionStatusTextBlock is null)
        {
            return;
        }

        var verified = _originalProfile.VisionCapabilityStatus == VisionCapabilityStatus.Verified
                       && !string.IsNullOrWhiteSpace(_originalProfile.VisionValidationSignature);
        var runtimeBlocked = _runtimeSupportsVision == false;
        var enabled = VisionEnableCheckBox.IsChecked == true;
        VisionEnableCheckBox.IsEnabled = enabled || (verified && !runtimeBlocked);
        VisionSettingsPanel.IsEnabled = !runtimeBlocked;
        VisionExternalProjectorPanel.Visibility = _originalProfile.VisionSource == VisionSourceKind.External
            ? Visibility.Visible : Visibility.Collapsed;
        VisionStatusTextBlock.Text = runtimeBlocked
            ? AppLanguageManager.Choose("当前 llama.cpp Runtime 未报告 --mmproj，无法启用视觉模块。", "The current llama.cpp Runtime does not report --mmproj, so vision cannot be enabled.")
            : _originalProfile.VisionCapabilityStatus switch
            {
                _ when !verified && _hasRegisteredVisionResources => AppLanguageManager.Choose("已有可用视觉模块；请在下拉菜单中选择后开启。", "Vision modules are available. Select one from the list to enable vision."),
                VisionCapabilityStatus.Verified => AppLanguageManager.Choose("已通过 GGUF 元数据识别并建立视觉关联；实际兼容性由 llama.cpp 在正式加载时确认。", "Vision was recognized and associated from GGUF metadata; llama.cpp confirms actual compatibility during normal loading."),
                VisionCapabilityStatus.BuiltInCandidate => AppLanguageManager.Choose("检测到内置视觉元数据，但尚未在本地模型管理中确认关联。", "Built-in vision metadata was detected but has not yet been associated in Local Model Management."),
                VisionCapabilityStatus.ExternalConfigured => AppLanguageManager.Choose("外置视觉关联已失效，请在本地模型管理中重新加载并识别。", "The external vision association is stale. Load and recognize it again in Local Model Management."),
                VisionCapabilityStatus.ValidationFailed => AppLanguageManager.Choose("上次视觉验证失败，请检查模型与 mmproj 是否匹配。", "The previous vision validation failed. Check that the model and mmproj match."),
                _ => AppLanguageManager.Choose("尚无已验证的视觉能力；请在本地模型管理中加载匹配的视觉模块。", "No verified vision capability is available. Load a matching vision module in Local Model Management."),
            };
    }

    private bool TryBuildVisionSettings(out VisionEditorSettings settings, out string error)
    {
        var enabled = VisionEnableCheckBox.IsChecked == true;
        if (!TryParseOptionalInteger(VisionImageMinTokensTextBox.Text, false, out var minimum)
            || !TryParseOptionalInteger(VisionImageMaxTokensTextBox.Text, false, out var maximum)
            || !TryParseOptionalInteger(VisionBatchMaxTokensTextBox.Text, false, out var batch))
        {
            settings = default!;
            error = AppLanguageManager.Choose("视觉 Token 参数必须是正整数，或留空使用 llama.cpp 默认值。", "Vision token values must be positive integers, or blank for llama.cpp defaults.");
            return false;
        }

        if (minimum is int min && maximum is int max && min > max)
        {
            settings = default!;
            error = "Image Min Tokens cannot exceed Image Max Tokens.";
            return false;
        }

        var offload = SelectedValue<string>(VisionOffloadComboBox) switch
        {
            "on" => true,
            "off" => false,
            _ => (bool?)null,
        };
        settings = new VisionEditorSettings(
            enabled,
            offload,
            NullWhenWhiteSpace(VisionDeviceTextBox.Text),
            minimum,
            maximum,
            batch);
        error = string.Empty;
        return true;
    }

    private void PopulateMtp(ModelProfile profile)
    {
        _isPopulatingMtp = true;
        _displayedMtpCapabilityStatus = profile.MtpCapabilityStatus != MtpCapabilityStatus.Unknown
            ? profile.MtpCapabilityStatus
            : profile.MtpSource == MtpSourceKind.External
              && !string.IsNullOrWhiteSpace(profile.MtpDraftModelRelativePath)
                ? MtpCapabilityStatus.ExternalConfigured
                : _hasEmbeddedMtpCandidate
                    ? MtpCapabilityStatus.EmbeddedCandidate
                    : MtpCapabilityStatus.Unknown;
        MtpEnableCheckBox.IsChecked = profile.MtpEnabled;
        MtpSourceValueTextBlock.Text = profile.MtpSource == MtpSourceKind.External
            ? AppLanguageManager.Choose("外置 MTP（由本地模型管理加载）", "External MTP (loaded in Local Model Management)")
            : AppLanguageManager.Choose("主模型内置 MTP", "MTP embedded in the main model");
        MtpDraftModelPathTextBox.Text = profile.MtpDraftModelRelativePath ?? string.Empty;
        MtpNMaxTextBox.Text = FormatOptional(profile.MtpDraftMaxTokens);
        MtpNMinTextBox.Text = FormatOptional(profile.MtpDraftMinTokens);
        MtpPMinTextBox.Text = FormatOptional(profile.MtpDraftMinimumProbability);
        MtpPSplitTextBox.Text = FormatOptional(profile.MtpDraftSplitProbability);
        Select(
            MtpBackendSamplingComboBox,
            profile.MtpBackendSampling is true ? "on" : profile.MtpBackendSampling is false ? "off" : string.Empty);
        MtpGpuLayersTextBox.Text = profile.MtpDraftGpuLayers ?? string.Empty;
        MtpDeviceTextBox.Text = profile.MtpDraftDevice ?? string.Empty;
        Select(MtpCacheTypeKComboBox, profile.MtpDraftCacheTypeK);
        Select(MtpCacheTypeVComboBox, profile.MtpDraftCacheTypeV);
        MtpThreadsTextBox.Text = FormatOptional(profile.MtpDraftThreads);
        MtpBatchThreadsTextBox.Text = FormatOptional(profile.MtpDraftBatchThreads);
        _isPopulatingMtp = false;
        UpdateMtpUiState();
    }

    private void UpdateMtpUiState(MtpCapabilityStatus? savedStatus = null)
    {
        if (MtpSettingsPanel is null || MtpStatusTextBlock is null)
        {
            return;
        }

        var enabled = MtpEnableCheckBox.IsChecked == true;
        var source = _originalProfile.MtpSource;
        var runtimeBlocked = _runtimeSupportsMtp == false;
        var sourceReady = source == MtpSourceKind.External
            ? _displayedMtpCapabilityStatus == MtpCapabilityStatus.Verified
              && !string.IsNullOrWhiteSpace(_originalProfile.MtpDraftModelRelativePath)
            : _hasEmbeddedMtpCandidate
              || _displayedMtpCapabilityStatus is MtpCapabilityStatus.EmbeddedCandidate or MtpCapabilityStatus.Verified;
        MtpEnableCheckBox.IsEnabled = enabled || (!runtimeBlocked && sourceReady);
        MtpSettingsPanel.IsEnabled = !runtimeBlocked;
        var isExternal = source == MtpSourceKind.External;
        MtpExternalModelPanel.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;
        MtpExternalResourcesPanel.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;

        var status = savedStatus ?? _displayedMtpCapabilityStatus;
        MtpStatusTextBlock.Text = runtimeBlocked
            ? AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 没有报告 --spec-type，无法启用原生 MTP。",
                "The current llama.cpp Runtime does not report --spec-type, so native MTP cannot be enabled.")
            : isExternal && _runtimeSupportsExternalMtp == false
                ? AppLanguageManager.Choose(
                    "当前 llama.cpp Runtime 没有报告 --spec-draft-model，无法加载外部 MTP。",
                    "The current llama.cpp Runtime does not report --spec-draft-model, so external MTP cannot be loaded.")
                : status switch
                {
                    MtpCapabilityStatus.Verified => AppLanguageManager.Choose(
                        "该 MTP 配置已经由 llama.cpp 成功加载验证。",
                        "This MTP configuration has been loaded successfully by llama.cpp."),
                    MtpCapabilityStatus.EmbeddedCandidate => AppLanguageManager.Choose(
                        "GGUF 元数据中检测到内置 NextN/MTP 层；首次启用后由 llama.cpp 实际加载验证。",
                        "Embedded NextN/MTP layers were detected in GGUF metadata. llama.cpp will validate them on first load."),
                    MtpCapabilityStatus.ExternalConfigured => AppLanguageManager.Choose(
                        "外置 MTP 尚未完成原生验证；请在本地模型管理中重新加载该文件。",
                        "The external MTP has not passed native validation. Reload it in Local Model Management."),
                    MtpCapabilityStatus.ValidationFailed => AppLanguageManager.Choose(
                        "上次原生加载未能验证 MTP；请检查 Router 日志和辅助模型兼容性。",
                        "The previous native load did not validate MTP. Check Router logs and companion compatibility."),
                    _ when _runtimeSupportsMtp is null => AppLanguageManager.Choose(
                        "未能读取当前 Runtime 的能力列表；保存后仍将由 llama.cpp 原生加载结果判定。",
                        "Runtime capabilities could not be read. Native llama.cpp loading will determine support after saving."),
                    _ => AppLanguageManager.Choose(
                        "当前没有可启用的 MTP 来源；可在本地模型管理中加载并验证外置 MTP 文件。",
                        "No MTP source is currently available. Load and validate an external MTP file in Local Model Management."),
                };
    }

    private void MarkMtpConfigurationEdited()
    {
        if (_isPopulatingMtp)
        {
            return;
        }
    }

    private void PopulateAdvanced(ModelProfile profile)
    {
        var extra = profile.ExtraArguments ?? new Dictionary<string, string?>();
        DeviceTextBox.Text = profile.Device ?? string.Empty;
        ThreadsTextBox.Text = ReadArgument(extra, "threads");
        ThreadsBatchTextBox.Text = ReadArgument(extra, "threads-batch");
        Select(KvOffloadComboBox, extra.ContainsKey("no-kv-offload") ? "off" : extra.ContainsKey("kv-offload") ? "on" : string.Empty);
        var loadMode = ReadArgument(extra, "load-mode");
        Select(DenseLoadModeComboBox, loadMode);
        Select(MoeLoadModeComboBox, loadMode);
        Select(LazyModeComboBox, ReadArgument(extra, "lazy-mode"));
        var fit = ReadArgument(extra, "fit");
        Select(DenseFitComboBox, fit);
        Select(MoeFitComboBox, fit);
        FitTargetTextBox.Text = ReadArgument(extra, "fit-target");
        FitContextTextBox.Text = ReadArgument(extra, "fit-ctx");
        CpuFfnLayersTextBox.Text = ReadArgument(extra, "n-cpu-ffn");
        Select(SplitModeComboBox, ReadArgument(extra, "split-mode"));
        TensorSplitTextBox.Text = ReadArgument(extra, "tensor-split");
        MainGpuTextBox.Text = ReadArgument(extra, "main-gpu");
        Select(NumaComboBox, ReadArgument(extra, "numa"));
        Select(SwaFullComboBox, extra.ContainsKey("swa-full") ? "on" : string.Empty);
        CtxCheckpointsTextBox.Text = ReadArgument(extra, "ctx-checkpoints");
        if (string.IsNullOrWhiteSpace(CtxCheckpointsTextBox.Text))
        {
            CtxCheckpointsTextBox.Text = ReadArgument(extra, "swa-checkpoints");
        }
        UpdateContextCheckpointsUiState();
        KeepTokensTextBox.Text = extra.FirstOrDefault(
            pair => pair.Key.Equals("keep", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
        UpdateKeepUiState();
        Select(RepackComboBox, extra.ContainsKey("no-repack") ? "off" : extra.ContainsKey("repack") ? "on" : string.Empty);
        Select(OpOffloadComboBox, extra.ContainsKey("no-op-offload") ? "off" : extra.ContainsKey("op-offload") ? "on" : string.Empty);
        Select(NoHostComboBox, extra.ContainsKey("no-host") ? "on" : string.Empty);
        Select(RopeScalingComboBox, ReadArgument(extra, "rope-scaling"));
        RopeScaleTextBox.Text = ReadArgument(extra, "rope-scale");
        RopeFreqBaseTextBox.Text = ReadArgument(extra, "rope-freq-base");
        RopeFreqScaleTextBox.Text = ReadArgument(extra, "rope-freq-scale");
        YarnArgumentsTextBox.Text = BuildYarnText(extra);
        OverrideTensorTextBox.Text = ReadArgument(extra, "override-tensor");

        var placement = profile.MoeExpertPlacement switch
        {
            MoeExpertPlacement.Gpu => "gpu",
            MoeExpertPlacement.CpuAll => "cpu-all",
            MoeExpertPlacement.CpuFirstLayers => "cpu-first",
            _ when extra.ContainsKey("cpu-moe") => "cpu-all",
            _ when extra.ContainsKey("n-cpu-moe") => "cpu-first",
            _ => string.Empty,
        };
        Select(MoePlacementComboBox, placement);
        CpuMoeLayersTextBox.Text = profile.CpuMoeLayers?.ToString(CultureInfo.InvariantCulture)
            ?? ReadArgument(extra, "n-cpu-moe");
        CpuMoeLayersTextBox.IsEnabled = placement == "cpu-first";
    }

    private bool TryBuildMtpSettings(out MtpEditorSettings settings, out string error)
    {
        var enabled = MtpEnableCheckBox.IsChecked == true;
        var source = _originalProfile.MtpSource;
        var draftPath = NullWhenWhiteSpace(MtpDraftModelPathTextBox.Text);

        if (enabled
            && source == MtpSourceKind.External
            && string.IsNullOrWhiteSpace(draftPath))
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "请先在本地模型管理中加载并验证外置 MTP 文件。",
                "Load and validate an external MTP file in Local Model Management first.");
            return false;
        }

        if (!TryParseOptionalInteger(MtpNMaxTextBox.Text, allowZero: false, out var nMax)
            || !TryParseOptionalInteger(MtpNMinTextBox.Text, allowZero: true, out var nMin)
            || !TryParseOptionalInteger(MtpThreadsTextBox.Text, allowZero: false, out var threads)
            || !TryParseOptionalInteger(MtpBatchThreadsTextBox.Text, allowZero: false, out var batchThreads))
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "N Max 和线程数必须是正整数；N Min 必须是非负整数。留空表示使用 llama.cpp 默认值。",
                "N Max and thread counts must be positive integers; N Min must be non-negative. Leave blank for llama.cpp defaults.");
            return false;
        }

        if (nMax is int maximum && nMin is int minimum && minimum > maximum)
        {
            settings = default!;
            error = AppLanguageManager.Choose("N Min 不能大于 N Max。", "N Min cannot exceed N Max.");
            return false;
        }

        if (!TryParseOptionalProbability(MtpPMinTextBox.Text, out var pMin)
            || !TryParseOptionalProbability(MtpPSplitTextBox.Text, out var pSplit))
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "P Min 和 P Split 必须是 0 到 1 之间的数值，或留空使用默认值。",
                "P Min and P Split must be values from 0 to 1, or blank for the default.");
            return false;
        }

        var gpuLayers = NullWhenWhiteSpace(MtpGpuLayersTextBox.Text);
        if (gpuLayers is not null
            && gpuLayers is not ("auto" or "all")
            && (!int.TryParse(gpuLayers, NumberStyles.None, CultureInfo.InvariantCulture, out var layerCount)
                || layerCount < 0))
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "外部 MTP GPU Offload 可填写非负整数、auto、all，或留空。",
                "External MTP GPU Offload accepts a non-negative integer, auto, all, or blank.");
            return false;
        }

        var backendSampling = SelectedValue<string>(MtpBackendSamplingComboBox) switch
        {
            "on" => true,
            "off" => false,
            _ => (bool?)null,
        };
        var cacheTypeK = NullWhenWhiteSpace(SelectedValue<string>(MtpCacheTypeKComboBox));
        var cacheTypeV = NullWhenWhiteSpace(SelectedValue<string>(MtpCacheTypeVComboBox));
        var device = NullWhenWhiteSpace(MtpDeviceTextBox.Text);

        var keepValidation = _originalProfile.MtpCapabilityStatus == MtpCapabilityStatus.Verified
                             && source == _originalProfile.MtpSource
                             && string.Equals(
                                 draftPath,
                                 _originalProfile.MtpDraftModelRelativePath,
                                 StringComparison.OrdinalIgnoreCase);
        var capabilityStatus = keepValidation
            ? MtpCapabilityStatus.Verified
            : source == MtpSourceKind.External && !string.IsNullOrWhiteSpace(draftPath)
                ? MtpCapabilityStatus.ExternalConfigured
                : _hasEmbeddedMtpCandidate
                    ? MtpCapabilityStatus.EmbeddedCandidate
                    : MtpCapabilityStatus.Unknown;

        settings = new MtpEditorSettings(
            enabled,
            source,
            capabilityStatus,
            draftPath,
            nMax,
            nMin,
            pMin,
            pSplit,
            backendSampling,
            gpuLayers,
            device,
            cacheTypeK,
            cacheTypeV,
            threads,
            batchThreads,
            keepValidation);
        error = string.Empty;
        return true;
    }

    private static bool TryParseOptionalInteger(string? text, bool allowZero, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || (allowZero ? parsed < 0 : parsed <= 0))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseOptionalProbability(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!double.IsFinite(parsed) || parsed is < 0 or > 1)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatOptional(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatOptional(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;

    private bool TryGetSelectedContextSize(out int contextSize, out string error)
    {
        if (ContextSizeComboBox.SelectedValue is int selectedValue
            && selectedValue != CustomContextSizeChoice)
        {
            contextSize = selectedValue;
            error = string.Empty;
            return true;
        }

        if (!TryParseContextSize(CustomContextSizeTextBox.Text, out contextSize, _originalProfile.ContextSize))
        {
            error = AppLanguageManager.Choose(
                "请直接输入正整数，单位为 K，例如输入 96 表示 96K（1K=1,024 tokens）。",
                "Enter a positive whole number in K, for example 96 for 96K (1K=1,024 tokens).");
            return false;
        }

        if (contextSize % ContextAlignment != 0)
        {
            error = AppLanguageManager.Choose(
                $"自定义 Context 必须是 {ContextAlignment:N0} tokens 的整数倍。",
                $"Custom Context must be a multiple of {ContextAlignment:N0} tokens.");
            return false;
        }

        if (_modelContextLimit is int contextLimit && contextSize > contextLimit)
        {
            error = AppLanguageManager.Choose(
                $"自定义 Context 不能超过模型声明上限 {contextLimit:N0} tokens。",
                $"Custom Context cannot exceed the declared model limit of {contextLimit:N0} tokens.");
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void UpdateContextRisk()
    {
        if (ContextRiskTextBlock is null || ContextSizeComboBox is null)
        {
            return;
        }

        var hasContextSize = ContextSizeComboBox.SelectedValue is int selectedValue
            && selectedValue != CustomContextSizeChoice
                ? (selectedValue > 0, selectedValue)
                : TryParseContextSize(CustomContextSizeTextBox?.Text, out var customValue, _originalProfile.ContextSize)
                    ? (true, customValue)
                    : (false, 0);
        ContextRiskTextBlock.Visibility = hasContextSize.Item1
            && hasContextSize.Item2 < ModelProfile.RecommendedMinimumCodexContext
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void UpdateLongConversationSummary()
    {
        if (_updatingConversationLimits || _populatingConversationLimits
            || ConversationValidationTextBlock is null || SaveProfileButton is null) return;
        _updatingConversationLimits = true;
        try
        {
            var hasContext = TryGetSelectedContextSize(out var context, out var contextError);
            if (hasContext && context <= 0)
            {
                hasContext = false;
                contextError = AppLanguageManager.Choose("请设置明确的 Context，才能校验长对话参数关系。", "Set an explicit Context to validate the conversation limits.");
            }
            var hasReserve = TryParseContextSize(CompactionSafetyReserveComboBox.Text, out var reserve, _originalProfile.CompactionSafetyReserve);
            var toolText = ToolOutputTokenLimitComboBox.Text.Trim();
            var inherit = toolText == AppLanguageManager.Choose("沿用 Codex 设置", "Inherit Codex setting");
            var hasTool = TryParseContextSize(toolText, out var tool, _originalProfile.ToolOutputTokenLimit ?? 0);

            if (hasContext && _choiceContext != context)
            {
                RebuildLimitChoices(CompactionSafetyReserveComboBox,
                    LongConversationLimits.MinimumReserve(context), LongConversationLimits.MaximumReserve(context), false);
                _choiceContext = context;
            }
            if (hasReserve && _choiceReserve != reserve)
            {
                RebuildLimitChoices(ToolOutputTokenLimitComboBox, LongConversationLimits.MinimumTokens,
                    reserve - 1, true);
                _choiceReserve = reserve;
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            if (!hasContext) errors.Add(contextError);
            if (!hasReserve) errors.Add(AppLanguageManager.Choose("安全余量请直接输入正整数，单位为 K。", "Enter a positive whole number for the reserve in K."));
            if (!inherit && !hasTool) errors.Add(AppLanguageManager.Choose("工具结果上限请直接输入正整数，单位为 K。", "Enter a positive whole number for the tool limit in K."));
            if (hasContext && hasReserve)
                errors.AddRange(LongConversationLimits.Validate(context, reserve, !inherit && hasTool ? tool : null));

            var minimumReserveK = (long)Math.Ceiling(LongConversationLimits.MinimumReserve(context) / 1024m);
            var maximumReserveK = LongConversationLimits.MaximumReserve(context) / 1024;
            var maximumToolK = (reserve - 1) / 1024;
            ReserveRangeTextBlock.Text = hasContext
                ? minimumReserveK <= maximumReserveK
                    ? AppLanguageManager.Choose($"范围：{minimumReserveK:N0}～{maximumReserveK:N0} K", $"Range: {minimumReserveK:N0}–{maximumReserveK:N0} K")
                    : AppLanguageManager.Choose("当前上下文没有可用的安全余量范围。", "No valid reserve range for this context.")
                : AppLanguageManager.Choose("请先输入有效 Context。", "Enter a valid Context first.");
            ToolRangeTextBlock.Text = hasReserve && maximumToolK >= 1
                ? AppLanguageManager.Choose($"显式值范围：1～{maximumToolK:N0} K", $"Explicit range: 1–{maximumToolK:N0} K")
                : AppLanguageManager.Choose("当前余量没有可用的整数 K 工具上限。", "No valid whole-K tool limit for this reserve.");
            var reserveInvalid = !hasReserve || (hasContext
                && (reserve < LongConversationLimits.MinimumReserve(context) || reserve > LongConversationLimits.MaximumReserve(context)));
            var toolInvalid = !inherit && (!hasTool || tool < LongConversationLimits.MinimumTokens || !hasReserve || tool >= reserve);
            ReserveRangeTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, reserveInvalid ? "ProfileWarningBrush" : "MutedBrush");
            ToolRangeTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, toolInvalid ? "ProfileWarningBrush" : "MutedBrush");
            if (reserveInvalid) CompactionSafetyReserveComboBox.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
            else CompactionSafetyReserveComboBox.ClearValue(Control.BorderBrushProperty);
            if (toolInvalid) ToolOutputTokenLimitComboBox.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
            else ToolOutputTokenLimitComboBox.ClearValue(Control.BorderBrushProperty);
            if (hasContext && hasReserve && reserve > 0 && reserve < context)
            {
                var threshold = context - reserve;
                AutoCompactThresholdTextBlock.Text = AppLanguageManager.Choose(
                    $"Codex 自动压缩线：{FormatContextSize(threshold)}（{threshold:N0} tokens）", $"Codex auto-compaction: {FormatContextSize(threshold)} ({threshold:N0} tokens)");
            }
            else AutoCompactThresholdTextBlock.Text = AppLanguageManager.Choose("自动压缩线：等待有效参数。", "Auto-compaction: awaiting valid values.");

            if (inherit) warnings.Add(AppLanguageManager.Choose("继承值未知，无法校验与安全余量的关系；可改为显式输入。", "Inherited value is unknown; its relation to the reserve cannot be validated. You can enter an explicit value."));
            if (hasReserve && hasTool && !inherit && tool < reserve && reserve - tool < 1024)
                warnings.Add(AppLanguageManager.Choose("工具上限接近安全余量，其他新增内容可用空间不足 1K。", "The tool limit is close to the reserve; less than 1K remains for other new content."));
            if (hasContext && hasReserve && (long)reserve * 2 > context)
                warnings.Add(AppLanguageManager.Choose("安全余量超过上下文的一半，可能增加压缩频率。", "The reserve exceeds half the context and may increase compaction frequency."));
            ConversationValidationTextBlock.Text = string.Join(Environment.NewLine, errors.Concat(warnings));
            ConversationValidationTextBlock.Visibility = Visibility.Visible;
            CompactionSafetyReserveComboBox.ToolTip = AppLanguageManager.Text("ParameterHelp_CompactionSafetyReserveComboBox") + Environment.NewLine + ReserveRangeTextBlock.Text;
            ToolOutputTokenLimitComboBox.ToolTip = AppLanguageManager.Text("ParameterHelp_ToolOutputTokenLimitComboBox") + Environment.NewLine + ToolRangeTextBlock.Text;
            SaveProfileButton.IsEnabled = SaveModelDefaultButton.IsEnabled = errors.Count == 0 && _contextShiftCancellation is null;
        }
        finally { _updatingConversationLimits = false; }
    }

    private static void RebuildLimitChoices(ComboBox combo, int minimum, int maximum, bool inherit)
    {
        var text = combo.Text;
        var editor = combo.Template?.FindName("PART_EditableTextBox", combo) as WpfTextBox;
        var caret = editor?.SelectionStart ?? 0;
        var selection = editor?.SelectionLength ?? 0;
        var values = new SortedSet<int>();
        foreach (var k in new[] { 1, 2, 4, 8, 12, 16, 20, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512 })
            if (k * 1024 >= minimum && k * 1024 <= maximum) values.Add(k * 1024);
        for (long value = 1024 * 1024; value <= maximum; value *= 2)
            if (value >= minimum) values.Add((int)value);
        if (TryParseContextSize(text, out var current) && current >= minimum && current <= maximum) values.Add(current);
        var firstWholeK = (long)Math.Ceiling(minimum / 1024m) * 1024;
        if (values.Count == 0 && firstWholeK <= maximum) values.Add((int)firstWholeK);
        var choices = values.Select(value => new Choice<int>(FormatKInput(value), value)).ToList();
        if (inherit) choices.Insert(0, new Choice<int>(AppLanguageManager.Choose("沿用 Codex 设置", "Inherit Codex setting"), 0));
        combo.ItemsSource = choices;
        combo.Text = text;
        if (editor is not null) editor.Select(Math.Min(caret, text.Length), Math.Min(selection, Math.Max(0, text.Length - caret)));
    }

    private static int? TryReadModelContextLimit(ModelProfile profile, string runtimeRoot)
        => TryReadModelMetadata(profile, runtimeRoot)?.ContextLength;

    private static GgufModelMetadata? TryReadModelMetadata(ModelProfile profile, string runtimeRoot)
    {
        try
        {
            var root = Path.GetFullPath(runtimeRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var modelPath = Path.GetFullPath(Path.Combine(root, profile.ModelRelativePath));
            if (!modelPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(modelPath))
            {
                return null;
            }

            return GgufContextMetadataReader.ReadMetadata(modelPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or OverflowException)
        {
            return null;
        }
    }

    private static IReadOnlyList<Choice<int>> BuildContextChoices(int? modelContextLimit)
    {
        var limit = modelContextLimit ?? FallbackContextLimit;
        var values = new SortedSet<int>();
        foreach (var initialValue in new[] { 4 * 1024, 8 * 1024, 16 * 1024 })
        {
            if (initialValue <= limit)
            {
                values.Add(initialValue);
            }
        }

        for (var value = 32 * 1024; value <= limit; value += 16 * 1024)
        {
            values.Add(value);
            if (value > int.MaxValue - 16 * 1024)
            {
                break;
            }
        }

        if (modelContextLimit is > 0)
        {
            values.Add(modelContextLimit.Value);
        }

        var choices = new List<Choice<int>>
        {
            new(AppLanguageManager.Choose("自动（模型默认）", "Auto (Model Default)"), 0),
        };
        choices.AddRange(values.Select(value => new Choice<int>(
            FormatKInput(value)
            + (modelContextLimit == value
                ? AppLanguageManager.Choose("（模型上限）", " (Model Limit)")
                : string.Empty),
            value)));
        choices.Add(new Choice<int>(AppLanguageManager.Choose("自定义…", "Custom…"), CustomContextSizeChoice));
        return choices;
    }

    private static bool TryParseContextSize(string? text, out int value, int existingTokens = 0)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        // Preserve an unchanged legacy value exactly; newly entered values use whole K.
        if (existingTokens > 0 && existingTokens % 1024 != 0 && normalized == FormatKInput(existingTokens))
        {
            value = existingTokens;
            return true;
        }

        if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var wholeK)
            || wholeK <= 0 || wholeK > int.MaxValue / 1024) return false;
        value = wholeK * 1024;
        return true;
    }

    private static string FormatKInput(int value) =>
        (value / 1024m).ToString("0.##########", CultureInfo.InvariantCulture);

    private static string FormatContextSize(int value) =>
        value % 1024 == 0
            ? $"{value / 1024:N0}K"
            : $"{value:N0}";

    private static void PopulateBatchChoices(ComboBox comboBox, IReadOnlyList<Choice<int>> standardChoices, int? value)
    {
        var choices = standardChoices.ToList();
        var label = value?.ToString("N0", CultureInfo.InvariantCulture) ?? "Default";
        if (value is { } number && !choices.Any(choice => choice.Value == number))
            choices.Add(new Choice<int>(label, number));
        comboBox.ItemsSource = choices;
        comboBox.SelectedValue = value ?? 0;
        comboBox.Text = label;
    }

    private bool TryGetBatchSizes(out int? batchSize, out int? microBatchSize, out string error)
    {
        error = string.Empty;
        var batchValid = TryParseBatchSize(BatchSizeComboBox.Text, out batchSize);
        var microBatchValid = TryParseBatchSize(MicroBatchSizeComboBox.Text, out microBatchSize);
        if (!batchValid || !microBatchValid)
        {
            error = AppLanguageManager.Choose(
                "Batch Size 和 Micro Batch 请输入正整数（可使用千位逗号），或选择 Default／留空以沿用 llama.cpp 默认值。",
                "Enter positive integers for Batch Size and Micro Batch (thousands separators are allowed), or choose Default / leave blank to use llama.cpp defaults.");
            return false;
        }
        if (batchSize is not null && microBatchSize is not null && microBatchSize > batchSize)
        {
            error = AppLanguageManager.Choose("Micro Batch 不应大于 Batch Size。", "Micro Batch must not exceed Batch Size.");
            return false;
        }
        return true;
    }

    private static bool TryParseBatchSize(string? text, out int? value)
    {
        value = null;
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Equals("Default", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Contains(',') && !System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\A[0-9]{1,3}(,[0-9]{3})+\z")) return false;
        if (!int.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return false;
        value = number;
        return true;
    }

    private static void PopulateChoices<T>(
        System.Windows.Controls.ComboBox comboBox,
        IReadOnlyList<Choice<T>> standardChoices,
        T selectedValue,
        string customLabel)
        where T : notnull
    {
        var choices = standardChoices.ToList();
        if (!choices.Any(choice => EqualityComparer<T>.Default.Equals(choice.Value, selectedValue)))
        {
            choices.Add(new Choice<T>(AppLanguageManager.Choose($"当前：{customLabel}", $"Current: {customLabel}"), selectedValue));
        }

        comboBox.ItemsSource = choices;
        comboBox.SelectedValue = selectedValue;
    }

    private static T SelectedValue<T>(System.Windows.Controls.ComboBox comboBox) where T : notnull =>
        comboBox.SelectedValue is T value
            ? value
            : throw new InvalidOperationException(AppLanguageManager.Choose(
                "请选择一个有效参数值。",
                "Select a valid parameter value."));

    private bool TryGetGpuLayers(out string gpuLayers, out string error)
    {
        gpuLayers = SelectedValue<string>(GpuLayersComboBox);
        if (gpuLayers == CustomGpuLayersChoice)
        {
            gpuLayers = CustomGpuLayersTextBox.Text.Trim();
        }

        if (gpuLayers is "auto" or "all"
            || int.TryParse(gpuLayers, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0)
        {
            error = string.Empty;
            return true;
        }

        error = AppLanguageManager.Choose(
            "GPU Offload 必须是 Auto、All 或非负整数。",
            "GPU Offload must be Auto, All, or a non-negative integer.");
        return false;
    }

    private bool TryGetMoePlacement(
        out MoeExpertPlacement? placement,
        out int? cpuMoeLayers,
        out string error)
    {
        placement = null;
        cpuMoeLayers = null;
        error = string.Empty;
        if (_originalProfile.ModelType != ModelType.MoE)
        {
            return true;
        }

        var value = SelectedValue<string>(MoePlacementComboBox);
        placement = value switch
        {
            "gpu" => MoeExpertPlacement.Gpu,
            "cpu-all" => MoeExpertPlacement.CpuAll,
            "cpu-first" => MoeExpertPlacement.CpuFirstLayers,
            _ => null,
        };
        if (placement != MoeExpertPlacement.CpuFirstLayers)
        {
            return true;
        }

        if (!int.TryParse(CpuMoeLayersTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var layers)
            || layers <= 0)
        {
            error = AppLanguageManager.Choose(
                "选择前 N 层专家放在 CPU 时，请填写正整数层数。",
                "Enter a positive layer count when placing the first N expert layers on CPU.");
            return false;
        }

        cpuMoeLayers = layers;
        return true;
    }

    private bool TryBuildAdvancedArguments(
        out IReadOnlyDictionary<string, string?> arguments,
        out string error)
    {
        var extra = (_originalProfile.ExtraArguments ?? new Dictionary<string, string?>())
            .Where(pair => ManagedAdvancedArgumentKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var key in ManagedAdvancedArgumentKeys)
        {
            extra.Remove(key);
        }
        foreach (var key in extra.Keys.Where(key => key.Equals("keep", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            extra.Remove(key);
        }

        if (!ValidateOptionalNonNegativeIntegers(
                out error,
                (AppLanguageManager.Choose("CPU 生成线程", "CPU Generation Threads"), ThreadsTextBox.Text, false),
                (AppLanguageManager.Choose("CPU 批处理线程", "CPU Batch Threads"), ThreadsBatchTextBox.Text, false),
                (AppLanguageManager.Choose("Fit 最低上下文", "Fit Minimum Context"), FitContextTextBox.Text, false),
                (AppLanguageManager.Choose("CPU Dense FFN 层数", "CPU Dense FFN Layers"), CpuFfnLayersTextBox.Text, true),
                (AppLanguageManager.Choose("主 GPU", "Main GPU"), MainGpuTextBox.Text, true),
                (AppLanguageManager.Choose("上下文检查点", "Context Checkpoints"), CtxCheckpointsTextBox.Text, true),
                ("--keep", KeepTokensTextBox.Text, true)))
        {
            arguments = extra;
            return false;
        }

        AddOptional(extra, "threads", ThreadsTextBox.Text);
        AddOptional(extra, "threads-batch", ThreadsBatchTextBox.Text);
        AddOptional(extra, "ctx-checkpoints", CtxCheckpointsTextBox.Text);
        AddOptional(extra, "keep", KeepTokensTextBox.Text);
        AddToggle(extra, KvOffloadComboBox, "kv-offload", "no-kv-offload");
        AddOptional(extra, "load-mode", _originalProfile.ModelType == ModelType.MoE
            ? SelectedValue<string>(MoeLoadModeComboBox)
            : SelectedValue<string>(DenseLoadModeComboBox));
        AddOptional(extra, "lazy-mode", SelectedValue<string>(LazyModeComboBox));
        AddOptional(extra, "fit", _originalProfile.ModelType == ModelType.MoE
            ? SelectedValue<string>(MoeFitComboBox)
            : SelectedValue<string>(DenseFitComboBox));
        AddOptional(extra, "fit-target", FitTargetTextBox.Text);
        AddOptional(extra, "fit-ctx", FitContextTextBox.Text);
        if (_originalProfile.ModelType == ModelType.Dense)
        {
            AddOptional(extra, "n-cpu-ffn", CpuFfnLayersTextBox.Text);
        }

        AddOptional(extra, "split-mode", SelectedValue<string>(SplitModeComboBox));
        AddOptional(extra, "tensor-split", TensorSplitTextBox.Text);
        AddOptional(extra, "main-gpu", MainGpuTextBox.Text);
        AddOptional(extra, "numa", SelectedValue<string>(NumaComboBox));
        if (SelectedValue<string>(SwaFullComboBox) == "on") extra["swa-full"] = null;
        AddToggle(extra, RepackComboBox, "repack", "no-repack");
        AddToggle(extra, OpOffloadComboBox, "op-offload", "no-op-offload");
        if (SelectedValue<string>(NoHostComboBox) == "on") extra["no-host"] = null;
        AddOptional(extra, "rope-scaling", SelectedValue<string>(RopeScalingComboBox));
        AddOptional(extra, "rope-scale", RopeScaleTextBox.Text);
        AddOptional(extra, "rope-freq-base", RopeFreqBaseTextBox.Text);
        AddOptional(extra, "rope-freq-scale", RopeFreqScaleTextBox.Text);
        if (!TryAddYarnArguments(extra, YarnArgumentsTextBox.Text, out error))
        {
            arguments = extra;
            return false;
        }

        AddOptional(extra, "override-tensor", OverrideTensorTextBox.Text);
        arguments = extra;
        return true;
    }

    private void UpdateContextCheckpointsUiState()
    {
        if (_runtimeSupportsContextCheckpoints == false)
        {
            // Existing intent stays editable; runtime support is checked at startup.
            CtxCheckpointsTextBox.IsEnabled = !string.IsNullOrWhiteSpace(CtxCheckpointsTextBox.Text);
            CtxCheckpointsTextBox.ToolTip = AppLanguageManager.Text("ParameterHelp_CtxCheckpointsTextBox") + Environment.NewLine + AppLanguageManager.Choose(
                "当前运行库未报告支持 --ctx-checkpoints；可保存，启动时会检查。",
                "The runtime does not report --ctx-checkpoints support. Settings can be saved; startup checks support.");
            return;
        }

        CtxCheckpointsTextBox.IsEnabled = true;
        CtxCheckpointsTextBox.SetResourceReference(FrameworkElement.ToolTipProperty, "ParameterHelp_CtxCheckpointsTextBox");
    }

    private void UpdateKeepUiState()
    {
        if (KeepTokensTextBox is null || KeepTokensStatusTextBlock is null)
        {
            return;
        }

        var hasValue = !string.IsNullOrWhiteSpace(KeepTokensTextBox.Text);
        KeepTokensTextBox.IsEnabled = hasValue || _runtimeSupportsKeep == true;
        KeepTokensStatusTextBlock.Visibility = _runtimeSupportsKeep == true
            ? Visibility.Collapsed
            : Visibility.Visible;
        KeepTokensStatusTextBlock.Text = _runtimeSupportsKeep switch
        {
            false => AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 未报告 --keep。", "The current llama.cpp Runtime does not report --keep."),
            _ => AppLanguageManager.Choose(
                "尚未确认当前 Runtime 是否支持 --keep。", "Support for --keep has not yet been confirmed for this Runtime."),
        };
    }

    private bool TryGetConversationLimits(out int reserve, out int? toolLimit, out string error)
    {
        toolLimit = null;
        var toolText = ToolOutputTokenLimitComboBox?.Text?.Trim() ?? string.Empty;
        var inherit = toolText == AppLanguageManager.Choose("沿用 Codex 设置", "Inherit Codex setting");
        if (!TryParseContextSize(CompactionSafetyReserveComboBox?.Text, out reserve, _originalProfile.CompactionSafetyReserve) || reserve < 1024
            || (!inherit && (!TryParseContextSize(toolText, out var tool, _originalProfile.ToolOutputTokenLimit ?? 0) || tool < 1024 || tool >= reserve)))
        {
            error = AppLanguageManager.Choose("安全余量和工具上限请直接输入正整数，单位为 K（1K=1,024 tokens）；工具上限必须小于安全余量。", "Enter positive whole numbers in K for the reserve and tool limit (1K=1,024 tokens). The tool limit must be smaller than the reserve.");
            return false;
        }
        if (!inherit) { TryParseContextSize(toolText, out var parsedTool, _originalProfile.ToolOutputTokenLimit ?? 0); toolLimit = parsedTool; }
        if (!TryGetSelectedContextSize(out var context, out error)) return false;
        if (context <= 0)
        {
            error = AppLanguageManager.Choose("请设置明确的 Context，才能校验长对话参数关系。", "Set an explicit Context to validate the conversation limits.");
            return false;
        }
        var errors = LongConversationLimits.Validate(context, reserve, toolLimit);
        if (errors.Count > 0) { error = string.Join(Environment.NewLine, errors); return false; }
        error = string.Empty;
        return true;
    }

    private bool TryGetCodexStreamIdleTimeout(out int? minutes)
    {
        minutes = null;
        var text = CodexStreamIdleTimeoutComboBox.Text.Trim();
        if (text == AppLanguageManager.Text("CodexStreamIdleTimeoutDefault")) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.CurrentCulture, out var value)
            || !CodexStreamIdleTimeout.IsValid(value)) return false;
        minutes = value;
        return true;
    }

    private void UpdateContextShiftUiState()
    {
        if (ContextShiftCheckBox is null || ContextShiftStatusTextBlock is null) return;
        var busy = _contextShiftCancellation is not null;
        var blocked = _shiftCapability?.Supported == false || _runtimeSupportsContextShift == false
            || VisionEnableCheckBox.IsChecked == true;
        ContextShiftCheckBox.IsEnabled = !busy && (!blocked || ContextShiftCheckBox.IsChecked == true);
        ContextShiftDetectButton.IsEnabled = !busy && _canDetectConfiguration() && _runtimeSupportsContextShift == true && VisionEnableCheckBox.IsChecked != true;
        ContextShiftStatusTextBlock.Visibility = Visibility.Visible;
        ContextShiftStatusTextBlock.Text = busy
            ? AppLanguageManager.Choose("正在加载模型并确认实际滚动；可能需要几分钟，可取消。", "Loading the model and confirming actual shifting; this may take several minutes. You can cancel.")
            : (_shiftCapability?.Reason ?? AppLanguageManager.Choose("尚未验证当前配置；首次开启时检测。", "This configuration is unverified; checked on first enable."))
                + Environment.NewLine + (ContextShiftCheckBox.IsChecked == true
                    ? _shiftCapability?.Supported == true && _runtimeSupportsContextShift == true && VisionEnableCheckBox.IsChecked != true
                        ? AppLanguageManager.Choose("开启：显式使用 --context-shift。", "On: explicitly uses --context-shift.")
                        : AppLanguageManager.Choose("已选择开启：验证无效或尚未确认，启动前需重新检测或关闭。", "Requested on: verification is missing or invalid; check again or turn off before starting.")
                    : AppLanguageManager.Choose("关闭：显式使用 --no-context-shift；触顶停止，工具参数仍可能被截断。", "Off: explicitly uses --no-context-shift; generation stops at capacity, and tool arguments may be truncated."));
    }

    private static bool ValidateOptionalNonNegativeIntegers(
        out string error,
        params (string Label, string Text, bool AllowZero)[] values)
    {
        foreach (var value in values)
        {
            var text = value.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || (value.AllowZero ? number < 0 : number <= 0))
            {
                error = AppLanguageManager.IsEnglish
                    ? $"{value.Label} must be a {(value.AllowZero ? "non-negative" : "positive")} integer, or blank to use the default."
                    : $"{value.Label}必须是{(value.AllowZero ? "非负" : "正")}整数，或留空使用默认值。";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static void AddOptional(IDictionary<string, string?> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    private static void AddToggle(
        IDictionary<string, string?> values,
        ComboBox comboBox,
        string enabledKey,
        string disabledKey)
    {
        var value = SelectedValue<string>(comboBox);
        if (value == "on") values[enabledKey] = null;
        if (value == "off") values[disabledKey] = null;
    }

    private static string ReadArgument(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static void Select(ComboBox comboBox, string? value) =>
        comboBox.SelectedValue = value ?? string.Empty;

    private static string BuildYarnText(IReadOnlyDictionary<string, string?> values)
    {
        var pairs = new[]
        {
            ("orig-ctx", "yarn-orig-ctx"), ("ext-factor", "yarn-ext-factor"),
            ("attn-factor", "yarn-attn-factor"), ("beta-slow", "yarn-beta-slow"),
            ("beta-fast", "yarn-beta-fast"),
        };
        return string.Join(';', pairs
            .Where(pair => values.ContainsKey(pair.Item2))
            .Select(pair => $"{pair.Item1}={values[pair.Item2]}"));
    }

    private static bool TryAddYarnArguments(
        IDictionary<string, string?> values,
        string text,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            error = string.Empty;
            return true;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["orig-ctx"] = "yarn-orig-ctx",
            ["ext-factor"] = "yarn-ext-factor",
            ["attn-factor"] = "yarn-attn-factor",
            ["beta-slow"] = "yarn-beta-slow",
            ["beta-fast"] = "yarn-beta-fast",
        };
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || separator == item.Length - 1
                || !map.TryGetValue(item[..separator].Trim(), out var key))
            {
                error = AppLanguageManager.Choose(
                    "YaRN 参数格式无效。请使用 orig-ctx=值;ext-factor=值 等格式。",
                    "Invalid YaRN parameter format. Use forms such as orig-ctx=value;ext-factor=value.");
                return false;
            }

            values[key] = item[(separator + 1)..].Trim();
        }

        error = string.Empty;
        return true;
    }

    private static string? NullWhenWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record MtpEditorSettings(
        bool Enabled,
        MtpSourceKind Source,
        MtpCapabilityStatus CapabilityStatus,
        string? DraftModelRelativePath,
        int? DraftMaxTokens,
        int? DraftMinTokens,
        double? DraftMinimumProbability,
        double? DraftSplitProbability,
        bool? BackendSampling,
        string? DraftGpuLayers,
        string? DraftDevice,
        string? DraftCacheTypeK,
        string? DraftCacheTypeV,
        int? DraftThreads,
        int? DraftBatchThreads,
        bool KeepValidation);

    private sealed record VisionEditorSettings(
        bool Enabled,
        bool? ProjectorOffload,
        string? ProjectorDevice,
        int? ImageMinTokens,
        int? ImageMaxTokens,
        int? BatchMaxTokens);

    private sealed record SandboxPathRow(
        Grid Container,
        WpfTextBox PathTextBox,
        WpfCheckBox AllowWriteCheckBox,
        WpfCheckBox GradleCheckBox,
        WpfCheckBox ReplaceGradleCheckBox,
        bool IsGradleDirectory);

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }
}
