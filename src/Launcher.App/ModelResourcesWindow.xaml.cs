using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public enum ModelResourceAction { GenerateTemplate, AddTemplate, LoadMtp, LoadVision }

public partial class ModelResourcesWindow : Window
{
    private readonly Launcher.Core.Diagnostics.DiagnosticJournal _diagnostics;
    private ModelProfile _model;
    private readonly string _root;
    private readonly Func<bool> _canMutate;
    private readonly Func<ModelResourceAction, Task<string>> _action;
    private readonly bool _embeddedMtp;
    private readonly bool _embeddedVision;
    private readonly Func<ModelType, Task<ModelProfile>>? _saveType;
    private readonly Func<string, Task<ModelProfile>>? _saveName;
    private readonly ModelType _detectedType;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _working;
    private string? _snapshot;

    private sealed record ResourceRow(string Name, string Status, string? FullPath, ModelResourceKind Kind, string? Path, bool CanRemove)
    {
        public string RemoveLabel => AppLanguageManager.Choose("移除", "Remove");
        public string RemoveTip => AppLanguageManager.Choose("从当前模型的可选资源中移除，保留硬盘文件。", "Remove registration for this model; keep the file on disk.");
        public Visibility RemoveVisibility => Path is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public ModelResourcesWindow(ModelProfile model, string root, Func<bool> canMutate,
        Func<ModelResourceAction, Task<string>> action, bool embeddedMtp, bool embeddedVision,
        Func<ModelType, Task<ModelProfile>>? saveType = null, Func<string, Task<ModelProfile>>? saveName = null, Launcher.Core.Diagnostics.DiagnosticJournal? diagnostics = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => AdaptiveWindowSizing.FitDialog(this, 860, 620, 640, 460);
        _diagnostics = diagnostics ?? Launcher.Core.Diagnostics.DiagnosticJournal.None;
        _model = model;
        _root = root;
        _canMutate = canMutate;
        _action = action;
        _embeddedMtp = embeddedMtp;
        _embeddedVision = embeddedVision;
        _saveType = saveType;
        _saveName = saveName;
        try { _detectedType = GgufContextMetadataReader.ReadMetadata(Path.Combine(root, model.ModelRelativePath)).DetectModelType(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        { _detectedType = ModelType.Unknown; }
        Title = AppLanguageManager.Text("ModelResourceManagement") + " — " + model.DisplayName;
        ModelNameText.Text = model.DisplayName;
        ModelNameText.ToolTip = model.DisplayName;
        DescriptionText.Text = AppLanguageManager.Choose("管理模型信息、类型与各套配置可选的资源。管理操作不会改写配置文件。", "Manage model information, type and resources shared by its configurations. Management operations do not edit configuration files.");
        ModelFileNameLabel.Text = AppLanguageManager.Choose("模型文件名", "Model filename");
        CardDisplayNameTextBox.Text = model.DisplayName;
        SaveTypeButton.Content = AppLanguageManager.Choose("保存类型", "Save type");
        SaveTypeButton.ToolTip = AppLanguageManager.Choose("仅更新模型类型登记，不修改已有配置。", "Update the registered type only; keep existing configurations unchanged.");
        TypeSelectionHint.Text = AppLanguageManager.Choose("保存后只更新模型类型，已有配置文件保持原样。类型不同的配置无法载入或启动，改回原类型后可继续使用。", "Saving updates registration only. Existing configuration files stay unchanged. Other-type configurations cannot load or start; switch back to use them again.");
        ResetTypeSelection();
        TemplateTab.Header = AppLanguageManager.Choose("模板管理", "Templates");
        MtpTab.Header = AppLanguageManager.Choose("MTP 文件管理", "MTP files");
        VisionTab.Header = AppLanguageManager.Choose("视觉模块管理", "Vision modules");
        Activated += (_, _) => RefreshResources();
        _timer.Tick += (_, _) => { if (!_working) RefreshResources(); };
        Loaded += (_, _) => { RefreshResources(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        Closing += (_, e) => { if (_working) e.Cancel = true; };
        RefreshResources();
    }

    private void RefreshResources()
    {
        var allowed = !_working && _canMutate();
        RefreshModelInformation(allowed);
        RefreshTypeUi(allowed);
        GenerateButton.IsEnabled = AddTemplateButton.IsEnabled = LoadMtpButton.IsEnabled = LoadVisionButton.IsEnabled = allowed;
        CloseButton.IsEnabled = !_working;
        AvailabilityText.Text = _working ? AppLanguageManager.Choose("正在处理，请等待完成。", "Working; wait for completion.")
            : allowed ? ResourceTabs.SelectedItem == ModelInformationTab
                ? AppLanguageManager.Choose("显示名称仅用于展示，不改变模型身份或配置绑定。", "The display name changes presentation only, not model identity or configuration bindings.")
                : ResourceTabs.SelectedItem == ModelTypeTab
                ? AppLanguageManager.Choose("选择类型后点击“保存类型”生效。", "Choose a type and select Save type to make it effective.")
                : AppLanguageManager.Choose("移除仅清理登记；被配置引用的资源需要先修改相关配置。", "Removal keeps source files. Update referencing configurations before removing a registration.")
            : AppLanguageManager.Choose("当前仅可查看。请停止本地服务、检测及下载后再修改。", "View only. Stop local services, detection and downloads before making changes.");
        try
        {
            var records = ModelResourceCatalog.Read(_model, _root).Records;
            var templates = Rows(ModelResourceKind.Template, records, allowed);
            var mtp = Rows(ModelResourceKind.Mtp, records, allowed);
            var vision = Rows(ModelResourceKind.Vision, records, allowed);
            var snapshot = string.Join("\n", templates.Concat(mtp).Concat(vision).Select(row => row.ToString()));
            if (snapshot == _snapshot) return;
            TemplateRows.ItemsSource = templates;
            MtpRows.ItemsSource = mtp;
            VisionRows.ItemsSource = vision;
            _snapshot = snapshot;
        }
        catch (Exception ex)
        {
            TemplateRows.ItemsSource = MtpRows.ItemsSource = VisionRows.ItemsSource = null;
            GenerateButton.IsEnabled = AddTemplateButton.IsEnabled = LoadMtpButton.IsEnabled = LoadVisionButton.IsEnabled = false;
            ResultText.Text = ex.Message;
            _snapshot = null;
        }
    }

    private static string TypeName(ModelType type) => type switch
    {
        ModelType.Dense => "Dense",
        ModelType.MoE => "MoE",
        _ => AppLanguageManager.Choose("未知", "Unknown"),
    };

    private void RefreshModelInformation(bool allowed)
    {
        var path = Path.GetFullPath(Path.Combine(_root, _model.ModelRelativePath));
        ModelFileNameText.Text = Path.GetFileName(path);
        ModelFileNameText.ToolTip = ModelFileNameText.Text;
        ModelFilePathText.Text = path;
        ModelFilePathText.ToolTip = path;
        var exists = File.Exists(path);
        long? size = _model.KnownSizeBytes;
        if (size is null && exists)
        {
            try { size = new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var sizeText = size is { } bytes ? FormatModelSize(bytes) : AppLanguageManager.Choose("未知", "Unknown");
        var source = _model.SourceKind == ModelSourceKind.LlamaCache ? AppLanguageManager.Choose("llama 下载缓存", "llama download cache") : AppLanguageManager.Choose("本地文件", "Local file");
        var lines = new List<string>
        {
            AppLanguageManager.Choose("模型类型：", "Model type: ") + TypeName(_model.ModelType),
            AppLanguageManager.Choose("来源：", "Source: ") + source,
            AppLanguageManager.Choose("主文件状态：", "Main file status: ") + (exists ? AppLanguageManager.Choose("存在", "Exists") : AppLanguageManager.Choose("不存在", "Missing")),
            AppLanguageManager.Choose("已知总大小：", "Known total size: ") + sizeText + (_model.KnownSizeBytes is null && size is not null ? AppLanguageManager.Choose("（仅主文件）", " (main file only)") : ""),
            AppLanguageManager.Choose("已知分片：", "Known shards: ") + (_model.KnownShardCount?.ToString() ?? AppLanguageManager.Choose("未知", "Unknown")),
            AppLanguageManager.Choose("内部 ID：", "Internal ID: ") + _model.Id,
            AppLanguageManager.Choose("内部 Alias：", "Internal Alias: ") + _model.Alias,
        };
        if (!string.IsNullOrWhiteSpace(_model.RemoteModelId)) lines.Add(AppLanguageManager.Choose("远程模型 ID：", "Remote model ID: ") + _model.RemoteModelId);
        ModelFactsText.Text = string.Join(Environment.NewLine, lines);
        CardDisplayNameTextBox.IsReadOnly = !allowed || _saveName is null;
        var name = CardDisplayNameTextBox.Text.Trim();
        SaveNameButton.IsEnabled = allowed && _saveName is not null && name.Length > 0 && !name.Any(char.IsControl) && name != _model.DisplayName;
    }

    private static string FormatModelSize(long bytes)
    {
        var value = (double)bytes;
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N2} {units[unit]}";
    }

    private void CardDisplayName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_model is not null && _canMutate is not null && ModelFactsText is not null) RefreshModelInformation(!_working && _canMutate());
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e)
    {
        if (_saveName is null || !SaveNameButton.IsEnabled) return;
        var name = CardDisplayNameTextBox.Text.Trim();
        await RunAsync(async () =>
        {
            var previousType = _model.ModelType;
            _model = await _diagnostics.RunAsync("model_display_name_save", new Dictionary<string, object?> { ["modelId"] = _model.Id },
                _ => _saveName(name));
            CardDisplayNameTextBox.Text = _model.DisplayName;
            Title = AppLanguageManager.Text("ModelResourceManagement") + " — " + _model.DisplayName;
            ModelNameText.Text = _model.DisplayName;
            ModelNameText.ToolTip = _model.DisplayName;
            if (_model.ModelType != previousType) ResetTypeSelection();
            ResultText.Text = AppLanguageManager.Choose("显示名称已保存；模型身份和所有配置文件保持原样。", "Display name saved; model identity and all configuration files were kept unchanged.");
        });
    }

    private ModelType? ChosenType => DenseTypeRadio.IsChecked == true ? ModelType.Dense : MoeTypeRadio.IsChecked == true ? ModelType.MoE : null;

    private void ResetTypeSelection()
    {
        DenseTypeRadio.IsChecked = _model.ModelType == ModelType.Dense;
        MoeTypeRadio.IsChecked = _model.ModelType == ModelType.MoE;
    }

    private void RefreshTypeUi(bool allowed)
    {
        var source = _model.ModelTypeSource == ModelTypeSource.Detected ? AppLanguageManager.Choose("自动识别", "Detected")
            : _model.ModelTypeSource == ModelTypeSource.UserSelected ? AppLanguageManager.Choose("用户指定", "User selected") : AppLanguageManager.Choose("旧登记", "Legacy");
        DetectedTypeText.Text = AppLanguageManager.Choose("自动识别结果：", "Detected type: ") + TypeName(_detectedType);
        CurrentTypeText.Text = AppLanguageManager.Choose("当前采用类型：", "Registered type: ") + TypeName(_model.ModelType) + " · " + source;
        var preferred = _model.PreferredConfigurationPath;
        TypeConfigurationText.Text = AppLanguageManager.Choose("下次启动配置：", "Next run config: ") + (File.Exists(preferred)
            ? Path.GetFileName(preferred) + (ModelConfigurationFile.IsTypeMismatch(_model.ConfigurationError)
                ? AppLanguageManager.Choose("（类型不匹配）", " (type mismatch)") : _model.ConfigurationError is not null ? AppLanguageManager.Choose("（不可用）", " (unavailable)") : "")
            : AppLanguageManager.Choose("无", "None"));
        TypeConfigurationText.ToolTip = preferred + (_model.ConfigurationError is { } error ? Environment.NewLine + error : "");
        DenseTypeRadio.IsEnabled = MoeTypeRadio.IsEnabled = allowed && _saveType is not null;
        SaveTypeButton.IsEnabled = allowed && _saveType is not null && ChosenType is { } selected && selected != _model.ModelType;
        TypeDetectionWarning.Text = _detectedType != ModelType.Unknown && ChosenType is { } choice && choice != _detectedType
            ? AppLanguageManager.Choose("所选类型与自动识别结果不同；此选择仅改变启动器参数分类，不改变 GGUF 的实际架构。", "The selected type differs from detection. It changes launcher parameter categories, not the GGUF architecture.") : "";
    }

    private void TypeChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (_canMutate is not null && _model is not null) RefreshTypeUi(!_working && _canMutate());
    }

    private void ResourceTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ResourceTabs) && _canMutate is not null && _model is not null) RefreshResources();
    }

