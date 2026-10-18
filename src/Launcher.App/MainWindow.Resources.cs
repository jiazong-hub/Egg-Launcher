using System.IO;
using System.Windows;
using Launcher.Models.Profiles;
using Launcher.Scripts.Templates;

namespace Launcher.App;

public partial class MainWindow
{
    private bool CanRunModelResourceDetection() => !HasActiveOrQueuedDownloads && !IsAgentRunning()
        && !IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot)
        && !(_settings.SelectedMode == Launcher.Core.Configuration.ProviderMode.Local && GetClientRunningSnapshot(force: true));

    private ModelResourcesWindow? _resourceWindow;
    private Window ResourceDialogOwner => (Window?)_resourceWindow ?? this;

    private bool CanMutateModelResources(ModelProfile model) => Dispatcher.CheckAccess()
        ? !_busy && CanRunModelResourceDetection() && CanEditProfile(model, forceClientRefresh: true)
        : Dispatcher.Invoke(() => CanMutateModelResources(model));

    private async void ModelResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedModelsList.SelectedItem is ModelListItem { Profile: not null } selected)
            await OpenModelResourcesAsync(selected.Profile);
    }

    private async Task OpenModelResourcesAsync(ModelProfile model)
    {
        if (_busy || _launchInProgress || _modelDialogOpening || _configurationSelectionInProgress || _resourceWindow is not null || _settings.LlamaRoot is not { } root) return;
        _modelDialogOpening = true;
        RefreshEggUiState();
        try
        {
            model = await _profileStore.ReadRegistrationAsync(root, model, _lifetime.Token);
            model = await ReadSelectedConfigurationForEditorAsync(model, root);
            await ModelResourceCatalog.EnsureMigratedAsync(model, root, _lifetime.Token);
            _resourceWindow = new ModelResourcesWindow(model, root,
                () => CanMutateModelResources(model),
                async action =>
                {
                    if (_busy || !CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
                        throw new InvalidOperationException(AppLanguageManager.Choose("请先停止本地服务和检测。", "Stop local services and detection first."));
                    var current = ModelResourceCatalog.Resolve(model, root);
                    if (ModelConfigurationFile.IsTypeMismatch(current.ConfigurationError)) current = ModelConfigurationFile.CreateUnloadedDraft(current);
                    StatusText.Text = AppLanguageManager.Choose("资源登记未改变。", "Resource registrations unchanged.");
                    switch (action)
                    {
                        case ModelResourceAction.GenerateTemplate: await ManageTemplateAsync(current, false); break;
                        case ModelResourceAction.AddTemplate: await ManageTemplateAsync(current, true); break;
                        case ModelResourceAction.LoadMtp: await LoadExternalMtpAsync(current); break;
                        case ModelResourceAction.LoadVision: await LoadExternalVisionAsync(current); break;
                    }
                    return StatusText.Text;
                }, DetectEmbeddedMtp(model, root), DetectBuiltInVision(model, root),
                async type =>
                {
                    var updated = await _profileStore.UpdateModelTypeAsync(root, model, type, () => CanMutateModelResources(model), _lifetime.Token);
                    try { model = await ModelConfigurationFile.ReadAsync(updated.PreferredConfigurationPath ?? "", updated, root, _lifetime.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                    { model = updated with { ConfigurationError = ex.Message }; }
                    return model;
                },
                async name =>
                {
                    var updated = await _profileStore.UpdateDisplayNameAsync(root, model, name, () => CanMutateModelResources(model), _lifetime.Token);
                    try { model = await ModelConfigurationFile.ReadAsync(updated.PreferredConfigurationPath ?? "", updated, root, _lifetime.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                    { model = updated with { ConfigurationError = ex.Message }; }
                    _profiles = _profiles.Select(profile => profile.Id == model.Id ? model : profile).ToArray();
                    ManagedModelsList.ItemsSource = _profiles.Select(CreateModelListItem).ToArray();
                    ManagedModelsList.SelectedItem = ManagedModelsList.Items.Cast<ModelListItem>().FirstOrDefault(item => item.Profile?.Id == model.Id);
                    RefreshEggUiState();
                    return model;
                }, _diagnostics)
            { Owner = this };
            _resourceWindow.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, AppLanguageManager.Text("ModelResourceManagement"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _resourceWindow = null;
            try
            {
                await ReloadProfilesAsync(root, _lifetime.Token);
                ManagedModelsList.SelectedItem = ManagedModelsList.Items.Cast<ModelListItem>().FirstOrDefault(item => item.Profile?.Id == model.Id);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            _modelDialogOpening = false;
            RefreshEggUiState();
        }
    }

    private async Task<bool> TryReuseResourceAsync(ModelProfile selection, string root, ModelResourceKind kind)
    {
        if (ModelResourceManagement.FindReusable(selection, root, kind) is null)
        {
            if (ModelResourceCatalog.Read(selection, root).Records.Any(record => record.Kind == kind
                    && ModelResourceManagement.SamePath(record.ResourcePath, new ModelResourceRecord(kind, selection).ResourcePath, root)))
            {
                await _diagnostics.WriteAsync("resource_validation_basis", "unavailable",
                    new Dictionary<string, object?> { ["modelId"] = selection.Id, ["resourceKind"] = kind.ToString(), ["reasonCode"] = "validation_basis_changed" });
                MessageBox.Show(ResourceDialogOwner, AppLanguageManager.Choose("已有记录不适用于当前依据，需要重新检测。", "The existing record does not match the current validation basis. Check again."),
                    AppLanguageManager.Text("ModelResourceManagement"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
                await _diagnostics.WriteAsync("resource_validation_basis", "new",
                    new Dictionary<string, object?> { ["modelId"] = selection.Id, ["resourceKind"] = kind.ToString() }, detailedOnly: true);
            return false;
        }
        await _diagnostics.WriteAsync("resource_validation_basis", "reused",
            new Dictionary<string, object?> { ["modelId"] = selection.Id, ["resourceKind"] = kind.ToString() }, detailedOnly: true);
        ShowResourceAlreadyAdded();
        return true;
    }

    private void ShowResourceAlreadyAdded()
    {
        StatusText.Text = AppLanguageManager.Choose("已添加且验证仍有效，可在参数设置中选择。", "Already registered and current. Select it in parameter settings.");
        MessageBox.Show(ResourceDialogOwner, StatusText.Text, AppLanguageManager.Text("ModelResourceManagement"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private Microsoft.Win32.OpenFileDialog CreateTemplateOpenDialog(string root)
    {
        var directory = ChatTemplateFile.GetDirectory(root);
        ChatTemplateFile.ValidatePath(Path.Combine(directory, ".template-dialog.jinja"), root);
        Directory.CreateDirectory(directory);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = AppLanguageManager.Text("AddTemplate"),
            Filter = "Jinja (*.jinja)|*.jinja",
            CheckFileExists = true,
            InitialDirectory = directory,
            RootDirectory = directory,
        };
        dialog.FileOk += (_, e) =>
        {
            try { ChatTemplateFile.ValidatePath(dialog.FileName, root); }
            catch (Exception ex)
            {
                e.Cancel = true;
                MessageBox.Show(ResourceDialogOwner, ex.Message, AppLanguageManager.Text("AddTemplate"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return dialog;
    }

    private async Task ManageTemplateAsync(ModelProfile model, bool add)
    {
        if (_busy || !CanRunModelResourceDetection() || _settings.LlamaRoot is not { } root) return;
        await using var operation = _diagnostics.Begin(add ? "template_add" : "template_generate", ModelDiagnosticFields(model));
        var profile = model with { Jinja = true, ChatTemplateRelativePath = null };
        if (add)
        {
            try
            {
                var dialog = CreateTemplateOpenDialog(root);
                if (dialog.ShowDialog(ResourceDialogOwner) != true) return;
                await operation.StageAsync("validate_template_path");
                profile = profile with { ChatTemplateRelativePath = Path.GetRelativePath(root, ChatTemplateFile.ValidatePath(dialog.FileName, root)) };
                operation.Set("resource", Path.GetFileName(profile.ChatTemplateRelativePath));
            }
            catch (Exception ex)
            {
                await operation.FailAsync(ex, "template_path_rejected");
                StatusText.Text = ex.Message;
                MessageBox.Show(ResourceDialogOwner, ex.Message, AppLanguageManager.Text("AddTemplate"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        SetBusy(true);
        try
        {
            if (!CanRunModelResourceDetection() || !CanEditProfile(model, forceClientRefresh: true))
                throw new InvalidOperationException(AppLanguageManager.Choose("本地服务状态已改变，请停止服务后再检测。", "Local service state changed. Stop services before checking."));
            await ModelResourceCatalog.EnsureMigratedAsync(profile, root, _lifetime.Token);
            await operation.StageAsync("check_existing_record");
            if (add ? ModelResourceManagement.FindReusable(profile, root, ModelResourceKind.Template) is not null
                : ModelResourceManagement.FindGeneratedTemplate(profile, root) is not null
                  || ModelResourceManagement.FindReusable(profile, root, ModelResourceKind.Template) is not null)
            {
                await operation.CompleteAsync("blocked", "already_registered");
                ShowResourceAlreadyAdded();
                return;
            }
            // Unique source assets never overwrite templates referenced by another configuration.
            await operation.StageAsync("prepare_and_render_template");
            var prepared = await PrepareChatTemplateAsync(profile, root, _lifetime.Token, explicitCheck: true,
                stagedFileName: $"{profile.Id}.{Guid.NewGuid():N}.codex-compatible.jinja");
            if (!prepared.RenderCheckCompleted || !ChatTemplateValidationCache.IsCurrent(prepared.Profile, root))
            { await operation.CompleteAsync("blocked", "template_check_not_current"); return; }
            if (!CanRunModelResourceDetection()) throw new InvalidOperationException("检测期间本地服务状态已改变 / Local service state changed during detection.");
            await operation.StageAsync("register_template");
            await ModelResourceCatalog.RegisterAsync(prepared.Profile, root, ModelResourceKind.Template, _lifetime.Token,
                defaultTemplate: !add && prepared.TemplateGenerated);
            operation.Set("resource", Path.GetFileName(prepared.Profile.ChatTemplateRelativePath));
            operation.Set("templateGenerated", prepared.TemplateGenerated);
            await operation.CompleteAsync();
            StatusText.Text = AppLanguageManager.Choose("模板已登记，可在模板设置中选择；已有配置和预选未改变。", "Template registered. Select it in Template Settings; existing configurations are unchanged.");
        }
        catch (Exception ex) { await operation.FailAsync(ex); StatusText.Text = ex.Message; MessageBox.Show(ResourceDialogOwner, ex.Message, AppLanguageManager.Text("TemplateSettings"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }
}
