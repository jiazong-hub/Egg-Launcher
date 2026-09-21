using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Launcher.Models.Remote;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Launcher.App;

public partial class ModelDownloadWindow : Window
{
    private readonly string _runtimeRoot;
    private readonly string _modelsRoot;
    private readonly HttpClient _catalogHttpClient;
    private readonly HuggingFaceModelCatalogClient _catalogClient;
    private readonly Func<string?> _getDownloadBlockReason;
    private bool _busy;

    public ModelDownloadWindow(string runtimeRoot, Func<string?>? getDownloadBlockReason = null)
    {
        InitializeComponent();
        UiMotion.AttachWindowEntrance(this);
        SourceInitialized += (_, _) =>
            AdaptiveWindowSizing.FitDialog(this, 820, 720, 640, 480);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _modelsRoot = Path.Combine(_runtimeRoot, "models");
        _getDownloadBlockReason = getDownloadBlockReason ?? (() => null);
        _catalogHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _catalogHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-Local-Launcher/1.0");
        _catalogClient = new HuggingFaceModelCatalogClient(_catalogHttpClient);
    }

    public HuggingFaceGgufVariant? SelectedDownload { get; private set; }

    public HuggingFaceMtpFile? SelectedMtpDownload { get; private set; }

    public HuggingFaceVisionFile? SelectedVisionDownload { get; private set; }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(SearchTextBox.Text))
        {
            return;
        }

        SetBusy(true, downloading: false);
        SearchResultsList.ItemsSource = null;
        VariantsList.ItemsSource = null;
        MtpFilesList.ItemsSource = null;
        VisionFilesList.ItemsSource = null;
        UpdateSelectionActions();
        ModelDetailsText.Text = AppLanguageManager.Choose("正在读取公开模型目录…", "Reading the public model catalog…");
        StatusText.Text = AppLanguageManager.Choose("正在搜索 Hugging Face 的 GGUF 模型。", "Searching Hugging Face for GGUF models.");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await _catalogClient.SearchAsync(SearchTextBox.Text, cancellation.Token);
            SearchResultsList.ItemsSource = results.Select(RemoteModelListItem.FromModel).ToArray();
            ModelDetailsText.Text = results.Count == 0
                ? AppLanguageManager.Choose("没有找到带 GGUF 标记的公开模型。请尝试输入更准确的名称。", "No public models tagged GGUF were found. Try a more precise name.")
                : AppLanguageManager.Choose("请选择一个模型仓库以读取量化版本和准确大小。", "Select a model repository to read quantizations and exact sizes.");
            StatusText.Text = AppLanguageManager.Choose(
                $"搜索完成：找到 {results.Count} 个相关 GGUF 仓库。",
                $"Search complete: found {results.Count} related GGUF repositories.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = AppLanguageManager.Choose("模型搜索超时，请检查网络后重试。", "Model search timed out. Check the network and try again.");
            ModelDetailsText.Text = StatusText.Text;
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"模型搜索失败：{exception.Message}", $"Model search failed: {exception.Message}");
            ModelDetailsText.Text = StatusText.Text;
        }
        finally
        {
            SetBusy(false, downloading: false);
        }
    }

    private async void SearchResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_busy)
        {
            UpdateSelectionActions();
            return;
        }

        if (SearchResultsList.SelectedItem is not RemoteModelListItem selected)
        {
            UpdateSelectionActions();
            return;
        }

        VariantsList.SelectedItem = null;
        MtpFilesList.SelectedItem = null;
        VisionFilesList.SelectedItem = null;
        SetBusy(true, downloading: false);
        VariantsList.ItemsSource = null;
        MtpFilesList.ItemsSource = null;
        VisionFilesList.ItemsSource = null;
        UpdateSelectionActions();
        ModelDetailsText.Text = FormatModelDetails(selected.Model);
        StatusText.Text = AppLanguageManager.Choose("正在读取所选仓库的 GGUF 文件列表…", "Reading the selected repository's GGUF file list…");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var details = await _catalogClient.GetDetailsAsync(selected.Model, cancellation.Token);
            VariantsList.ItemsSource = details.Variants.Select(RemoteVariantListItem.FromVariant).ToArray();
            MtpFilesList.ItemsSource = details.ExternalMtpFiles.Select(RemoteMtpFileListItem.FromFile).ToArray();
            VisionFilesList.ItemsSource = details.ExternalVisionFiles.Select(RemoteVisionFileListItem.FromFile).ToArray();
            ModelDetailsText.Text = FormatModelDetails(selected.Model);
            StatusText.Text = details.Variants.Count == 0 && details.ExternalMtpFiles.Count == 0 && details.ExternalVisionFiles.Count == 0
                ? AppLanguageManager.Choose("该仓库没有可由 llama 原生量化标识选择的主 GGUF 文件。", "This repository has no primary GGUF file selectable by a native llama quantization identifier.")
                : AppLanguageManager.Choose(
                    $"已识别 {details.Variants.Count} 个主模型量化版本、{details.ExternalMtpFiles.Count} 个 MTP 候选、{details.ExternalVisionFiles.Count} 个视觉模块。辅助文件仍需与具体主模型联合验证。",
                    $"Identified {details.Variants.Count} main-model quantizations, {details.ExternalMtpFiles.Count} MTP candidates, and {details.ExternalVisionFiles.Count} vision modules. Auxiliary files still require validation with a specific main model.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = AppLanguageManager.Choose("读取模型文件列表超时。", "Reading the model file list timed out.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"读取模型详情失败：{exception.Message}", $"Failed to read model details: {exception.Message}");
        }
        finally
        {
            SetBusy(false, downloading: false);
        }
    }

    private void VariantsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VariantsList.SelectedItem is not null)
        {
            MtpFilesList.SelectedItem = null;
            VisionFilesList.SelectedItem = null;
        }

        UpdateSelectionActions();
    }

    private void MtpFilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MtpFilesList.SelectedItem is not null)
        {
            VariantsList.SelectedItem = null;
            VisionFilesList.SelectedItem = null;
        }

        UpdateSelectionActions();
    }

    private void VisionFilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VisionFilesList.SelectedItem is not null)
        {
            VariantsList.SelectedItem = null;
            MtpFilesList.SelectedItem = null;
        }

        UpdateSelectionActions();
    }

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not RemoteModelListItem selected)
        {
            return;
        }

        OpenInDefaultBrowser(
            HuggingFaceWebUriBuilder.BuildRepositoryUri(selected.Model.RepositoryId),
            AppLanguageManager.Choose("当前模型仓库", "current model repository"));
    }

    private void OpenVariantButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not RemoteModelListItem model
            || VariantsList.SelectedItem is not RemoteVariantListItem variant)
        {
            return;
        }

        OpenInDefaultBrowser(
            HuggingFaceWebUriBuilder.BuildVariantUri(model.Model, variant.Variant),
            AppLanguageManager.Choose("当前模型版本", "current model version"));
    }

    private void OpenInDefaultBrowser(Uri uri, string targetName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            StatusText.Text = AppLanguageManager.Choose($"已在默认浏览器中打开{targetName}。", $"Opened the {targetName} in the default browser.");
        }
        catch (Exception exception)
        {
            StatusText.Text = AppLanguageManager.Choose($"无法打开{targetName}：{exception.Message}", $"Unable to open the {targetName}: {exception.Message}");
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || FindAncestor<WpfButtonBase>(source) is not null
            || FindAncestor<WpfTextBoxBase>(source) is not null
            || FindAncestor<WpfScrollBar>(source) is not null
            || FindAncestor<ListBoxItem>(source) is not null)
        {
            return;
        }

        SearchResultsList.SelectedItem = null;
        VariantsList.SelectedItem = null;
        MtpFilesList.SelectedItem = null;
        VisionFilesList.SelectedItem = null;
        ModelDetailsText.Text = SearchResultsList.Items.Count == 0
            ? AppLanguageManager.Text("SelectRepositoryHint")
            : AppLanguageManager.Choose("请选择一个模型仓库以查看详细信息。", "Select a model repository to view details.");
        UpdateSelectionActions();
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var selectedVariant = VariantsList.SelectedItem as RemoteVariantListItem;
        var selectedMtp = MtpFilesList.SelectedItem as RemoteMtpFileListItem;
        var selectedVision = VisionFilesList.SelectedItem as RemoteVisionFileListItem;
        if (selectedVariant is null && selectedMtp is null && selectedVision is null)
        {
            return;
        }

        var blockReason = _getDownloadBlockReason();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            MessageBox.Show(
                this,
                blockReason,
                AppLanguageManager.Choose("暂时不能启动下载服务", "Download Service Unavailable"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            StatusText.Text = blockReason;
            return;
        }

        if (SearchResultsList.SelectedItem is RemoteModelListItem { Model.IsGated: true })
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose("该模型需要 Hugging Face 授权。本版本不读取或保存访问令牌，请选择公开模型。", "This model requires Hugging Face authorization. This release does not read or store access tokens; select a public model."),
                AppLanguageManager.Choose("暂不支持 gated 模型", "Gated Models Are Not Supported"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var requiredBytes = selectedVariant?.Variant.TotalSizeBytes ?? selectedMtp?.File.SizeBytes ?? selectedVision?.File.SizeBytes ?? 0;
        if (requiredBytes > 0 && !HasEnoughFreeSpace(requiredBytes, out var freeSpace))
        {
            MessageBox.Show(
                this,
                AppLanguageManager.Choose($"磁盘可用空间不足。文件约为 {FormatSize(requiredBytes)}，当前可用 {FormatSize(freeSpace)}。", $"Insufficient disk space. The file is approximately {FormatSize(requiredBytes)}; {FormatSize(freeSpace)} is available."),
                AppLanguageManager.Choose("无法开始下载", "Unable to Start Download"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedDownload = selectedVariant?.Variant;
        SelectedMtpDownload = selectedMtp?.File;
        SelectedVisionDownload = selectedVision?.File;
        var downloadId = SelectedDownload?.DownloadId ?? SelectedMtpDownload?.DownloadId ?? SelectedVisionDownload!.DownloadId;
        StatusText.Text = SelectedDownload is not null
            ? AppLanguageManager.Choose($"已创建主模型下载任务：{downloadId}", $"Main model download task created: {downloadId}")
            : SelectedMtpDownload is not null
                ? AppLanguageManager.Choose($"已创建单文件 MTP 下载任务：{downloadId}", $"Single-file MTP download task created: {downloadId}")
                : AppLanguageManager.Choose($"已创建单文件视觉模块下载任务：{downloadId}", $"Single-file vision module download task created: {downloadId}");
        DialogResult = true;
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        StatusText.Text = AppLanguageManager.Choose("下载任务请在主界面的下载管理卡片中取消。", "Cancel downloads from the Download Management card on the main page.");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _catalogHttpClient.Dispose();
    }

    private void SetBusy(bool busy, bool downloading)
    {
        _busy = busy;
        SearchTextBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        SearchResultsList.IsEnabled = !busy;
        VariantsList.IsEnabled = !busy;
        MtpFilesList.IsEnabled = !busy;
        VisionFilesList.IsEnabled = !busy;
        CancelDownloadButton.IsEnabled = busy && downloading;
        CloseButton.IsEnabled = !(busy && downloading);
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        OpenRepositoryButton.IsEnabled = SearchResultsList.SelectedItem is RemoteModelListItem;
        OpenVariantButton.IsEnabled = SearchResultsList.SelectedItem is RemoteModelListItem
            && VariantsList.SelectedItem is RemoteVariantListItem;
        DownloadButton.IsEnabled = CanDownloadSelection();
    }

    private bool CanDownloadSelection() =>
        !_busy
        && (VariantsList.SelectedItem is RemoteVariantListItem
            || MtpFilesList.SelectedItem is RemoteMtpFileListItem
            || VisionFilesList.SelectedItem is RemoteVisionFileListItem)
        && SearchResultsList.SelectedItem is RemoteModelListItem { Model.IsGated: false };

    private static T? FindAncestor<T>(DependencyObject? value)
        where T : DependencyObject
    {
        while (value is not null)
        {
            if (value is T match)
            {
                return match;
            }

            value = value switch
            {
                FrameworkContentElement content => content.Parent,
                _ => VisualTreeHelper.GetParent(value),
            };
        }

        return null;
    }

    private bool HasEnoughFreeSpace(long requiredBytes, out long freeSpace)
    {
        freeSpace = long.MaxValue;
        try
        {
            var root = Path.GetPathRoot(_modelsRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            freeSpace = new DriveInfo(root).AvailableFreeSpace;
            return freeSpace >= requiredBytes;
        }
        catch (IOException)
        {
            // llama.cpp remains the authority if Windows cannot report capacity here.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string FormatModelDetails(HuggingFaceModelSearchResult model)
    {
        var updated = model.LastModified?.ToLocalTime().ToString("yyyy-MM-dd")
            ?? AppLanguageManager.Choose("未知", "Unknown");
        return AppLanguageManager.IsEnglish
            ? $"Repository: {model.RepositoryId}\nAuthor: {model.Author}\nPurpose: {model.PipelineTag ?? "Not specified"}\nLicense: {model.License ?? "Not specified"}\nDownloads: {model.Downloads:N0}\nUpdated: {updated}\nAccess: {(model.IsGated ? "Authorization required (not downloaded by this release)" : "Public")}"
            : $"仓库：{model.RepositoryId}\n作者：{model.Author}\n用途：{model.PipelineTag ?? "未标注"}\n许可证：{model.License ?? "未标注"}\n下载量：{model.Downloads:N0}\n更新时间：{updated}\n访问：{(model.IsGated ? "需要授权（本版本不下载）" : "公开")}";
    }

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

    private sealed record RemoteModelListItem(string Summary, HuggingFaceModelSearchResult Model)
    {
        public static RemoteModelListItem FromModel(HuggingFaceModelSearchResult model) => new(
            $"{model.RepositoryId} · ↓ {model.Downloads:N0}" + (model.IsGated ? AppLanguageManager.Choose(" · 需授权", " · Gated") : string.Empty),
            model);
    }

    private sealed record RemoteVariantListItem(string Summary, HuggingFaceGgufVariant Variant)
    {
        public static RemoteVariantListItem FromVariant(HuggingFaceGgufVariant variant) => new(
            $"{variant.Quantization} · {FormatSize(variant.TotalSizeBytes)}" +
            (variant.ShardCount > 1 ? AppLanguageManager.Choose($" · {variant.ShardCount} 分片", $" · {variant.ShardCount} shards") : string.Empty),
            variant);
    }

    private sealed record RemoteMtpFileListItem(string Summary, HuggingFaceMtpFile File)
    {
        public static RemoteMtpFileListItem FromFile(HuggingFaceMtpFile file) => new(
            $"{file.Path} · {FormatSize(file.SizeBytes)}",
            file);
    }

    private sealed record RemoteVisionFileListItem(string Summary, HuggingFaceVisionFile File)
    {
        public static RemoteVisionFileListItem FromFile(HuggingFaceVisionFile file) => new(
            $"{file.Path} · {FormatSize(file.SizeBytes)}",
            file);
    }
}
