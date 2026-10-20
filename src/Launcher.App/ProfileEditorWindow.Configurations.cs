using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Launcher.Models.Profiles;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private readonly Launcher.Core.Diagnostics.DiagnosticJournal _diagnostics;
    private readonly Func<ModelProfile, bool> _canEditConfiguration;
    private readonly Func<bool> _canDetectConfiguration;
    private ModelProfile _registration = null!;
    private string? _configurationPath;
    private string? _configurationDigest;
    private bool _configurationDirty;
    private bool _selectingResources;
    private bool _hasRegisteredVisionResources;
    private bool _allowConfigurationClose;
    private bool _persistingConfiguration;
    private bool IsConfigurationDetectionRunning => _contextShiftCancellation is not null || _reasoningCancellation is not null || _checkingTemplate;
    private sealed record ResourceChoice(string Label, ModelResourceRecord Record)
    {
        public override string ToString() => Label;
    }

    private void InitializeConfigurationFiles()
    {
        _registration = _originalProfile;
        try
        {
            if (_registration.PreferredConfigurationPath is { } selected) EnsureUserConfigurationPath(selected);
            _configurationPath = _originalProfile.ConfigurationError is null && File.Exists(_originalProfile.PreferredConfigurationPath)
                ? _originalProfile.PreferredConfigurationPath : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _registration = _registration with { ConfigurationError = ex.Message };
        }
        _configurationDigest = _configurationPath is null ? null : _originalProfile.ConfigurationContentDigest ?? ModelConfigurationFile.Digest(_configurationPath);
        PopulateResourceChoices();
        RefreshConfigurationNames();
        ProfileEditorTabs.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler((_, e) =>
            {
                if (e.OriginalSource is System.Windows.Controls.TextBox text && !text.IsReadOnly
                    && (!text.Name.StartsWith("PART_", StringComparison.Ordinal) || text.IsKeyboardFocusWithin)) MarkConfigurationDirty();
            }));
        ProfileEditorTabs.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, e) => { if (e.OriginalSource is System.Windows.Controls.ComboBox) MarkConfigurationDirty(); }));
        ProfileEditorTabs.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is System.Windows.Controls.CheckBox) MarkConfigurationDirty(); }));
        ProfileEditorTabs.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is System.Windows.Controls.CheckBox) MarkConfigurationDirty(); }));
        Closing += ConfigurationWindow_Closing;
        Activated += (_, _) =>
        {
            RefreshConfigurationNames();
            RefreshConfigurationResources();
        };
    }

    private void RefreshConfigurationResources()
    {
        if (_persistingConfiguration) return;
        try
        {
            // Refresh proof and choices only; preserve unsaved controls and requested switches.
            _originalProfile = ModelResourceCatalog.Resolve(_originalProfile, _runtimeRoot);
            _displayedMtpCapabilityStatus = _originalProfile.MtpCapabilityStatus;
            PopulateResourceChoices();
            UpdateMtpUiState();
            UpdateVisionUiState();
            UpdateTemplateStatus();
        }
        catch (Exception ex) { TemplateStatusText.Text = ex.Message; }
    }

    private void MarkConfigurationDirty()
    {
        if (_registration is null || _selectingResources || _populatingProfile || _populatingReasoning || _isPopulatingVision || _isPopulatingMtp) return;
        _configurationDirty = true;
        RefreshConfigurationNames();
    }

    private void RefreshConfigurationNames()
    {
        LoadedConfigurationLabel.Text = AppLanguageManager.Choose("载入配置：", "Loaded: ");
        LoadedConfigurationName.Text = _configurationPath is null ? AppLanguageManager.Choose("无", "None") : Path.GetFileNameWithoutExtension(_configurationPath);
        LoadedConfigurationDirtyText.Visibility = _configurationDirty ? Visibility.Visible : Visibility.Collapsed;
        LoadedConfigurationDirtyText.ToolTip = AppLanguageManager.Choose("未保存的修改", "Unsaved changes");
        LoadedConfigurationButton.ToolTip = _configurationPath;
        var state = ModelConfigurationSelection.Inspect(_registration, _runtimeRoot);
        var preferred = state.Path;
        PreferredConfigurationLabel.Text = AppLanguageManager.Choose("启动配置：", "Startup: ");
        PreferredConfigurationName.Text = state.Name ?? AppLanguageManager.Choose("无", "None");
        PreferredConfigurationButton.ToolTip = state.Error is null ? preferred : preferred + Environment.NewLine + state.Error;
        System.Windows.Automation.AutomationProperties.SetName(LoadedConfigurationButton,
            LoadedConfigurationLabel.Text + (_configurationPath is null ? LoadedConfigurationName.Text : Path.GetFileName(_configurationPath))
            + (_configurationDirty ? AppLanguageManager.Choose("（未保存）", " (unsaved)") : ""));
        System.Windows.Automation.AutomationProperties.SetName(PreferredConfigurationButton,
            PreferredConfigurationLabel.Text + (state.Exists ? Path.GetFileName(preferred) : PreferredConfigurationName.Text));
    }

    private void LocateLoadedConfiguration_Click(object sender, RoutedEventArgs e) => LocateConfiguration(_configurationPath);
    private void LocatePreferredConfiguration_Click(object sender, RoutedEventArgs e) => LocateConfiguration(_registration.PreferredConfigurationPath);
    private void LocateConfiguration(string? path)
    {
        try
        {
            if (path is not null) EnsureUserConfigurationPath(path);
            if (path is null || !File.Exists(path)) throw new FileNotFoundException(AppLanguageManager.Choose("配置文件不存在，请重新载入。", "Configuration is missing; load it again."));
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add("/select,");
            info.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(info);
        }
        catch (Exception ex) { ShowConfigurationError(ex); }
    }

    private async Task<bool> PersistConfigurationAsync(bool saveAs, bool apply)
    {
        if (_persistingConfiguration) return false;
        _persistingConfiguration = true;
        await using var operation = _diagnostics.Begin(apply ? "configuration_apply" : saveAs ? "configuration_save_as" : "configuration_save",
            new Dictionary<string, object?> { ["modelId"] = _registration.Id, ["configuration"] = Path.GetFileName(_configurationPath) });
        try
        {
            await operation.StageAsync("check_model_state_and_type");
            await EnsureRegisteredTypeAsync();
            if (!_canEditConfiguration(_registration)) throw new InvalidOperationException(AppLanguageManager.Choose("模型正在运行，不能保存参数。", "The model is running; parameters cannot be saved."));
            if (IsConfigurationDetectionRunning) { await operation.CompleteAsync("blocked", "detection_running"); return false; }
            await operation.StageAsync("validate_edit_parameters");
            operation.SetDetail("validationScope", "structure;ranges;dependencies;path;model_binding;custom_conflicts;startup_checks_deferred");
            _probeProfile = null;
            CompleteSave(false, captureForProbe: true);
            if (_probeProfile is not { } candidate) { await operation.CompleteAsync("blocked", "parameters_rejected"); return false; }
            await operation.StageAsync("validate_custom_parameters");
            var customErrors = ValidateCustomParametersForSave(candidate);
            if (customErrors.Count > 0)
            {
                operation.Set("validationErrors", customErrors);
                await operation.CompleteAsync("blocked", "custom_parameters_rejected");
                MessageBox.Show(this, string.Join(Environment.NewLine, customErrors), AppLanguageManager.Choose("自定参数无效", "Invalid custom parameters"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            operation.Set("llamaCustomNames", candidate.CustomParameters.Llama.Select(r => r.Name).ToArray());
            operation.Set("codexCustomNames", candidate.CustomParameters.Codex.Select(r => r.Name).ToArray());
            var path = _configurationPath;
            if (saveAs || path is null)
            {
                var dialog = CreateConfigurationSaveDialog(saveAs);
                if (dialog.ShowDialog(this) != true) return false;
                path = Path.GetFullPath(dialog.FileName);
            }
            operation.Set("configuration", Path.GetFileName(path));
            await operation.StageAsync("validate_path_and_binding");
            EnsureUserConfigurationPath(path);
            await EnsureRegisteredTypeAsync();
            if (!_canEditConfiguration(_registration)) throw new InvalidOperationException("模型运行状态已改变 / Model state changed.");
            await operation.StageAsync("write_configuration");
            await ModelConfigurationFile.WriteAsync(path, ModelConfigurationFile.Bind(_registration, candidate), _runtimeRoot,
                requireExisting: !saveAs && _configurationPath is not null,
                expectedDigest: !saveAs && path == _configurationPath ? _configurationDigest : null);
            _configurationPath = path;
            _configurationDigest = ModelConfigurationFile.Digest(path);
            _configurationDirty = false;
            UpdatedProfile = candidate;
            operation.Set("configurationSaved", true);
            if (apply)
            {
                await operation.StageAsync("set_startup_preselection");
                // Only the per-model pointer is committed. Global model/mode selection is untouched.
                var next = await new JsonModelProfileStore().UpdatePreferredConfigurationAsync(_runtimeRoot, _registration, path,
                    () => Dispatcher.CheckAccess() ? _canEditConfiguration(_registration)
                        : Dispatcher.Invoke(() => _canEditConfiguration(_registration)));
                _registration = next;
            }
            _originalProfile = ModelConfigurationFile.Bind(_registration, candidate);
            RefreshConfigurationNames();
            await operation.CompleteAsync();
            return true;
        }
        catch (Exception ex) { await operation.FailAsync(ex); ShowConfigurationError(ex); return false; }
        finally { _persistingConfiguration = false; }
    }

    private string PrepareConfigurationDirectory()
    {
        var directory = ModelConfigurationFile.GetDirectory(_registration, _runtimeRoot);
        EnsureUserConfigurationPath(Path.Combine(directory, "未命名.json"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void EnsureUserConfigurationPath(string path) => ModelConfigurationFile.ValidatePath(path, _registration, _runtimeRoot);

    private Microsoft.Win32.SaveFileDialog CreateConfigurationSaveDialog(bool saveAs)
    {
        var directory = PrepareConfigurationDirectory();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = AppLanguageManager.Text("SaveNewConfiguration"),
            Filter = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = directory,
            RootDirectory = directory,
            FileName = saveAs ? "" : "未命名.json",
        };
        dialog.FileOk += ConfigurationDialog_FileOk;
        return dialog;
    }

    private Microsoft.Win32.OpenFileDialog CreateConfigurationOpenDialog()
    {
        var directory = PrepareConfigurationDirectory();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguageManager.Text("LoadConfiguration"),
            Filter = "JSON (*.json)|*.json",
            CheckFileExists = true,
            InitialDirectory = directory,
            RootDirectory = directory,
        };
        dialog.FileOk += ConfigurationDialog_FileOk;
        return dialog;
    }

    private void ConfigurationDialog_FileOk(object? sender, CancelEventArgs e)
    {
        try { EnsureUserConfigurationPath(((Microsoft.Win32.FileDialog)sender!).FileName); }
        catch (Exception ex) { e.Cancel = true; ShowConfigurationError(ex); }
    }

    private async void ApplyConfiguration_Click(object sender, RoutedEventArgs e) => await PersistConfigurationAsync(false, true);
    private async void LoadConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_persistingConfiguration || IsConfigurationDetectionRunning || !_canEditConfiguration(_registration) || !await ConfirmDiscardConfigurationAsync()) return;
        try
        {
            var dialog = CreateConfigurationOpenDialog();
            if (dialog.ShowDialog(this) != true) return;
            await LoadConfigurationFileAsync(dialog.FileName);
        }
        catch (Exception ex) { ShowConfigurationError(ex); }
    }

    private async Task LoadConfigurationFileAsync(string path) =>
        await _diagnostics.RunAsync("configuration_load", new Dictionary<string, object?> { ["modelId"] = _registration.Id, ["configuration"] = Path.GetFileName(path) },
            async operation => { await LoadConfigurationFileCoreAsync(path, operation); return true; });

    private async Task LoadConfigurationFileCoreAsync(string path, Launcher.Core.Diagnostics.DiagnosticOperation operation)
    {
        if (IsConfigurationDetectionRunning) throw new InvalidOperationException(AppLanguageManager.Choose(
            "请等待能力检测结束或取消检测后，再载入配置。", "Finish or cancel capability detection before loading a configuration."));
        await operation.StageAsync("validate_configuration_path");
        EnsureUserConfigurationPath(path);
        await operation.StageAsync("check_registered_model_type");
        await EnsureRegisteredTypeAsync();
        var digest = ModelConfigurationFile.Digest(path);
        await operation.StageAsync("read_and_bind_configuration");
        var profile = await ModelConfigurationFile.ReadAsync(path, _registration, _runtimeRoot);
        await operation.StageAsync("resolve_resource_choices");
        profile = ModelResourceCatalog.Resolve(profile, _runtimeRoot);
        if (digest != ModelConfigurationFile.Digest(path)) throw new IOException("载入期间配置发生变化，请重试 / Configuration changed while loading.");
        _selectingResources = true;
        try { _originalProfile = profile; _reasoningProfile = profile; Populate(profile); PopulateResourceChoices(); }
        finally { _selectingResources = false; }
        _configurationPath = Path.GetFullPath(path);
        _configurationDigest = digest;
        _configurationDirty = false;
        _effortWhileThinkingOffAccepted = false;
        RefreshConfigurationNames();
    }

    private async Task EnsureRegisteredTypeAsync()
    {
        var current = await new JsonModelProfileStore().ReadRegistrationAsync(_runtimeRoot, _registration);
        if (current.ModelType != _registration.ModelType)
            throw new InvalidDataException(ModelConfigurationFile.TypeMismatchPrefix + Environment.NewLine
                + AppLanguageManager.Choose("模型登记类型已改变，请关闭并重新打开参数页面。", "The registered type changed. Close and reopen parameter settings."));
        // Presentation belongs to registration, including when another window changed it.
        _registration = _registration with { DisplayName = current.DisplayName };
    }

    private async Task<bool> ConfirmDiscardConfigurationAsync()
    {
        if (!_configurationDirty) return true;
        var answer = MessageBox.Show(this, AppLanguageManager.Choose("保存当前修改？选择否将放弃未保存的修改。", "Save changes? No discards unsaved edits."),
            AppLanguageManager.Text("ModelParameters"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && await PersistConfigurationAsync(false, false);
    }

    private void CancelConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_persistingConfiguration) return;
        // Cancel discards this editing session without entering the window-close save prompt.
        _allowConfigurationClose = true;
        Close();
    }
    private async void ConfigurationWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowConfigurationClose) return;
        e.Cancel = true;
        await Task.Yield();
        if (_persistingConfiguration || !await ConfirmDiscardConfigurationAsync()) return;
        _allowConfigurationClose = true;
        Close();
    }

    private void ShowConfigurationError(Exception ex) => MessageBox.Show(this, ex.Message,
        AppLanguageManager.Text("ModelParameters"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private void PopulateResourceChoices()
    {
        var previous = _selectingResources;
        _selectingResources = true;
        try
        {
            var records = ModelResourceCatalog.Read(_originalProfile, _runtimeRoot).Records.Where(r => ModelResourceCatalog.IsCurrent(r, _runtimeRoot)).ToList();
            _hasRegisteredVisionResources = records.Any(r => r.Kind == ModelResourceKind.Vision);
            var embedded = _originalProfile with { ChatTemplateRelativePath = null };
            FillResourceChoices(TemplateResourceComboBox, records.Where(r => r.Kind == ModelResourceKind.Template),
                new(ModelResourceKind.Template, embedded), _originalProfile.ChatTemplateRelativePath, ModelResourceKind.Template);
            FillResourceChoices(VisionResourceComboBox, records.Where(r => r.Kind == ModelResourceKind.Vision),
                null, _originalProfile.VisionProjectorRelativePath, ModelResourceKind.Vision);
            FillResourceChoices(MtpResourceComboBox, records.Where(r => r.Kind == ModelResourceKind.Mtp),
                _hasEmbeddedMtpCandidate ? new(ModelResourceKind.Mtp, _originalProfile with { MtpSource = MtpSourceKind.Embedded, MtpDraftModelRelativePath = null, MtpCapabilityStatus = MtpCapabilityStatus.EmbeddedCandidate }) : null,
                _originalProfile.MtpDraftModelRelativePath, ModelResourceKind.Mtp);
        }
        finally { _selectingResources = previous; }
    }

    private void FillResourceChoices(ComboBox combo, IEnumerable<ModelResourceRecord> records, ModelResourceRecord? builtIn, string? selectedPath, ModelResourceKind kind)
    {
        var choices = records.GroupBy(r => r.ResourcePath is null ? "" : Path.GetFullPath(Path.Combine(_runtimeRoot, r.ResourcePath)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).Select(r => new ResourceChoice(r.ResourcePath is null ? AppLanguageManager.Choose("内置", "Built-in") : r.Label, r)).ToList();
        if (builtIn is not null && !choices.Any(c => c.Record.ResourcePath is null)) choices.Insert(0, new(AppLanguageManager.Choose("内置", "Built-in"), builtIn));
        if (kind != ModelResourceKind.Template && (!choices.Any(c => c.Record.ResourcePath is null)
            || kind == ModelResourceKind.Vision && !choices.Any(c => c.Record.ResourcePath is null && c.Record.Evidence.VisionSource == VisionSourceKind.External)))
        {
            var none = _originalProfile with
            {
                MtpSource = MtpSourceKind.External,
                MtpDraftModelRelativePath = null,
                MtpCapabilityStatus = MtpCapabilityStatus.Unknown,
                MtpValidationSignature = null,
                MtpValidatedAtUtc = null,
                VisionSource = VisionSourceKind.External,
                VisionProjectorRelativePath = null,
                VisionCapabilityStatus = VisionCapabilityStatus.Unknown,
                VisionValidationSignature = null,
                VisionValidatedAtUtc = null,
            };
            choices.Insert(0, new(AppLanguageManager.Choose("未选择", "None"), new(kind, none)));
        }
        if (kind == ModelResourceKind.Template)
        {
            var recommended = ModelResourceCatalog.Read(_originalProfile, _runtimeRoot).DefaultTemplatePath;
            choices = choices.Select(c => c.Record.ResourcePath is not null && ModelResourceManagement.SamePath(c.Record.ResourcePath, recommended, _runtimeRoot)
                ? c with { Label = c.Label + AppLanguageManager.Choose("（默认）", " (default)") } : c).ToList();
        }
        var selected = choices.FirstOrDefault(c => ModelResourceManagement.SamePath(c.Record.ResourcePath, selectedPath, _runtimeRoot)
            && (kind != ModelResourceKind.Vision || c.Record.Evidence.VisionSource == _originalProfile.VisionSource));
        if (selected is null && kind == ModelResourceKind.Vision && selectedPath is null)
            selected = choices.FirstOrDefault(c => c.Record.ResourcePath is null && c.Record.Evidence.VisionSource == VisionSourceKind.External);
        if (selected is null)
        {
            selected = new(AppLanguageManager.Choose("未选择或不可用", "None or unavailable"), new(kind, _originalProfile));
            choices.Add(selected);
        }
        combo.ItemsSource = choices;
        combo.SelectedItem = selected;
        combo.ToolTip = AppLanguageManager.Text("ParameterHelp_" + combo.Name) + Environment.NewLine + selectedPath;
    }

    private void TemplateResource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingResources || TemplateResourceComboBox.SelectedItem is not ResourceChoice choice) return;
        ChatTemplatePathTextBox.Text = choice.Record.ResourcePath ?? "";
        _originalProfile = ModelResourceCatalog.Resolve(_originalProfile with
        {
            ChatTemplateRelativePath = choice.Record.ResourcePath,
            Jinja = JinjaCheckBox.IsChecked == true,
            ThinkingEnabled = _reasoningProfile.ThinkingEnabled,
            ShowThinkingProcess = ShowThinkingProcessCheckBox.IsChecked == true,
            ExposeReasoningEffortInChatGpt = ReasoningEffortCheckBox.IsChecked == true,
            PreferredReasoningLevel = _reasoningProfile.PreferredReasoningLevel,
            ReverseReasoningLevelDisplayOrder = ReverseReasoningOrderCheckBox.IsChecked == true,
        }, _runtimeRoot);
        _reasoningProfile = _originalProfile;
        RefreshReasoningUi();
        PopulateResourceChoices();
        UpdateVisionUiState();
        UpdateTemplateStatus();
        MarkConfigurationDirty();
    }

    private void VisionResource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingResources || VisionResourceComboBox.SelectedItem is not ResourceChoice choice) return;
        _originalProfile = ModelResourceCatalog.Resolve(_originalProfile with { VisionSource = choice.Record.Evidence.VisionSource, VisionProjectorRelativePath = choice.Record.ResourcePath }, _runtimeRoot);
        VisionProjectorPathTextBox.Text = choice.Record.ResourcePath ?? "";
        UpdateVisionUiState();
        MarkConfigurationDirty();
    }

    private void MtpResource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingResources || MtpResourceComboBox.SelectedItem is not ResourceChoice choice) return;
        _originalProfile = ModelResourceCatalog.Resolve(_originalProfile with { MtpSource = choice.Record.Evidence.MtpSource, MtpDraftModelRelativePath = choice.Record.ResourcePath, MtpCapabilityStatus = choice.Record.Evidence.MtpCapabilityStatus }, _runtimeRoot);
        _displayedMtpCapabilityStatus = _originalProfile.MtpCapabilityStatus;
        MtpDraftModelPathTextBox.Text = choice.Record.ResourcePath ?? "";
        UpdateMtpUiState();
        MarkConfigurationDirty();
    }
}
