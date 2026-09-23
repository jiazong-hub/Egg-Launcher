using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Runtime.Detection;

namespace Launcher.App;

public partial class ProfileEditorWindow : Window
{
    private const int CustomContextSizeChoice = -1;
    private const string CustomGpuLayersChoice = "__custom__";
    private const int FallbackContextLimit = 128 * 1024;
    private const int ContextAlignment = 256;

    private static readonly IReadOnlyList<Choice<int>> CompactionSafetyReserveChoices =
    [
        new("1K", 1024),
        new("2K", 2048),
        new("4K", 4096),
        new("8K", 8192),
        new("12K", 12288),
        new("16K", 16384),
        new("24K", 24576),
        new("32K", 32768),
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
    ];

    private static readonly IReadOnlyList<Choice<int>> MicroBatchChoices =
    [
        new("Default", 0),
        new("64", 64),
        new("128", 128),
        new("256", 256),
        new("512", 512),
    ];

    private readonly IReadOnlyList<Choice<int>> IdleSleepChoices =
    [
        new(AppLanguageManager.Choose("30 秒", "30 seconds"), 30),
        new(AppLanguageManager.Choose("1 分钟", "1 minute"), 60),
        new(AppLanguageManager.Choose("5 分钟", "5 minutes"), 300),
        new(AppLanguageManager.Choose("15 分钟", "15 minutes"), 900),
        new(AppLanguageManager.Choose("禁用", "Disabled"), -1),
    ];

    private readonly string _runtimeRoot;
    private readonly ModelProfile _originalProfile;
    private readonly int? _modelContextLimit;
    private readonly IReadOnlyList<Choice<int>> _contextChoices;
    private readonly bool _hasEmbeddedMtpCandidate;
    private bool? _runtimeSupportsMtp;
    private bool? _runtimeSupportsExternalMtp;
    private bool? _runtimeSupportsContextCheckpoints;
    private bool _isPopulatingMtp;
    private MtpCapabilityStatus _displayedMtpCapabilityStatus = MtpCapabilityStatus.Unknown;
    private bool? _runtimeSupportsVision;
    private bool _isPopulatingVision;

    public ProfileEditorWindow(
        ModelProfile profile,
        string runtimeRoot,
        IReadOnlySet<string>? runtimeCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _originalProfile = profile;
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _modelContextLimit = TryReadModelContextLimit(profile, _runtimeRoot);
        _hasEmbeddedMtpCandidate = TryReadModelMetadata(profile, _runtimeRoot)?.HasEmbeddedMtp == true;
        _contextChoices = BuildContextChoices(_modelContextLimit);
        InitializeComponent();
        UiMotion.AttachWindowEntrance(this);
        InitializeAdvancedChoices();
        InitializeMtpChoices();
        VisionOffloadComboBox.ItemsSource = DefaultOnOffChoices;
        SourceInitialized += (_, _) =>
            AdaptiveWindowSizing.FitDialog(this, 820, 720, 640, 480);
        UpdatedProfile = profile;
        RestoreModelDefaultsButton.IsEnabled = profile.DefaultParameters is not null;
        Populate(profile);
        ApplyRuntimeCapabilities(runtimeCapabilities);
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
        var defaults = ModelProfileFactory.RestoreModelDefaults(_originalProfile) with
        {
            DisplayName = DisplayNameTextBox.Text.Trim(),
        };
        Populate(defaults);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
        => CompleteSave(saveAsModelDefault: false);

    private void SaveAsModelDefaultButton_Click(object sender, RoutedEventArgs e)
        => CompleteSave(saveAsModelDefault: true);

    private void ContextSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContextRiskTextBlock is null || CustomContextSizeTextBox is null)
        {
            return;
        }

