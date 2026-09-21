using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using Launcher.Core.Configuration;
using Launcher.Models.Profiles;
using Launcher.Models.Remote;
using Launcher.Models.Scanning;
using Launcher.Runtime.Router;

namespace Launcher.App;

public partial class MainWindow
{
    private readonly Queue<DownloadRequest> _downloadQueue = new();
    private readonly List<string> _downloadHistory = [];
    private readonly Queue<(DateTimeOffset Time, long Bytes)> _downloadSpeedSamples = new();
    private CancellationTokenSource? _activeDownloadCancellation;
    private LlamaRouterProcessManager? _activeDownloadRouter;
    private Task? _downloadProcessorTask;
    private DownloadRequest? _activeDownload;
    private DownloadRequest? _suspendedDownload;
    private bool _downloadExitConfirmed;
    private bool _downloadsSuspendedForLocal;
    private bool _downloadPreemptRequested;
    private bool _localLaunchReservationActive;
    private string? _downloadSuspensionReason;
    private string _lastDownloadSummary = string.Empty;

    private bool HasActiveOrQueuedDownloads =>
        _activeDownload is not null || _suspendedDownload is not null || _downloadQueue.Count > 0;

    private void EnqueueDownload(HuggingFaceGgufVariant variant)
        => EnqueueDownload(DownloadRequest.ForMainModel(variant));

    private void EnqueueDownload(HuggingFaceMtpFile file)
        => EnqueueDownload(DownloadRequest.ForMtpFile(file));

    private void EnqueueDownload(HuggingFaceVisionFile file)
        => EnqueueDownload(DownloadRequest.ForVisionFile(file));