    private async void SaveType_Click(object sender, RoutedEventArgs e)
    {
        if (_saveType is null || ChosenType is not { } selected || selected == _model.ModelType) return;
        await RunAsync(async () =>
        {
            _model = await _diagnostics.RunAsync("model_type_save", new Dictionary<string, object?> { ["modelId"] = _model.Id, ["previousType"] = _model.ModelType.ToString(), ["requestedType"] = selected.ToString() },
                _ => _saveType(selected));
            ResetTypeSelection();
            ResultText.Text = AppLanguageManager.Choose("模型类型已保存；所有配置文件保持原样。", "Model type saved; all configuration files were kept unchanged.");
        });
    }

    private List<ResourceRow> Rows(ModelResourceKind kind, IReadOnlyList<ModelResourceRecord> records, bool allowed)
    {
        var relevant = records.Where(record => record.Kind == kind).ToArray();
        var builtInVerified = relevant.Any(record => record.ResourcePath is null && ModelResourceCatalog.IsCurrent(record, _root));
        var builtInName = kind switch
        {
            ModelResourceKind.Template => AppLanguageManager.Choose("GGUF 内置模板", "Embedded GGUF template"),
            ModelResourceKind.Mtp => AppLanguageManager.Choose("内置 MTP", "Embedded MTP"),
            _ => AppLanguageManager.Choose("内置视觉能力", "Built-in vision"),
        };
        var builtInStatus = builtInVerified ? VerifiedStatus(kind) : kind switch
        {
            ModelResourceKind.Template => AppLanguageManager.Choose("沿用 GGUF 内置模板；未取得有效渲染检查记录。", "Uses the embedded template; no current rendering check record."),
            ModelResourceKind.Mtp when _embeddedMtp => AppLanguageManager.Choose("检测到内置 NextN/MTP；沿用正式启动时的加载确认流程。", "Embedded NextN/MTP detected; confirmed through normal model startup."),
            ModelResourceKind.Mtp => AppLanguageManager.Choose("未检测到内置 MTP。", "No embedded MTP detected."),
            _ when _embeddedVision => AppLanguageManager.Choose("检测到内置视觉元数据；可通过加载视觉模块确认关联。", "Built-in vision metadata detected; use Load Vision Module to confirm the association."),
            _ => AppLanguageManager.Choose("未确认内置视觉能力。", "No built-in vision confirmed."),
        };
        var result = new List<ResourceRow> { new(builtInName, builtInStatus, null, kind, null, false) };
        foreach (var group in relevant.Where(record => record.ResourcePath is not null)
                     .GroupBy(record => System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, record.ResourcePath!)), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var valid = group.Where(record => ModelResourceCatalog.IsCurrent(record, _root)).ToArray();
            var status = !File.Exists(group.Key) ? AppLanguageManager.Choose("文件缺失", "File missing")
                : valid.Length == 0 ? AppLanguageManager.Choose("验证依据已失效，需重新添加并检测", "Validation is stale; add and check again") : VerifiedStatus(kind);
            if (kind == ModelResourceKind.Vision && valid.Length > 0)
                status += AppLanguageManager.Choose(" · 适用模板：", " · Templates: ") + string.Join(" / ", valid.Select(record =>
                    record.Evidence.ChatTemplateRelativePath is { } path ? System.IO.Path.GetFileName(path) : AppLanguageManager.Choose("内置", "Built-in")).Distinct());
            result.Add(new(System.IO.Path.GetFileName(group.Key), status, group.Key, kind, group.First().ResourcePath, allowed));
        }
        return result;
    }

    private static string VerifiedStatus(ModelResourceKind kind) => kind switch
    {
        ModelResourceKind.Template => AppLanguageManager.Choose("渲染检查通过，实际对话待验证", "Rendering checks passed; inference not verified"),
        ModelResourceKind.Mtp => AppLanguageManager.Choose("加载验证通过", "Loading verified"),
        _ => AppLanguageManager.Choose("已通过 GGUF 元数据识别", "Recognized from GGUF metadata"),
    };

    private async Task RunAsync(Func<Task> operation)
    {
        if (_working) return;
        if (!_canMutate()) { RefreshResources(); return; }
        _working = true;
        RefreshResources();
        try { await operation(); }
        catch (Exception ex) { ResultText.Text = ex.Message; MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _working = false; RefreshResources(); }
    }

    private async Task PerformAsync(ModelResourceAction action) => await RunAsync(async () => ResultText.Text = await _action(action));
    private async void Generate_Click(object sender, RoutedEventArgs e) => await PerformAsync(ModelResourceAction.GenerateTemplate);
    private async void AddTemplate_Click(object sender, RoutedEventArgs e) => await PerformAsync(ModelResourceAction.AddTemplate);
    private async void LoadMtp_Click(object sender, RoutedEventArgs e) => await PerformAsync(ModelResourceAction.LoadMtp);
    private async void LoadVision_Click(object sender, RoutedEventArgs e) => await PerformAsync(ModelResourceAction.LoadVision);

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResourceRow { Path: not null } row }) return;
        await RunAsync(async () =>
        {
            if (MessageBox.Show(this, AppLanguageManager.Choose($"移除 {row.Name} 的登记？\n硬盘文件会保留。", $"Remove registration for {row.Name}?\nThe file on disk will be kept."),
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await _diagnostics.RunAsync("resource_remove", new Dictionary<string, object?> { ["modelId"] = _model.Id, ["resourceKind"] = row.Kind.ToString(), ["resource"] = Path.GetFileName(row.Path) },
                async operation => { await operation.StageAsync("check_references_and_remove_registration"); await ModelResourceManagement.RemoveAsync(_model, _root, row.Kind, row.Path, _canMutate); return true; });
            ResultText.Text = AppLanguageManager.Choose("已移除登记，硬盘文件已保留。", "Registration removed; the source file was kept.");
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) { if (!_working) Close(); }
}