        var isCustom = ContextSizeComboBox.SelectedValue is CustomContextSizeChoice;
        CustomContextSizeTextBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        UpdateContextRisk();
    }

    private void CustomContextSizeTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateContextRisk();

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

    private void VisionEnableCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateVisionUiState();

    private void VisionParameter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isPopulatingVision)
        {
            UpdateVisionUiState();
        }
    }

    private void CompleteSave(bool saveAsModelDefault)
    {
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

        if (!TryBuildVisionSettings(out var vision, out var visionError))
        {
            MessageBox.Show(this, visionError, AppLanguageManager.Choose("视觉参数无效", "Invalid Vision Parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mtp.Enabled
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

        var updated = _originalProfile with
        {
            DisplayName = DisplayNameTextBox.Text.Trim(),
            ContextSize = contextSize,
            CompactionSafetyReserve = SelectedValue<int>(CompactionSafetyReserveComboBox),
            GpuLayers = gpuLayers,
            Device = NullWhenWhiteSpace(DeviceTextBox.Text),
            MoeExpertPlacement = moePlacement,
            CpuMoeLayers = cpuMoeLayers,
            FlashAttention = SelectedValue<string>(FlashAttentionComboBox),
            CacheTypeK = SelectedValue<string>(CacheTypeKComboBox),
            CacheTypeV = SelectedValue<string>(CacheTypeVComboBox),
            Parallel = SelectedValue<int>(ParallelComboBox),
            BatchSize = NullWhenZero(SelectedValue<int>(BatchSizeComboBox)),
            MicroBatchSize = NullWhenZero(SelectedValue<int>(MicroBatchSizeComboBox)),
            IdleSleepSeconds = SelectedValue<int>(IdleSleepSecondsComboBox),
            Jinja = JinjaCheckBox.IsChecked == true,
            ChatTemplateRelativePath = NullWhenWhiteSpace(ChatTemplatePathTextBox.Text),
            ExposeReasoningEffortInChatGpt = ReasoningEffortCheckBox.IsEnabled
                && ReasoningEffortCheckBox.IsChecked == true,
            MtpEnabled = mtp.Enabled,
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
            ExtraArguments = extraArguments,
        };
        var errors = ModelProfileValidator.Validate(updated, _runtimeRoot);
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

    private void Populate(ModelProfile profile)
    {
        DisplayNameTextBox.Text = profile.DisplayName;
        AliasTextBox.Text = profile.Alias;
        ModelPathTextBox.Text = profile.ModelRelativePath;
        ModelTypeTextBlock.Text = profile.ModelType switch
        {
            ModelType.Dense => AppLanguageManager.Choose("Dense（稠密模型）", "Dense Model"),
            ModelType.MoE => AppLanguageManager.Choose("MoE（混合专家模型）", "MoE Model"),
            _ => AppLanguageManager.Choose("未指定", "Unspecified"),
        };
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
            CustomContextSizeTextBox.Text = profile.ContextSize.ToString("N0", CultureInfo.CurrentCulture);
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
            profile.CompactionSafetyReserve.ToString("N0"));
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
        PopulateChoices(BatchSizeComboBox, BatchChoices, profile.BatchSize ?? 0, profile.BatchSize?.ToString("N0") ?? "Default");
        PopulateChoices(
            MicroBatchSizeComboBox,
            MicroBatchChoices,
            profile.MicroBatchSize ?? 0,
            profile.MicroBatchSize?.ToString("N0") ?? "Default");
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
        PopulateAdvanced(profile);
        UpdateContextRisk();
    }

    private void PopulateReasoningCapability(ModelProfile profile)
    {
        var verified = profile.ReasoningCapabilityStatus == ReasoningCapabilityStatus.Verified
            && profile.SupportedReasoningLevels is { Count: > 0 };
        ReasoningEffortCheckBox.IsEnabled = verified;
        ReasoningEffortCheckBox.IsChecked = verified && profile.ExposeReasoningEffortInChatGpt;
        ReasoningCapabilityStatusTextBlock.Text = profile.ReasoningCapabilityStatus switch
        {
            ReasoningCapabilityStatus.Unsupported => AppLanguageManager.Choose(
                "llama.cpp 已确认：该模型不支持思考强度调节。",
                "llama.cpp confirmed that this model does not support reasoning effort selection."),
            ReasoningCapabilityStatus.SupportedLevelsUnknown => AppLanguageManager.Choose(
                "llama.cpp 已确认模型支持思考强度，但当前无法识别完整档位，因此暂不允许开启。",
                "llama.cpp confirmed reasoning-effort support, but the complete levels are unavailable, so this option remains disabled."),
            ReasoningCapabilityStatus.Verified when verified => AppLanguageManager.Choose(
                $"已确认档位：{string.Join("、", profile.SupportedReasoningLevels.Select(FormatReasoningLevel))}",
                $"Verified levels: {string.Join(", ", profile.SupportedReasoningLevels.Select(FormatReasoningLevel))}"),
            _ => AppLanguageManager.Choose(
                "尚未取得可验证的思考强度档位；请在本地模型管理中执行检测。",
                "No verifiable reasoning-effort levels are available. Run detection from Local Model Management."),
        };
    }

    private static string FormatReasoningLevel(string level) => level switch
    {
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "XHigh",
        "max" => "Max",
        "ultra" => "Ultra",
        "persistent" => "Persistent",
        _ => level,
    };

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
        if (capabilities is null)
        {
            _runtimeSupportsMtp = null;
            _runtimeSupportsExternalMtp = null;
            _runtimeSupportsContextCheckpoints = null;
            UpdateContextCheckpointsUiState();
            _runtimeSupportsVision = null;
            UpdateMtpUiState();
            UpdateVisionUiState();
            return;
        }

        _runtimeSupportsMtp = capabilities.Contains("spec-type");
        _runtimeSupportsExternalMtp = capabilities.Contains("spec-draft-model");
        _runtimeSupportsContextCheckpoints = capabilities.Contains("ctx-checkpoints")
            || capabilities.Contains("swa-checkpoints");
        _runtimeSupportsVision = capabilities.Contains("mmproj");

        UpdateContextCheckpointsUiState();

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
            control.ToolTip = AppLanguageManager.Choose(
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
            control.ToolTip = AppLanguageManager.Choose(
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
                control.ToolTip = AppLanguageManager.Choose(
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
        VisionSettingsPanel.IsEnabled = enabled && !runtimeBlocked;
        VisionStatusTextBlock.Text = runtimeBlocked
            ? AppLanguageManager.Choose("当前 llama.cpp Runtime 未报告 --mmproj，无法启用视觉模块。", "The current llama.cpp Runtime does not report --mmproj, so vision cannot be enabled.")
            : _originalProfile.VisionCapabilityStatus switch
            {
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
        if (enabled && (_runtimeSupportsVision == false
                        || _originalProfile.VisionCapabilityStatus != VisionCapabilityStatus.Verified))
        {
            settings = default!;
            error = AppLanguageManager.Choose("视觉模块必须先在本地模型管理中通过 GGUF 元数据识别并建立关联。", "Vision must first be recognized from GGUF metadata and associated in Local Model Management.");
            return false;
        }

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
        MtpSettingsPanel.IsEnabled = enabled && !runtimeBlocked;
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

        if (enabled && _runtimeSupportsMtp == false)
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 不支持原生 MTP，请先关闭 MTP 或更新 Runtime。",
                "The current llama.cpp Runtime does not support native MTP. Disable MTP or update the Runtime.");
            return false;
        }

        if (enabled && source == MtpSourceKind.External && _runtimeSupportsExternalMtp == false)
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 不支持加载外部 MTP 模型。",
                "The current llama.cpp Runtime does not support loading an external MTP model.");
            return false;
        }

        if (enabled
            && source == MtpSourceKind.Embedded
            && !_hasEmbeddedMtpCandidate
            && _originalProfile.MtpCapabilityStatus != MtpCapabilityStatus.Verified)
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "没有在主 GGUF 中检测到内置 NextN/MTP 层；请选择匹配的外部 MTP GGUF。",
                "No embedded NextN/MTP layers were detected in the main GGUF. Choose a matching external MTP GGUF.");
            return false;
        }

        if (enabled
            && source == MtpSourceKind.External
            && (string.IsNullOrWhiteSpace(draftPath)
                || _originalProfile.MtpCapabilityStatus != MtpCapabilityStatus.Verified))
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
            && (!int.TryParse(gpuLayers, NumberStyles.None, CultureInfo.InvariantCulture, out var layerCount)
                || layerCount < 0))
        {
            settings = default!;
            error = AppLanguageManager.Choose(
                "外部 MTP GPU Offload 必须是非负整数，或留空使用自动值。",
                "External MTP GPU Offload must be a non-negative integer, or blank for automatic placement.");
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

        if (!TryParseContextSize(CustomContextSizeTextBox.Text, out contextSize))
        {
            error = AppLanguageManager.Choose(
                "请输入正整数 token 数，或使用 K 后缀，例如 98,304 或 96K。",
                "Enter a positive token count or use a K suffix, such as 98,304 or 96K.");
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
                : TryParseContextSize(CustomContextSizeTextBox?.Text, out var customValue)
                    ? (true, customValue)
                    : (false, 0);
        ContextRiskTextBlock.Visibility = hasContextSize.Item1
            && hasContextSize.Item2 < ModelProfile.RecommendedMinimumCodexContext
                ? Visibility.Visible
                : Visibility.Collapsed;
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
            FormatContextSize(value)
            + (modelContextLimit == value
                ? AppLanguageManager.Choose("（模型上限）", " (Model Limit)")
                : string.Empty),
            value)));
        choices.Add(new Choice<int>(AppLanguageManager.Choose("自定义…", "Custom…"), CustomContextSizeChoice));
        return choices;
    }

    private static bool TryParseContextSize(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        if (normalized.EndsWith('K') || normalized.EndsWith('k'))
        {
            var number = normalized[..^1];
            if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var kibibytes))
            {
                return false;
            }

            var tokenCount = kibibytes * 1024;
            if (tokenCount != decimal.Truncate(tokenCount) || tokenCount is <= 0 or > int.MaxValue)
            {
                return false;
            }

            value = decimal.ToInt32(tokenCount);
            return true;
        }

        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }

    private static string FormatContextSize(int value) =>
        value % 1024 == 0
            ? $"{value / 1024:N0}K"
            : $"{value:N0}";

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

    private static int? NullWhenZero(int value) => value == 0 ? null : value;

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
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var key in ManagedAdvancedArgumentKeys)
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
                (AppLanguageManager.Choose("上下文检查点", "Context Checkpoints"), CtxCheckpointsTextBox.Text, true)))
        {
            arguments = extra;
            return false;
        }

        if (_runtimeSupportsContextCheckpoints == false
            && !string.IsNullOrWhiteSpace(CtxCheckpointsTextBox.Text))
        {
            arguments = extra;
            error = AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 不支持 --ctx-checkpoints；清空此项后才能保存。",
                "The current llama.cpp Runtime does not support --ctx-checkpoints. Clear this field before saving.");
            return false;
        }

        AddOptional(extra, "threads", ThreadsTextBox.Text);
        AddOptional(extra, "threads-batch", ThreadsBatchTextBox.Text);
        AddOptional(extra, "ctx-checkpoints", CtxCheckpointsTextBox.Text);
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
            // Keep an existing value editable so the user can remove it after changing
            // to a Runtime that does not expose this option. A non-empty value is rejected
            // during save below.
            CtxCheckpointsTextBox.IsEnabled = !string.IsNullOrWhiteSpace(CtxCheckpointsTextBox.Text);
            CtxCheckpointsTextBox.ToolTip = AppLanguageManager.Choose(
                "当前 llama.cpp Runtime 不支持 --ctx-checkpoints；清空此项后才能保存。",
                "The current llama.cpp Runtime does not support --ctx-checkpoints. Clear this field before saving.");
            return;
        }

        CtxCheckpointsTextBox.IsEnabled = true;
        CtxCheckpointsTextBox.SetResourceReference(FrameworkElement.ToolTipProperty, "ContextCheckpointsTip");
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

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }
}
