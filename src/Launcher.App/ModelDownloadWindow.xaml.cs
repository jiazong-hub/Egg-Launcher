using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using Launcher.Models.Remote;
using Launcher.Models.Scanning;
using Launcher.Runtime.Router;

namespace Launcher.App;

public partial class ModelDownloadWindow : Window
{
    private readonly string _runtimeRoot;
    private readonly string _modelsRoot;
    private readonly HttpClient _catalogHttpClient;
    private readonly HuggingFaceModelCatalogClient _catalogClient;
    private readonly Func<string?> _getDownloadBlockReason;
    private CancellationTokenSource? _operationCancellation;
    private LlamaRouterProcessManager? _downloadRouter;
    private bool _busy;
    private readonly Queue<(DateTimeOffset Time, long Bytes)> _speedSamples = new();

    public ModelDownloadWindow(string runtimeRoot, Func<string?>? getDownloadBlockReason = null)
    {
        InitializeComponent();
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

    public GgufModelCandidate? DownloadedModel { get; private set; }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
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
        DownloadButton.IsEnabled = false;
        ModelDetailsText.Text = "正在读取公开模型目录…";
        StatusText.Text = "正在搜索 Hugging Face 的 GGUF 模型。";
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await _catalogClient.SearchAsync(SearchTextBox.Text, cancellation.Token);
            SearchResultsList.ItemsSource = results.Select(RemoteModelListItem.FromModel).ToArray();
            ModelDetailsText.Text = results.Count == 0
                ? "没有找到带 GGUF 标记的公开模型。请尝试输入更准确的名称。"
                : "请选择一个模型仓库以读取量化版本和准确大小。";
            StatusText.Text = $"搜索完成：找到 {results.Count} 个相关 GGUF 仓库。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "模型搜索超时，请检查网络后重试。";
            ModelDetailsText.Text = StatusText.Text;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"模型搜索失败：{exception.Message}";
            ModelDetailsText.Text = StatusText.Text;
        }
        finally
        {
            SetBusy(false, downloading: false);
        }
    }

    private async void SearchResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_busy || SearchResultsList.SelectedItem is not RemoteModelListItem selected)
        {
            return;
        }

        SetBusy(true, downloading: false);
        VariantsList.ItemsSource = null;
        DownloadButton.IsEnabled = false;
        ModelDetailsText.Text = FormatModelDetails(selected.Model) + "\n\n正在读取 GGUF 文件列表…";
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var details = await _catalogClient.GetDetailsAsync(selected.Model, cancellation.Token);
            VariantsList.ItemsSource = details.Variants.Select(RemoteVariantListItem.FromVariant).ToArray();
            ModelDetailsText.Text = FormatModelDetails(selected.Model);
            StatusText.Text = details.Variants.Count == 0
                ? "该仓库没有可由 llama 原生量化标识选择的主 GGUF 文件。"
                : $"已识别 {details.Variants.Count} 个可下载量化版本。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "读取模型文件列表超时。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取模型详情失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false, downloading: false);
        }
    }

    private void VariantsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DownloadButton.IsEnabled = CanDownloadSelection();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || VariantsList.SelectedItem is not RemoteVariantListItem selected)
        {
            return;
        }

        var blockReason = _getDownloadBlockReason();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            MessageBox.Show(
                this,
                blockReason,
                "暂时不能启动下载服务",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            StatusText.Text = blockReason;
            return;
        }

        if (SearchResultsList.SelectedItem is RemoteModelListItem { Model.IsGated: true })
        {
            MessageBox.Show(
                this,
                "该模型需要 Hugging Face 授权。本版本不读取或保存访问令牌，请选择公开模型。",
                "暂不支持 gated 模型",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (selected.Variant.TotalSizeBytes > 0 && !HasEnoughFreeSpace(selected.Variant.TotalSizeBytes, out var freeSpace))
        {
            MessageBox.Show(
                this,
                $"磁盘可用空间不足。模型约为 {FormatSize(selected.Variant.TotalSizeBytes)}，当前可用 {FormatSize(freeSpace)}。",
                "无法开始下载",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        _speedSamples.Clear();
        DownloadProgressBar.Value = 0;
        DownloadProgressText.Text = "正在启动 llama.cpp 下载服务…";
        StatusText.Text = $"准备由 llama.cpp 下载 {selected.Variant.DownloadId}。";
        SetBusy(true, downloading: true);
        var downloadSucceeded = false;
        LlamaRouterProcessInfo? downloadRouterInfo = null;

        try
        {
            var lateBlockReason = _getDownloadBlockReason();
            if (!string.IsNullOrWhiteSpace(lateBlockReason))
            {
                throw new InvalidOperationException(lateBlockReason);
            }

            Directory.CreateDirectory(_modelsRoot);
            var port = GetAvailableLoopbackPort();
            var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var healthHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            healthHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var healthClient = new RouterHealthClient(healthHttpClient);
            _downloadRouter = new LlamaRouterProcessManager(healthClient);
            var router = await _downloadRouter.StartAsync(
                new LlamaRouterStartRequest
                {
                    ExecutablePath = Path.Combine(_runtimeRoot, "llama-server.exe"),
                    WorkingDirectory = _runtimeRoot,
                    LogDirectory = Path.Combine(_runtimeRoot, "logs"),
                    ModelCacheDirectory = _modelsRoot,
                    Options = new LlamaRouterOptions
                    {
                        Host = IPAddress.Loopback.ToString(),
                        Port = port,
                        ModelsDirectory = _modelsRoot,
                        ModelsPresetPath = null,
                        MaximumLoadedModels = 1,
                        AutoloadModels = false,
                        ApiKey = apiKey,
                        DisableMultimodalProjectorAutoDownload = true,
                    },
                    StartupTimeout = TimeSpan.FromSeconds(20),
                },
                _operationCancellation.Token);
            downloadRouterInfo = router;
            DownloadProgressText.Text = "llama.cpp 下载服务已启动，正在连接原生状态流…";
            StatusText.Text = "正在由 llama.cpp 连接 Hugging Face；首次出现字节进度前可能需要解析仓库元数据。";

            using var downloadHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            downloadHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var downloader = new RouterModelDownloadClient(downloadHttpClient);
            var progress = new Progress<RouterModelDownloadProgress>(UpdateDownloadProgress);
            var activity = new Progress<RouterModelDownloadActivity>(UpdateDownloadActivity);
            var result = await downloader.DownloadAsync(
                router.BaseUri,
                selected.Variant.DownloadId,
                progress,
                _operationCancellation.Token,
                activity);

            var fullPath = Path.GetFullPath(result.ModelPath);
            if (!IsUnderDirectory(fullPath, _modelsRoot))
            {
                throw new InvalidOperationException("llama.cpp 返回的模型路径不在指定 models 目录内。");
            }

            DownloadedModel = new GgufModelCandidate(
                fullPath,
                Path.GetRelativePath(_modelsRoot, fullPath),
                $"{selected.Variant.RepositoryId.Split('/')[1]}-{selected.Variant.Quantization}",
                selected.Variant.TotalSizeBytes > 0
                    ? selected.Variant.TotalSizeBytes
                    : new FileInfo(fullPath).Length,
                selected.Variant.ShardCount,
                selected.Variant.DownloadId,
                selected.Variant.RepositoryId,
                selected.Variant.Quantization);
            DownloadProgressBar.Value = 100;
            DownloadProgressText.Text = result.WasAlreadyCached
                ? "模型已经存在于 llama.cpp 缓存中。"
                : $"下载完成 · {FormatSize(DownloadedModel.TotalSizeBytes)}";
            StatusText.Text = "下载完成，正在返回启动器并登记模型。";
            downloadSucceeded = true;
        }
        catch (OperationCanceledException) when (_operationCancellation?.IsCancellationRequested == true)
        {
            StatusText.Text = "下载已取消；临时文件如何保留或恢复由 llama.cpp 处理。";
            DownloadProgressText.Text = StatusText.Text;
        }
        catch (Exception exception)
        {
            var nativeFailure = RouterModelDownloadLogInspector.FindFailure(downloadRouterInfo);
            StatusText.Text = nativeFailure is null
                ? $"下载失败：{exception.Message}"
                : $"下载失败：{nativeFailure}";
            DownloadProgressText.Text = StatusText.Text;
        }
        finally
        {
            if (_downloadRouter is not null)
            {
                try
                {
                    await _downloadRouter.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    downloadSucceeded = false;
                    StatusText.Text = $"下载服务未能完整停止：{cleanupException.Message}";
                    DownloadProgressText.Text = StatusText.Text;
                }
                finally
                {
                    _downloadRouter = null;
                }
            }

            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false, downloading: false);
        }

        if (downloadSucceeded)
        {
            DialogResult = true;
        }
    }

    private void UpdateDownloadProgress(RouterModelDownloadProgress progress)
    {
        var now = DateTimeOffset.UtcNow;
        _speedSamples.Enqueue((now, progress.DownloadedBytes));
        while (_speedSamples.Count > 1 && now - _speedSamples.Peek().Time > TimeSpan.FromSeconds(3))
        {
            _speedSamples.Dequeue();
        }

        var oldest = _speedSamples.Peek();
        var elapsed = (now - oldest.Time).TotalSeconds;
        var bytesPerSecond = elapsed > 0.2
            ? Math.Max(0, progress.DownloadedBytes - oldest.Bytes) / elapsed
            : 0;
        var percent = progress.Fraction * 100;
        DownloadProgressBar.Value = percent;
        var currentFile = progress.Files.LastOrDefault()?.FileName ?? "GGUF";
        DownloadProgressText.Text =
            $"{currentFile}\n{percent:0.0}% · {FormatSize(progress.DownloadedBytes)} / {FormatSize(progress.TotalBytes)}" +
            (bytesPerSecond > 0 ? $" · {FormatSize((long)bytesPerSecond)}/s" : string.Empty);
    }

    private void UpdateDownloadActivity(RouterModelDownloadActivity activity)
    {
        StatusText.Text = activity.Message;
        if (activity.Phase != RouterModelDownloadPhase.Transferring
            || DownloadProgressBar.Value <= 0)
        {
            DownloadProgressText.Text = activity.Message;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        StatusText.Text = "正在请求 llama.cpp 取消下载…";
        _operationCancellation?.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy || _operationCancellation is null)
        {
            _catalogHttpClient.Dispose();
            return;
        }

        e.Cancel = true;
        MessageBox.Show(
            this,
            "模型正在下载。请先点击“取消下载”，等待 llama.cpp 停止后再关闭窗口。",
            "下载尚未结束",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void SetBusy(bool busy, bool downloading)
    {
        _busy = busy;
        SearchTextBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        SearchResultsList.IsEnabled = !busy;
        VariantsList.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy && CanDownloadSelection();
        CancelDownloadButton.IsEnabled = busy && downloading;
        CloseButton.IsEnabled = !(busy && downloading);
    }

    private bool CanDownloadSelection() =>
        !_busy
        && VariantsList.SelectedItem is RemoteVariantListItem
        && SearchResultsList.SelectedItem is RemoteModelListItem { Model.IsGated: false };

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
        var updated = model.LastModified?.ToLocalTime().ToString("yyyy-MM-dd") ?? "未知";
        return
            $"仓库：{model.RepositoryId}\n" +
            $"作者：{model.Author}\n" +
            $"用途：{model.PipelineTag ?? "未标注"}\n" +
            $"许可证：{model.License ?? "未标注"}\n" +
            $"下载量：{model.Downloads:N0}\n" +
            $"更新时间：{updated}\n" +
            $"访问：{(model.IsGated ? "需要授权（本版本不下载）" : "公开")}";
    }

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

    private sealed record RemoteModelListItem(string Summary, HuggingFaceModelSearchResult Model)
    {
        public static RemoteModelListItem FromModel(HuggingFaceModelSearchResult model) => new(
            $"{model.RepositoryId} · ↓ {model.Downloads:N0}" + (model.IsGated ? " · 需授权" : string.Empty),
            model);
    }

    private sealed record RemoteVariantListItem(string Summary, HuggingFaceGgufVariant Variant)
    {
        public static RemoteVariantListItem FromVariant(HuggingFaceGgufVariant variant) => new(
            $"{variant.Quantization} · {FormatSize(variant.TotalSizeBytes)}" +
            (variant.ShardCount > 1 ? $" · {variant.ShardCount} 分片" : string.Empty),
            variant);
    }
}