    private void EnqueueDownload(DownloadRequest request)
    {
        if (HasActiveOrQueuedDownloads)
        {
            StatusText.Text = AppLanguageManager.Choose(
                "当前已有一个文件正在下载或等待；请完成或取消后再创建下一个任务。",
                "One file is already downloading or waiting. Finish or cancel it before creating another task.");
            return;
        }

        _downloadQueue.Enqueue(request);
        StatusText.Text = AppLanguageManager.Choose($"已创建下载任务：{request.DownloadId}", $"Download task created: {request.DownloadId}");
        if (IsLocalRuntimeSessionActive(forceClientRefresh: true))
        {
            _downloadsSuspendedForLocal = true;
            _downloadSuspensionReason = AppLanguageManager.Choose("等待 Local 客户端关闭", "Waiting for the Local client to close");
            _backgroundLifecycleTimer.Start();
        }

        RefreshDownloadUi();
        if (!_downloadsSuspendedForLocal
            && (_downloadProcessorTask is null || _downloadProcessorTask.IsCompleted))
        {
            _downloadProcessorTask = ProcessDownloadQueueAsync();
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        while (!_downloadsSuspendedForLocal
               && _downloadQueue.TryDequeue(out var request)
               && !_lifetime.IsCancellationRequested)
        {
            _activeDownload = request;
            _activeDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _downloadSpeedSamples.Clear();
            _lastDownloadSummary = AppLanguageManager.Choose($"准备下载：{request.DownloadId}", $"Preparing download: {request.DownloadId}");
            SetDownloadProgress(0, AppLanguageManager.Choose($"准备下载 {request.DownloadId}", $"Preparing {request.DownloadId}"));
            try
            {
                var blockReason = GetModelDownloadBlockReason();
                if (!string.IsNullOrWhiteSpace(blockReason))
                {
                    _suspendedDownload = request;
                    _downloadsSuspendedForLocal = true;
                    _downloadSuspensionReason = blockReason;
                    _backgroundLifecycleTimer.Start();
                    _lastDownloadSummary = AppLanguageManager.Choose($"下载等待中：{blockReason}", $"Download waiting: {blockReason}");
                    break;
                }

                if (request.MainModel is { } variant)
                {
                    var candidate = await DownloadModelAsync(variant, _activeDownloadCancellation.Token);
                    var runtimeRoot = _settings.LlamaRoot
                        ?? throw new InvalidOperationException(AppLanguageManager.Choose("下载完成时 Runtime 已不可用。", "The Runtime became unavailable when the download completed."));
                    SetDownloadProgress(100, AppLanguageManager.Choose($"下载完成：{variant.DownloadId}", $"Download complete: {variant.DownloadId}"));
                    try
                    {
                        var existing = FindManagedProfile(candidate, runtimeRoot);
                        if (existing is null || IsProfileUnavailable(existing))
                        {
                            var added = await AddCandidateAsync(candidate, _activeDownloadCancellation.Token);
                            await ReloadProfilesAsync(runtimeRoot, _activeDownloadCancellation.Token);
                            _downloadHistory.Insert(0, AppLanguageManager.Choose($"已完成　{added.Profile.DisplayName}", $"Completed  {added.Profile.DisplayName}"));
                            _lastDownloadSummary = AppLanguageManager.Choose($"下载成功并已添加：{added.Profile.DisplayName}", $"Downloaded and added: {added.Profile.DisplayName}");
                            StatusText.Text = added.Status;
                        }
                        else
                        {
                            _downloadHistory.Insert(0, AppLanguageManager.Choose($"已存在　{existing.DisplayName}", $"Already exists  {existing.DisplayName}"));
                            _lastDownloadSummary = AppLanguageManager.Choose($"下载成功，模型已存在：{existing.DisplayName}", $"Download complete; model already exists: {existing.DisplayName}");
                            StatusText.Text = AppLanguageManager.Choose($"{existing.DisplayName} 已经是已管理模型。", $"{existing.DisplayName} is already a managed model.");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _downloadHistory.Insert(0, AppLanguageManager.Choose($"已下载　{variant.DownloadId}　自动添加失败：{exception.Message}", $"Downloaded  {variant.DownloadId}  Automatic add failed: {exception.Message}"));
                        _lastDownloadSummary = AppLanguageManager.Choose($"下载成功，但未能自动添加：{variant.DownloadId}", $"Download complete, but automatic add failed: {variant.DownloadId}");
                        StatusText.Text = AppLanguageManager.Choose($"{_lastDownloadSummary}。可在本地模型管理中扫描后重新添加。原因：{exception.Message}", $"{_lastDownloadSummary}. Scan in Local Model Management to add it again. Reason: {exception.Message}");
                    }
                }
                else if (request.MtpFile is { } mtpFile)
                {
                    var downloadedPath = await DownloadMtpFileAsync(mtpFile, _activeDownloadCancellation.Token);
                    SetDownloadProgress(100, AppLanguageManager.Choose($"MTP 文件下载完成：{mtpFile.Path}", $"MTP file download complete: {mtpFile.Path}"));
                    var relative = Path.GetRelativePath(_settings.LlamaRoot!, downloadedPath);
                    _downloadHistory.Insert(0, AppLanguageManager.Choose($"MTP 已下载　{relative}", $"MTP downloaded  {relative}"));
                    _lastDownloadSummary = AppLanguageManager.Choose(
                        $"MTP 文件已下载：{relative}；未添加到任何模型，请在本地模型管理中手动加载。",
                        $"MTP file downloaded: {relative}. It was not added to any model; load it manually in Local Model Management.");
                    StatusText.Text = _lastDownloadSummary;
                }
                else if (request.VisionFile is { } visionFile)
                {
                    var downloadedPath = await DownloadVisionFileAsync(visionFile, _activeDownloadCancellation.Token);
                    SetDownloadProgress(100, AppLanguageManager.Choose($"视觉模块下载完成：{visionFile.Path}", $"Vision module download complete: {visionFile.Path}"));
                    var relative = Path.GetRelativePath(_settings.LlamaRoot!, downloadedPath);
                    _downloadHistory.Insert(0, AppLanguageManager.Choose($"视觉模块已下载　{relative}", $"Vision module downloaded  {relative}"));
                    _lastDownloadSummary = AppLanguageManager.Choose(
                        $"视觉模块已下载：{relative}；未关联任何模型，请在本地模型管理中手动加载并识别。",
                        $"Vision module downloaded: {relative}. It is not associated with a model; load and recognize it in Local Model Management.");
                    StatusText.Text = _lastDownloadSummary;
                }
            }
            catch (OperationCanceledException) when (_activeDownloadCancellation.IsCancellationRequested)
            {
                if (_downloadPreemptRequested)
                {
                    _suspendedDownload = request;
                    _lastDownloadSummary = AppLanguageManager.Choose($"已暂停：{request.DownloadId} · 等待 Local 服务结束", $"Paused: {request.DownloadId} · Waiting for Local services to stop");
                    StatusText.Text = AppLanguageManager.Choose("下载服务已停止，正在为本地模型释放 Runtime。", "Download service stopped; releasing the Runtime for the local model.");
                }
                else
                {
                    _downloadHistory.Insert(0, AppLanguageManager.Choose($"已取消　{request.DownloadId}", $"Canceled  {request.DownloadId}"));
                    _lastDownloadSummary = AppLanguageManager.Choose($"下载已取消：{request.DownloadId}", $"Download canceled: {request.DownloadId}");
                    StatusText.Text = AppLanguageManager.Choose($"{_lastDownloadSummary}；未完成文件会保留用于续传。", $"{_lastDownloadSummary}; the incomplete file is retained for resume.");
                }
            }
            catch (Exception) when (_downloadPreemptRequested)
            {
                _suspendedDownload = request;
                _lastDownloadSummary = AppLanguageManager.Choose($"已暂停：{request.DownloadId} · 等待 Local 服务结束", $"Paused: {request.DownloadId} · Waiting for Local services to stop");
                StatusText.Text = AppLanguageManager.Choose("下载连接已结束，任务已保留；正在为本地模型释放 Runtime。", "The download connection ended and the task was retained; releasing the Runtime for the local model.");
            }
            catch (Exception exception)
            {
                _downloadHistory.Insert(0, AppLanguageManager.Choose($"失败　　{request.DownloadId}　{exception.Message}", $"Failed  {request.DownloadId}  {exception.Message}"));
                _lastDownloadSummary = AppLanguageManager.Choose($"下载失败：{request.DownloadId} · {exception.Message}", $"Download failed: {request.DownloadId} · {exception.Message}");
                StatusText.Text = _lastDownloadSummary;
            }
            finally
            {
                if (_activeDownloadRouter is not null)
                {
                    try
                    {
                        await _activeDownloadRouter.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        StatusText.Text = AppLanguageManager.Choose($"下载服务未能完整停止：{exception.Message}", $"The download service did not stop completely: {exception.Message}");
                    }

                    _activeDownloadRouter = null;
                }

                _activeDownloadCancellation.Dispose();
                _activeDownloadCancellation = null;
                _activeDownload = null;
                _downloadPreemptRequested = false;
                RefreshDownloadUi();
            }
        }

        RefreshDownloadUi();
    }

    private async Task<GgufModelCandidate> DownloadModelAsync(
        HuggingFaceGgufVariant variant,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        var modelsRoot = Path.Combine(runtimeRoot, "models");
        Directory.CreateDirectory(modelsRoot);
        var port = ReserveAvailableLoopbackPort();
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var healthHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        healthHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _activeDownloadRouter = new LlamaRouterProcessManager(new RouterHealthClient(healthHttpClient));
        var router = await _activeDownloadRouter.StartAsync(
            new LlamaRouterStartRequest
            {
                ExecutablePath = Path.Combine(runtimeRoot, "llama-server.exe"),
                WorkingDirectory = runtimeRoot,
                LogDirectory = Path.Combine(runtimeRoot, "logs"),
                ModelCacheDirectory = modelsRoot,
                Options = new LlamaRouterOptions
                {
                    Host = IPAddress.Loopback.ToString(),
                    Port = port,
                    ModelsDirectory = modelsRoot,
                    ModelsPresetPath = null,
                    MaximumLoadedModels = 1,
                    AutoloadModels = false,
                    ApiKey = apiKey,
                    DisableMultimodalProjectorAutoDownload = true,
                },
                StartupTimeout = TimeSpan.FromSeconds(20),
            },
            cancellationToken);

        using var downloadHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        downloadHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var downloader = new RouterModelDownloadClient(downloadHttpClient);
        var result = await downloader.DownloadAsync(
            router.BaseUri,
            variant.DownloadId,
            new Progress<RouterModelDownloadProgress>(UpdateMainDownloadProgress),
            cancellationToken,
            new Progress<RouterModelDownloadActivity>(activity =>
            {
                if (string.Equals(_activeDownload?.DownloadId, variant.DownloadId, StringComparison.Ordinal))
                {
                    SetDownloadProgress(MainDownloadProgressBar.Value, activity.Message);
                }
            }));
        try
        {
            return new HuggingFaceCacheResolver().ResolveVariant(modelsRoot, variant, cancellationToken);
        }
        catch (InvalidDataException) when (!string.IsNullOrWhiteSpace(result.ModelPath))
        {
            // Older llama.cpp builds can expose a direct GGUF path instead of the HF cache layout.
        }

        var fullPath = Path.GetFullPath(result.ModelPath!);
        var modelsPrefix = Path.GetFullPath(modelsRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(AppLanguageManager.Choose("llama.cpp 返回的模型路径不在 models 目录内。", "The model path returned by llama.cpp is outside the models directory."));
        }

        var scanned = await Task.Run(
            () => _modelScanner.Scan(modelsRoot, cancellationToken),
            cancellationToken);
        var directCandidate = scanned.Models.FirstOrDefault(candidate => string.Equals(
            Path.GetFullPath(candidate.PrimaryPath),
            fullPath,
            StringComparison.OrdinalIgnoreCase));
        if (directCandidate is null)
        {
            throw new InvalidDataException(AppLanguageManager.Choose("llama.cpp 返回的模型路径不是完整、可识别的 GGUF 主文件。", "The model path returned by llama.cpp is not a complete, recognizable primary GGUF file."));
        }

        var repositoryName = variant.RepositoryId.Split('/').LastOrDefault() ?? variant.RepositoryId;
        return directCandidate with
        {
            DisplayName = $"{repositoryName}-{variant.Quantization}",
            TotalSizeBytes = variant.TotalSizeBytes > 0
                ? variant.TotalSizeBytes
                : directCandidate.TotalSizeBytes,
            ShardCount = variant.ShardCount,
            RemoteModelId = variant.DownloadId,
            RemoteRepositoryId = variant.RepositoryId,
            RemoteQuantization = variant.Quantization,
        };
    }

    private ModelProfile? FindManagedProfile(GgufModelCandidate candidate, string runtimeRoot) =>
        ManagedModelCandidateMatcher.FindMatch(candidate, runtimeRoot, _profiles);

    private async Task<string> DownloadMtpFileAsync(
        HuggingFaceMtpFile file,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        var modelsRoot = Path.Combine(runtimeRoot, "models");
        Directory.CreateDirectory(modelsRoot);
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-Local-Launcher/1.0");
        var downloader = new HuggingFaceFileDownloadClient(httpClient);
        return await downloader.DownloadMtpFileAsync(
            file,
            modelsRoot,
            new Progress<HuggingFaceFileDownloadProgress>(progress =>
            {
                if (!string.Equals(_activeDownload?.DownloadId, file.DownloadId, StringComparison.Ordinal))
                {
                    return;
                }

                var details = progress.TotalBytes > 0
                    ? $"{progress.Fraction * 100:0.0}% · {FormatDownloadSize(progress.DownloadedBytes)} / {FormatDownloadSize(progress.TotalBytes)}"
                    : AppLanguageManager.Choose(
                        $"已接收 {FormatDownloadSize(progress.DownloadedBytes)}",
                        $"Received {FormatDownloadSize(progress.DownloadedBytes)}");
                SetDownloadProgress(progress.Fraction * 100, details);
            }),
            cancellationToken);
    }

    private async Task<string> DownloadVisionFileAsync(
        HuggingFaceVisionFile file,
        CancellationToken cancellationToken)
    {
        var runtimeRoot = _settings.LlamaRoot
            ?? throw new InvalidOperationException(AppLanguageManager.Choose("尚未配置 llama.cpp Runtime。", "The llama.cpp Runtime has not been configured."));
        var modelsRoot = Path.Combine(runtimeRoot, "models");
        Directory.CreateDirectory(modelsRoot);
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-Local-Launcher/1.0");
        var downloader = new HuggingFaceFileDownloadClient(httpClient);
        return await downloader.DownloadVisionFileAsync(
            file,
            modelsRoot,
            new Progress<HuggingFaceFileDownloadProgress>(progress =>
            {
                if (!string.Equals(_activeDownload?.DownloadId, file.DownloadId, StringComparison.Ordinal))
                {
                    return;
                }

                var details = progress.TotalBytes > 0
                    ? $"{progress.Fraction * 100:0.0}% · {FormatDownloadSize(progress.DownloadedBytes)} / {FormatDownloadSize(progress.TotalBytes)}"
                    : AppLanguageManager.Choose($"已接收 {FormatDownloadSize(progress.DownloadedBytes)}", $"Received {FormatDownloadSize(progress.DownloadedBytes)}");
                SetDownloadProgress(progress.Fraction * 100, details);
            }),
            cancellationToken);
    }

    private void UpdateMainDownloadProgress(RouterModelDownloadProgress progress)
    {
        if (!string.Equals(_activeDownload?.DownloadId, progress.ModelId, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _downloadSpeedSamples.Enqueue((now, progress.DownloadedBytes));
        while (_downloadSpeedSamples.Count > 1
               && now - _downloadSpeedSamples.Peek().Time > TimeSpan.FromSeconds(3))
        {
            _downloadSpeedSamples.Dequeue();
        }

        var oldest = _downloadSpeedSamples.Peek();
        var elapsed = (now - oldest.Time).TotalSeconds;
        var bytesPerSecond = elapsed > 0.2
            ? Math.Max(0, progress.DownloadedBytes - oldest.Bytes) / elapsed
            : 0;
        var details = $"{progress.Fraction * 100:0.0}% · {FormatDownloadSize(progress.DownloadedBytes)} / {FormatDownloadSize(progress.TotalBytes)}"
            + (bytesPerSecond > 0 ? $" · {FormatDownloadSize((long)bytesPerSecond)}/s" : string.Empty);
        SetDownloadProgress(progress.Fraction * 100, details);
    }

    private void SetDownloadProgress(double percent, string details)
    {
        MainDownloadProgressBar.Value = percent;
        DownloadManagerProgressBar.Value = percent;
        DownloadStatusSummaryText.Text = _activeDownload is null
            ? details
            : AppLanguageManager.Choose($"正在下载：{_activeDownload.RepositoryId} · {details}", $"Downloading: {_activeDownload.RepositoryId} · {details}");
        ActiveDownloadDetailsText.Text = DownloadStatusSummaryText.Text;
        RefreshDownloadUi(refreshLaunchState: false);
    }

    private void RefreshDownloadUi(bool refreshLaunchState = true)
    {
        var hasTasks = HasActiveOrQueuedDownloads;
        var hasActiveDownload = _activeDownloadCancellation is not null;
        CancelActiveDownloadButton.IsEnabled = hasActiveDownload;
        CancelActiveDownloadButton.Visibility = hasActiveDownload ? Visibility.Visible : Visibility.Collapsed;
        MainDownloadProgressBar.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
        DownloadManagerProgressBar.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
        var rows = new List<string>();
        if (_activeDownload is not null)
        {
            rows.Add(AppLanguageManager.Choose($"下载中　{_activeDownload.DownloadId}", $"Downloading  {_activeDownload.DownloadId}"));
        }

        if (_suspendedDownload is not null)
        {
            rows.Add(AppLanguageManager.Choose(
                $"已暂停　{_suspendedDownload.DownloadId}　{_downloadSuspensionReason ?? "等待 Runtime 空闲"}",
                $"Paused  {_suspendedDownload.DownloadId}  {_downloadSuspensionReason ?? "Waiting for Runtime"}"));
        }

        rows.AddRange(_downloadQueue.Select(item => _downloadsSuspendedForLocal
            ? AppLanguageManager.Choose($"等待中　{item.DownloadId}　{_downloadSuspensionReason}", $"Waiting  {item.DownloadId}  {_downloadSuspensionReason}")
            : AppLanguageManager.Choose($"排队中　{item.DownloadId}", $"Queued  {item.DownloadId}")));
        rows.AddRange(_downloadHistory.Take(20));
        DownloadTasksList.ItemsSource = rows;
        if (!hasTasks)
        {
            DownloadStatusSummaryText.Text = _lastDownloadSummary;
            ActiveDownloadDetailsText.Text = _lastDownloadSummary;
            MainDownloadProgressBar.Value = 0;
            DownloadManagerProgressBar.Value = 0;
        }
        else if (_downloadsSuspendedForLocal)
        {
            DownloadStatusSummaryText.Text = AppLanguageManager.Choose(
                $"下载已暂停：{_downloadSuspensionReason ?? "等待 Local 服务结束"}",
                $"Download paused: {_downloadSuspensionReason ?? "Waiting for Local services to stop"}");
            ActiveDownloadDetailsText.Text = DownloadStatusSummaryText.Text;
        }
        else if (_activeDownload is null && _downloadQueue.TryPeek(out var queued))
        {
            DownloadStatusSummaryText.Text = AppLanguageManager.Choose($"等待下载：{queued.DownloadId}", $"Waiting to download: {queued.DownloadId}");
            ActiveDownloadDetailsText.Text = DownloadStatusSummaryText.Text;
        }

        if (refreshLaunchState)
        {
            RefreshEggUiState();
        }
    }

    private void CancelActiveDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        CancelActiveDownloadButton.IsEnabled = false;
        StatusText.Text = AppLanguageManager.Choose("正在请求 llama.cpp 取消当前下载…", "Requesting llama.cpp to cancel the current download…");
        _activeDownloadCancellation?.Cancel();
    }

    private async Task CancelDownloadsForExitAsync()
    {
        _downloadQueue.Clear();
        _suspendedDownload = null;
        _downloadsSuspendedForLocal = false;
        _backgroundLifecycleTimer.Stop();
        _activeDownloadCancellation?.Cancel();
        if (_downloadProcessorTask is not null)
        {
            try
            {
                await _downloadProcessorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private bool IsLocalRuntimeSessionActive(bool forceClientRefresh = false) =>
        _settings.SelectedMode == ProviderMode.Local
        && (GetClientRunningSnapshot(forceClientRefresh)
            || IsAgentRunning()
            || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot));

    private async Task<bool> SuspendDownloadsForLocalLaunchAsync()
    {
        _downloadsSuspendedForLocal = true;
        _backgroundLifecycleTimer.Start();
        _downloadSuspensionReason = AppLanguageManager.Choose("等待 Local 服务结束", "Waiting for Local services to stop");
        if (_activeDownloadCancellation is not null)
        {
            _downloadPreemptRequested = true;
            _activeDownloadCancellation.Cancel();
        }

        if (_downloadProcessorTask is not null)
        {
            try
            {
                await _downloadProcessorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        RefreshDownloadUi();
        if (_activeDownloadRouter is not null || _activeDownload is not null)
        {
            StatusText.Text = AppLanguageManager.Choose("下载服务未能安全停止，本次未启动本地模型。", "The download service could not stop safely; the local model was not started.");
            return false;
        }

        var remainingConflict = GetModelDownloadBlockReason();
        if (!string.IsNullOrWhiteSpace(remainingConflict))
        {
            StatusText.Text = AppLanguageManager.Choose($"下载服务暂停后仍检测到 llama 冲突，本次未启动本地模型：{remainingConflict}", $"A llama conflict remains after pausing downloads; the local model was not started: {remainingConflict}");
            return false;
        }

        StatusText.Text = AppLanguageManager.Choose("下载任务已暂停，Runtime 已为本地模型释放。", "Download tasks paused; the Runtime has been released for the local model.");
        return true;
    }

    private Task TryResumeSuspendedDownloadsAsync()
    {
        if (!_downloadsSuspendedForLocal
            || _localLaunchReservationActive
            || _lifetime.IsCancellationRequested
            || GetClientRunningSnapshot()
            || IsAgentRunning()
            || IsLlamaServerFromRuntimeRunning(_settings.LlamaRoot))
        {
            return Task.CompletedTask;
        }

        if (_suspendedDownload is not null)
        {
            PrependDownload(_suspendedDownload);
            _suspendedDownload = null;
        }

        _downloadsSuspendedForLocal = false;
        _backgroundLifecycleTimer.Stop();
        _downloadSuspensionReason = null;
        _lastDownloadSummary = HasActiveOrQueuedDownloads
            ? AppLanguageManager.Choose("Local 服务已停止，正在恢复下载队列", "Local services stopped; resuming the download queue")
            : AppLanguageManager.Text("NoActiveDownload");
        RefreshDownloadUi();
        if (_downloadQueue.Count > 0
            && (_downloadProcessorTask is null || _downloadProcessorTask.IsCompleted))
        {
            _downloadProcessorTask = ProcessDownloadQueueAsync();
        }

        return Task.CompletedTask;
    }

    private void PrependDownload(DownloadRequest request)
    {
        var pending = _downloadQueue.ToArray();
        _downloadQueue.Clear();
        _downloadQueue.Enqueue(request);
        foreach (var item in pending)
        {
            _downloadQueue.Enqueue(item);
        }
    }

    private static string FormatDownloadSize(long bytes)
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

    private sealed record DownloadRequest(
        HuggingFaceGgufVariant? MainModel,
        HuggingFaceMtpFile? MtpFile,
        HuggingFaceVisionFile? VisionFile)
    {
        public string DownloadId => MainModel?.DownloadId ?? MtpFile?.DownloadId ?? VisionFile!.DownloadId;

        public string RepositoryId => MainModel?.RepositoryId ?? MtpFile?.RepositoryId ?? VisionFile!.RepositoryId;

        public static DownloadRequest ForMainModel(HuggingFaceGgufVariant variant) =>
            new(variant, null, null);

        public static DownloadRequest ForMtpFile(HuggingFaceMtpFile file) =>
            new(null, file, null);

        public static DownloadRequest ForVisionFile(HuggingFaceVisionFile file) =>
            new(null, null, file);
    }
}
