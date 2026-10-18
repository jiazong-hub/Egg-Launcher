using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Launcher.Models.Profiles;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Launcher.App;

public partial class MainWindow
{
    private ContextMenu? _configurationMenu;
    private bool _configurationMenuOpening;
    private bool _modelDialogOpening;
    private bool _configurationSelectionInProgress;

    private bool CanUseModelCardActions(ModelProfile model, bool force = false) =>
        !_busy && !_launchInProgress && !_modelDialogOpening && !_configurationSelectionInProgress && CanEditProfile(model, force);

    private bool CanChangeCardConfiguration(ModelProfile model, string root) => Dispatcher.CheckAccess()
        ? !_busy && !_launchInProgress && !_modelDialogOpening && string.Equals(_settings.LlamaRoot, root, StringComparison.OrdinalIgnoreCase)
            && CanEditProfile(model, forceClientRefresh: true)
        : Dispatcher.Invoke(() => CanChangeCardConfiguration(model, root));

    private async Task<ModelProfile> ReadSelectedConfigurationForEditorAsync(ModelProfile registration, string root)
    {
        try { return await ModelConfigurationFile.ReadAsync(registration.PreferredConfigurationPath ?? "", registration, root, _lifetime.Token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            await _diagnostics.WriteAsync("configuration_unavailable", "unavailable", ModelDiagnosticFields(registration), ex);
            return registration with { ConfigurationError = ex.Message };
        }
    }

    private async void ModeCardManagement_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: ModelProfile model } && CanUseModelCardActions(model, force: true))
            await OpenModelResourcesAsync(model);
    }

    private async void ModeCardConfiguration_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_configurationMenuOpening || sender is not Button { Tag: ModelProfile model } button
            || _settings.LlamaRoot is not { } root || !CanUseModelCardActions(model, force: true)) return;
        if (_configurationMenu is { IsOpen: true }) { _configurationMenu.IsOpen = false; return; }
        _configurationMenuOpening = true;
        try
        {
            var current = await _profileStore.ReadRegistrationAsync(root, model, _lifetime.Token);
            var menu = CreateConfigurationMenu(current, root);
            if (!CanUseModelCardActions(current, force: true)) return;
            if (button.DataContext is ModeCardItem card)
                card.UpdateConfiguration(ModelConfigurationSelection.Inspect(current, root));
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = static (popupSize, targetSize, _) =>
            [
                new(new System.Windows.Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + 4), PopupPrimaryAxis.Horizontal),
                new(new System.Windows.Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 4), PopupPrimaryAxis.Horizontal),
            ];
            _configurationMenu = menu;
            menu.Closed += (_, _) => { if (ReferenceEquals(_configurationMenu, menu)) _configurationMenu = null; };
            menu.IsOpen = true;
            await _diagnostics.WriteAsync("configuration_directory_scan", "success",
                new Dictionary<string, object?> { ["modelId"] = current.Id, ["fileCount"] = menu.Items.OfType<MenuItem>().Count(item => item.IsEnabled) }, detailedOnly: true);
            if (!ModelConfigurationSelection.Inspect(current, root).Exists)
                await _diagnostics.WriteAsync("configuration_preselection_unavailable", "unavailable", ModelDiagnosticFields(current));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, AppLanguageManager.Choose("启动配置", "Startup configuration"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _configurationMenuOpening = false; }
    }

    private ContextMenu CreateConfigurationMenu(ModelProfile model, string root)
    {
        var files = ModelConfigurationSelection.ListFiles(model, root);
        var selection = ModelConfigurationSelection.Inspect(model, root);
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("ConfigurationMenuStyle"),
            ItemContainerStyle = (Style)FindResource("ConfigurationMenuItemStyle"),
        };
        foreach (var file in files)
        {
            var item = new MenuItem
            {
                Header = file.Name,
                ToolTip = file.Path,
                IsChecked = selection.Exists && string.Equals(file.Path, selection.Path, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += async (_, e) =>
            {
                e.Handled = true;
                menu.IsOpen = false;
                await SelectCardConfigurationAsync(model, root, file.Path);
            };
            menu.Items.Add(item);
        }
        if (files.Count == 0)
            menu.Items.Add(new MenuItem { Header = AppLanguageManager.Choose("暂无配置文件", "No configuration files"), IsEnabled = false });
        return menu;
    }

    private async Task SelectCardConfigurationAsync(ModelProfile model, string root, string path)
    {
        if (!CanUseModelCardActions(model, force: true)) return;
        await using var operation = _diagnostics.Begin("configuration_preselect", ModelDiagnosticFields(model, path));
        _configurationSelectionInProgress = true;
        RefreshEggUiState();
        try
        {
            await operation.StageAsync("update_model_preselection");
            var updated = await _profileStore.UpdatePreferredConfigurationAsync(root, model, path,
                () => CanChangeCardConfiguration(model, root), _lifetime.Token);
            // Refresh the preview read-only. General navigation refresh can migrate other models
            // or fill global pending-selection metadata, neither of which belongs to this action.
            var preview = await ReadSelectedConfigurationForEditorAsync(updated, root);
            var selectedModel = (ManagedModelsList.SelectedItem as ModelListItem)?.Profile?.Id;
            _profiles = _profiles.Select(profile => profile.Id == updated.Id ? preview : profile).ToArray();
            ManagedModelsList.ItemsSource = _profiles.Select(CreateModelListItem).ToArray();
            ManagedModelsList.SelectedItem = ManagedModelsList.Items.Cast<ModelListItem>().FirstOrDefault(item => item.Profile?.Id == selectedModel);
            await operation.CompleteAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await operation.FailAsync(ex);
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, AppLanguageManager.Choose("启动配置", "Startup configuration"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _configurationSelectionInProgress = false;
            RefreshEggUiState();
        }
    }

    private static bool IsModeCardActionSource(object source)
    {
        for (var current = source as DependencyObject; current is not null;)
        {
            if (current is ButtonBase or MenuItem) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
