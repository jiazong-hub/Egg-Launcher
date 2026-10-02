using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Launcher.Models.Profiles;
using CheckBox = System.Windows.Controls.CheckBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using ScrollViewer = System.Windows.Controls.ScrollViewer;

namespace Launcher.App;

public partial class ProfileEditorWindow
{
    private Popup? _commonPathsPopup;
    private IReadOnlyList<CommonSandboxDirectory>? _commonDirectories;
    private bool _detectingCommonDirectories;
    private CancellationTokenSource? _commonDirectoryCancellation;

    private async void CommonSandboxPaths_Click(object sender, RoutedEventArgs e)
    {
        if (_commonPathsPopup?.IsOpen == true) { _commonPathsPopup.IsOpen = false; return; }
        var panel = new StackPanel();
        var frame = new Border { Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        frame.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        _commonPathsPopup ??= new Popup
        {
            PlacementTarget = CommonSandboxPathsButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };
        _commonPathsPopup.Child = frame;
        Closed -= CloseCommonDirectoryMenu;
        Closed += CloseCommonDirectoryMenu;
        frame.Width = Math.Max(300, Math.Min(580, ActualWidth - 90));
        _commonPathsPopup.IsOpen = true;
        if (_commonDirectories is null)
        {
            panel.Children.Add(new TextBlock { Text = AppLanguageManager.Choose("正在识别常用目录…", "Detecting common directories…") });
            if (_detectingCommonDirectories) return;
            _detectingCommonDirectories = true;
            using var cancellation = new CancellationTokenSource();
            _commonDirectoryCancellation = cancellation;
            try { _commonDirectories = await Task.Run(() => CommonSandboxDirectories.DetectAsync(cancellation.Token), cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                if (IsVisible && _commonPathsPopup.Child is Border currentFrame && currentFrame.Child is StackPanel errorPanel)
                {
                    errorPanel.Children.Clear();
                    errorPanel.Children.Add(new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = AppLanguageManager.Choose("常用目录识别失败，可重试或使用自定义路径。", "Directory detection failed. Retry or use a custom path."),
                        ToolTip = exception.GetType().Name
                    });
                    var retry = new Button { Content = AppLanguageManager.Choose("重试", "Retry"), Margin = new Thickness(0, 8, 0, 0) };
                    retry.Click += (_, _) => { _commonPathsPopup.IsOpen = false; CommonSandboxPaths_Click(retry, new RoutedEventArgs()); };
                    errorPanel.Children.Add(retry);
                }
                return;
            }
            finally
            {
                _detectingCommonDirectories = false;
                if (ReferenceEquals(_commonDirectoryCancellation, cancellation)) _commonDirectoryCancellation = null;
            }
            if (!IsVisible) return;
            // The menu may have been closed/reopened while detection was in progress.
            if (_commonPathsPopup.Child is Border current && current.Child is StackPanel currentPanel) panel = currentPanel;
        }
        PopulateCommonDirectoryMenu(panel);
    }

    private void PopulateCommonDirectoryMenu(StackPanel panel)
    {
        panel.Children.Clear();
        var list = new StackPanel();
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = Math.Min(320, Math.Max(140, ActualHeight - 250)),
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        });
        var selections = new List<(CommonSandboxDirectory Directory, CheckBox Check, ComboBox Permission, CheckBox? Gradle)>();
        var add = new Button { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, IsEnabled = false };
        void UpdateCount()
        {
            var count = selections.Count(x => x.Check.IsChecked == true);
            add.Content = AppLanguageManager.Choose($"添加所选（{count}）", $"Add selected ({count})");
            add.IsEnabled = count > 0;
        }
        foreach (var directory in _commonDirectories ?? [])
        {
            var isGradle = directory.Name.StartsWith("Gradle", StringComparison.Ordinal);
            var existingRow = directory.Path is { } detectedPath
                ? _sandboxPathRows.FirstOrDefault(x => SameSandboxPath(x.PathTextBox.Text, detectedPath)) : null;
            var exists = existingRow is not null;
            var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var check = new CheckBox
            {
                Content = directory.Name + (exists ? AppLanguageManager.Choose("（已添加）", " (added)") : ""),
                IsEnabled = directory.Path is not null && (!exists || isGradle),
                FontSize = 12
            };
            texts.Children.Add(check);
            var description = directory.Path ?? AppLanguageManager.Choose("未识别，请使用自定义路径添加", "Not identified; use a custom path");
            if (directory.Path is not null && !Directory.Exists(directory.Path)) description += AppLanguageManager.Choose("（尚未创建）", " (not created)");
            var path = new TextBlock
            {
                Text = description,
                ToolTip = description,
                FontSize = 11,
                Margin = new Thickness(20, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            path.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            texts.Children.Add(path);
            row.Children.Add(texts);
            var permission = new ComboBox
            {
                ItemsSource = new[] { AppLanguageManager.Choose("只读", "Read only"), AppLanguageManager.Choose("读写", "Read/write") },
                SelectedIndex = (existingRow?.AllowWriteCheckBox.IsChecked ?? directory.AllowWrite) ? 1 : 0,
                Width = 84,
                Height = 30,
                MinHeight = 30,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top,
                IsEnabled = check.IsEnabled && !exists
            };
            System.Windows.Controls.Grid.SetColumn(permission, 1);
            row.Children.Add(permission);
            list.Children.Add(row);
            CheckBox? gradle = null;
            if (isGradle)
            {
                gradle = new CheckBox
                {
                    Content = AppLanguageManager.Choose("同时设为 Gradle 用户目录", "Also use as Gradle user home"),
                    IsChecked = existingRow?.GradleCheckBox.IsChecked == true,
                    IsEnabled = false,
                    Margin = new Thickness(20, 8, 0, 0),
                    FontSize = 12
                };
                texts.Children.Add(gradle);
                texts.Children.Add(new TextBlock
                {
                    Text = AppLanguageManager.Choose("用于缓存、用户配置及初始化脚本，避免沙箱默认目录异常。", "Sets caches, user configuration and init scripts; avoids sandbox home lookup issues."),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20, 4, 0, 0)
                });
                var gradleOption = gradle;
                void UpdateGradleOption()
                {
                    gradleOption.IsEnabled = check.IsChecked == true && permission.SelectedIndex == 1;
                    // Deselecting the menu row cancels selection, not its existing setting.
                    if (permission.SelectedIndex != 1) gradleOption.IsChecked = false;
                }
                check.Checked += (_, _) => UpdateGradleOption();
                check.Unchecked += (_, _) => UpdateGradleOption();
                permission.SelectionChanged += (_, _) => UpdateGradleOption();
            }
            selections.Add((directory, check, permission, gradle));
            check.Checked += (_, _) => UpdateCount();
            check.Unchecked += (_, _) => UpdateCount();
        }
        UpdateCount();
        add.Click += (_, _) =>
        {
            foreach (var entry in selections.Where(x => x.Check.IsChecked == true))
                if (entry.Directory.Path is { } path)
                {
                    if (HasSandboxPath(path))
                    {
                        if (entry.Gradle is null) continue;
                        var existing = _sandboxPathRows.First(x => SameSandboxPath(x.PathTextBox.Text, path));
                        // Preserve existing permission, replace only its explicit Gradle-purpose controls.
                        if (!existing.AllowWriteCheckBox.IsChecked.GetValueOrDefault() && entry.Gradle.IsChecked == true)
                        {
                            System.Windows.MessageBox.Show(this, AppLanguageManager.Choose("该目录已按只读添加，请先在路径列表中启用写入，再设置 Gradle 用户目录。", "This directory is read-only. Enable write access in the path list first."));
                            continue;
                        }
                        if (!existing.IsGradleDirectory)
                        {
                            AddGradleDirectoryControls(existing);
                            var index = _sandboxPathRows.IndexOf(existing);
                            existing = existing with { IsGradleDirectory = true };
                            _sandboxPathRows[index] = existing;
                            MarkSandboxSettingsEdited();
                        }
                        // Keep the same controls and row position; their existing handlers
                        // clear replacement consent only if the user explicitly disables Gradle.
                        existing.GradleCheckBox.IsChecked = entry.Gradle.IsChecked == true;
                    }
                    else AddSandboxPathRow(new SandboxPathPermission
                    {
                        Path = path,
                        AllowWrite = entry.Permission.SelectedIndex == 1,
                        IsGradleDirectory = entry.Gradle is not null,
                        UseAsGradleUserHome = entry.Gradle?.IsChecked == true
                    }, markEdited: true);
                }
            _commonPathsPopup!.IsOpen = false;
        };
        var footer = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        footer.Children.Add(add);
        var refresh = new Button { Content = AppLanguageManager.Choose("刷新", "Refresh"), Margin = new Thickness(8, 10, 0, 0) };
        refresh.Click += (_, _) => { _commonDirectories = null; _commonPathsPopup!.IsOpen = false; CommonSandboxPaths_Click(sender: refresh, new RoutedEventArgs()); };
        footer.Children.Add(refresh);
        panel.Children.Add(footer);
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = AppLanguageManager.Choose("添加后请保存设置，并重新启动客户端，确认聊天使用 egg_launcher_active。", "Save, restart the client, and select egg_launcher_active in the chat.")
        });
    }

    private bool HasSandboxPath(string path) => _sandboxPathRows.Any(row => SameSandboxPath(row.PathTextBox.Text, path));

    private static bool SameSandboxPath(string left, string path)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Trim())),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }

    private void CloseCommonDirectoryMenu(object? sender, EventArgs e)
    {
        _commonDirectoryCancellation?.Cancel();
        if (_commonPathsPopup is not null) _commonPathsPopup.IsOpen = false;
    }
}
