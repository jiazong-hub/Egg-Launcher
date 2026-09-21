using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.Security;
using Launcher.Runtime.Transport;
using Launcher.Runtime.Router;
using Launcher.Orchestration.ModeSwitch;

namespace Launcher.Orchestration.Agent;

public sealed class LocalRouterSupervisor : IAsyncDisposable
{
    private const int MaximumUpstreamPortAttempts = 5;
    private static readonly TimeSpan RuntimeStateHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions RuntimeStateSerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISettingsStore _settingsStore;
    private readonly ILlamaRouterProcessManager _processManager;
    private readonly ILoopbackSafetyProxy _safetyProxy;
    private readonly IRouterControlClient _routerControlClient;
    private readonly LauncherDataPaths _dataPaths;
    private readonly TimeProvider _timeProvider;
    private readonly string _routerApiKey;
    private readonly string _protectedRouterApiKey;
    private readonly DateTimeOffset _agentStartedAtUtc;
    private readonly ILocalEndpointMigrationCoordinator? _endpointMigrationCoordinator;
    private readonly Func<string, Task>? _runtimeStateDiagnosticWriter;
    private LlamaRouterProcessInfo? _routerInfo;
    private RouterLaunchFingerprint? _activeFingerprint;
    private RuntimeState? _lastPublishedRuntimeState;
    private DateTimeOffset? _lastRuntimeStateWriteAtUtc;
    private ProviderMode _lastKnownSelectedMode = ProviderMode.OpenAI;
    private bool _hasPublishedRuntimeState;
    private bool _disposed;

    public LocalRouterSupervisor(
        ISettingsStore settingsStore,
        ILlamaRouterProcessManager processManager,
        ILoopbackSafetyProxy safetyProxy,
        IRouterControlClient routerControlClient,
        LauncherDataPaths dataPaths,
        TimeProvider? timeProvider = null,
        string? routerApiKey = null,
        ILocalEndpointMigrationCoordinator? endpointMigrationCoordinator = null,
        Func<string, Task>? runtimeStateDiagnosticWriter = null)
    {
        _settingsStore = settingsStore;
        _processManager = processManager;
        _safetyProxy = safetyProxy;
        _routerControlClient = routerControlClient;
        _dataPaths = dataPaths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _endpointMigrationCoordinator = endpointMigrationCoordinator;
        _runtimeStateDiagnosticWriter = runtimeStateDiagnosticWriter;
        _routerApiKey = string.IsNullOrWhiteSpace(routerApiKey)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            : routerApiKey;
        _protectedRouterApiKey = CurrentUserSecretProtector.ProtectString(_routerApiKey);
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        _agentStartedAtUtc = currentProcess.StartTime.ToUniversalTime();
    }

    public async Task<LocalRouterReconcileResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _lastKnownSelectedMode = settings.SelectedMode;
            if (settings.SelectedMode == ProviderMode.OpenAI)
            {
                var stopped = _processManager.OwnedProcessId is not null || _safetyProxy.IsRunning;
                await StopRouterComponentsAsync(cancellationToken).ConfigureAwait(false);

                _routerInfo = null;
                _activeFingerprint = null;
                if (stopped || !_hasPublishedRuntimeState || !File.Exists(_dataPaths.RuntimeStateFile))
                {
                    await TryWriteRuntimeStateAsync(new RuntimeState
                    {
                        SelectedMode = ProviderMode.OpenAI,
                        Phase = RuntimePhase.Stopped,
                        AgentProcessId = Environment.ProcessId,
                    }).ConfigureAwait(false);
                }
                return new LocalRouterReconcileResult(
                    ProviderMode.OpenAI,
                    stopped ? LocalRouterReconcileAction.Stopped : LocalRouterReconcileAction.None,
                    null,
                    stopped ? "已停止 Agent 所有的 Local Router 与安全代理。" : null);
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedModelId))
            {
                var stopped = _processManager.OwnedProcessId is not null || _safetyProxy.IsRunning;
                await StopRouterComponentsAsync(cancellationToken).ConfigureAwait(false);
                _routerInfo = null;
                _activeFingerprint = null;
                await TryWriteRuntimeStateAsync(new RuntimeState
                {
                    SelectedMode = ProviderMode.Local,
                    Phase = RuntimePhase.Stopped,
                    AgentProcessId = Environment.ProcessId,
                }).ConfigureAwait(false);
                return new LocalRouterReconcileResult(
                    ProviderMode.Local,
                    stopped ? LocalRouterReconcileAction.Stopped : LocalRouterReconcileAction.None,
                    null,
                    "当前 Local 模型已被移除；等待用户重新预选模型并启动。");
            }

            if (_processManager.OwnedProcessId is null)
            {
                var recoveringFromRouterExit = _routerInfo is not null
                    || _activeFingerprint is not null
                    || _safetyProxy.IsRunning;
                await StopRouterComponentsAsync(cancellationToken).ConfigureAwait(false);

                _routerInfo = null;
                _activeFingerprint = null;
                return await StartRouterAsync(
                    settings,
                    recoveringFromRouterExit
                        ? LocalRouterReconcileAction.Restarted
                        : LocalRouterReconcileAction.Started,
                    recoveringFromRouterExit
                        ? "检测到 Local Router 意外退出，已清理旧安全代理并完成重启。"
                        : "Local Router 已就绪；等待 Egg Launcher 启动本次 ChatGPT Local 会话。",
                    cancellationToken).ConfigureAwait(false);
            }

            var requestedFingerprint = CreateFingerprint(settings);
            if (_activeFingerprint is not null && _activeFingerprint != requestedFingerprint)
            {
                await StopRouterComponentsAsync(cancellationToken).ConfigureAwait(false);
                _routerInfo = null;
                _activeFingerprint = null;
                return await StartRouterAsync(
                    settings,
                    LocalRouterReconcileAction.Restarted,
                    $"Local Router 已切换到模型 {settings.SelectedModelId}。",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!_safetyProxy.IsRunning && _routerInfo is not null)
            {
                try
                {
                    settings = await StartAndValidateSafetyProxyAsync(settings, cancellationToken)
                        .ConfigureAwait(false);
                    _activeFingerprint = CreateFingerprint(settings);
                    await TryWriteRuntimeStateAsync(CreateRunningRuntimeState(
                        settings,
                        Path.Combine(Path.GetFullPath(settings.LlamaRoot!), "llama-server.exe")))
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var publishedError = await StopAfterStartupFailureAsync(settings, exception)
                        .ConfigureAwait(false);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publishedError).Throw();
                }
            }

            await PublishHeartbeatIfDueAsync().ConfigureAwait(false);

            return new LocalRouterReconcileResult(
                ProviderMode.Local,
                LocalRouterReconcileAction.None,
                _processManager.OwnedProcessId,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            Exception? disposeError = null;
            try
            {
                await _safetyProxy.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposeError = exception;
            }

            try
            {
                await _processManager.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposeError = disposeError is null
                    ? exception
                    : new AggregateException("Local 后台组件未能完整停止。", disposeError, exception);
            }

            await TryWriteRuntimeStateAsync(new RuntimeState
            {
                SelectedMode = _lastKnownSelectedMode,
                Phase = RuntimePhase.Stopped,
                AgentProcessId = Environment.ProcessId,
            }).ConfigureAwait(false);
            _routerInfo = null;
            _activeFingerprint = null;
            _disposed = true;
            if (disposeError is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeError).Throw();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private LlamaRouterStartRequest CreateStartRequest(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LlamaRoot))
        {
            throw new InvalidDataException("Local 模式缺少 llama.cpp Runtime Root。");
        }

        var runtimeRoot = Path.GetFullPath(settings.LlamaRoot);
        var executablePath = Path.Combine(runtimeRoot, "llama-server.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("找不到 Local 模式记录的 llama-server.exe。", executablePath);
        }

        if (!File.Exists(_dataPaths.RouterPresetFile))
        {
            throw new FileNotFoundException("找不到 Local 模式 Router preset。", _dataPaths.RouterPresetFile);
        }

        return new LlamaRouterStartRequest
        {
            ExecutablePath = executablePath,
            WorkingDirectory = runtimeRoot,
            LogDirectory = _dataPaths.LogsDirectory,
            Options = new LlamaRouterOptions
            {
                Host = "127.0.0.1",
                Port = ReserveAvailableLoopbackPort(),
                ModelsPresetPath = _dataPaths.RouterPresetFile,
                MaximumLoadedModels = 1,
                AutoloadModels = true,
                EnableMetrics = settings.LlamaMetricsSupported,
                ApiKey = _routerApiKey,
            },
        };
    }

    private async Task<LocalRouterReconcileResult> StartRouterAsync(
        LauncherSettings settings,
        LocalRouterReconcileAction action,
        string message,
        CancellationToken cancellationToken)
    {
        var request = CreateStartRequest(settings);
        await TryWriteRuntimeStateAsync(new RuntimeState
        {
            SelectedMode = ProviderMode.Local,
            Phase = RuntimePhase.Starting,
            RouterExecutablePath = request.ExecutablePath,
            SelectedModelId = settings.SelectedModelId,
            AgentProcessId = Environment.ProcessId,
        }).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _routerInfo = await _processManager.StartAsync(request, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (LlamaRouterPortBindingException) when (attempt < MaximumUpstreamPortAttempts)
                {
                    request = CreateStartRequest(settings);
                }
            }
        }
        catch (Exception exception)
        {
            await TryWriteRuntimeStateAsync(new RuntimeState
            {
                SelectedMode = ProviderMode.Local,
                Phase = RuntimePhase.Error,
                RouterExecutablePath = request.ExecutablePath,
                SelectedModelId = settings.SelectedModelId,
                AgentProcessId = Environment.ProcessId,
                LastError = SafeRuntimeError(exception),
            }).ConfigureAwait(false);
            throw;
        }

        try
        {
            settings = await StartAndValidateSafetyProxyAsync(settings, cancellationToken).ConfigureAwait(false);
            _activeFingerprint = CreateFingerprint(settings);
            await TryWriteRuntimeStateAsync(CreateRunningRuntimeState(settings, request.ExecutablePath))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var publishedError = await StopAfterStartupFailureAsync(settings, exception, request.ExecutablePath)
                .ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(publishedError).Throw();
        }

        return new LocalRouterReconcileResult(
            ProviderMode.Local,
            action,
            _routerInfo.ProcessId,
            message);
    }

    private async Task<LauncherSettings> StartAndValidateSafetyProxyAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        if (_routerInfo is null)
        {
            throw new InvalidOperationException("Local Router 尚未启动，无法验证 Desktop 连接端点。");
        }

        var publicBaseUri = PublicBaseUri(settings.RouterPort);
        try
        {
            await _safetyProxy.StartAsync(
                publicBaseUri,
                _routerInfo.BaseUri,
                cancellationToken,
                _routerApiKey).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAddressAlreadyInUse(exception))
        {
            if (_endpointMigrationCoordinator is null)
            {
                throw new IOException(
                    $"Desktop 回环端口 {settings.RouterPort} 已被占用，且当前 Agent 无法迁移配置。",
                    exception);
            }

            await _safetyProxy.StartAsync(
                PublicBaseUri(0),
                _routerInfo.BaseUri,
                cancellationToken,
                _routerApiKey).ConfigureAwait(false);
            var migratedBaseUri = _safetyProxy.PublicBaseUri
                ?? throw new InvalidOperationException("安全代理未报告迁移后的回环端点。");
            try
            {
                settings = await _endpointMigrationCoordinator.MigrateAsync(
                    settings,
                    migratedBaseUri,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _safetyProxy.StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            publicBaseUri = migratedBaseUri;
        }

        publicBaseUri = _safetyProxy.PublicBaseUri ?? publicBaseUri;
        var routeProbe = await _routerControlClient.ProbeResponsesRouteAsync(publicBaseUri, cancellationToken)
            .ConfigureAwait(false);
        if (!routeProbe.IsAvailable)
        {
            var status = routeProbe.StatusCode is { } statusCode ? $"HTTP {statusCode}" : "无 HTTP 响应";
            throw new InvalidOperationException(
                $"当前 llama.cpp 与 ChatGPT Desktop 不兼容：POST /v1/responses 探测失败（{status}）。"
                + $" {routeProbe.Diagnostic ?? "请更新 llama.cpp Runtime。"}");
        }

        return settings;
    }

    private static bool IsAddressAlreadyInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException
                && socketException.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }

            if (current.HResult == unchecked((int)0x80072740)
                || current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("10048", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Exception> StopAfterStartupFailureAsync(
        LauncherSettings settings,
        Exception exception,
        string? executablePath = null)
    {
        Exception? cleanupError = null;
        try
        {
            await StopRouterComponentsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            cleanupError = cleanupException;
        }

        _routerInfo = null;
        _activeFingerprint = null;
        var publishedError = cleanupError is null
            ? exception
            : new AggregateException("Local 启动失败，且后台组件未能完整停止。", exception, cleanupError);
        await TryWriteRuntimeStateAsync(new RuntimeState
        {
            SelectedMode = ProviderMode.Local,
            Phase = RuntimePhase.Error,
            RouterExecutablePath = executablePath,
            SelectedModelId = settings.SelectedModelId,
            AgentProcessId = Environment.ProcessId,
            LastError = SafeRuntimeError(publishedError),
        }).ConfigureAwait(false);
        return publishedError;
    }

    private RouterLaunchFingerprint CreateFingerprint(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SelectedModelId))
        {
            throw new InvalidDataException("Local 模式缺少已选择的模型 ID。");
        }

        if (string.IsNullOrWhiteSpace(settings.LlamaRoot))
        {
            throw new InvalidDataException("Local 模式缺少 llama.cpp Runtime Root。");
        }

        return new RouterLaunchFingerprint(
            settings.SelectedModelId,
            Path.GetFullPath(settings.LlamaRoot),
            settings.RouterPort,
            settings.LlamaMetricsSupported,
            ComputeFileFingerprint(_dataPaths.RouterPresetFile));
    }

    private RuntimeState CreateRunningRuntimeState(LauncherSettings settings, string executablePath)
    {
        if (_routerInfo is null || _safetyProxy.PublicBaseUri is null)
        {
            throw new InvalidOperationException("Local Router 或安全代理尚未完成启动。");
        }

        return new RuntimeState
        {
            SelectedMode = ProviderMode.Local,
            Phase = RuntimePhase.Running,
            RouterProcessId = _routerInfo.ProcessId,
            RouterStartedAtUtc = _routerInfo.StartedAtUtc,
            RouterExecutablePath = executablePath,
            RouterBaseUri = _routerInfo.BaseUri.ToString(),
            ProtectedRouterApiKey = _protectedRouterApiKey,
            PublicProxyBaseUri = _safetyProxy.PublicBaseUri.ToString(),
            SelectedModelId = settings.SelectedModelId,
            AgentProcessId = Environment.ProcessId,
        };
    }

    private static string ComputeFileFingerprint(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static Uri PublicBaseUri(int port) => new($"http://127.0.0.1:{port}/");

    private static int ReserveAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task TryWriteRuntimeStateAsync(RuntimeState state)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            string? temporaryPath = null;
            try
            {
                var path = Path.GetFullPath(_dataPaths.RuntimeStateFile);
                var directory = Path.GetDirectoryName(path)
                    ?? throw new InvalidOperationException("运行状态文件必须位于一个目录中。");
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                var publishedState = state with
                {
                    AgentProtocolVersion = RuntimeState.CurrentAgentProtocolVersion,
                    AgentExecutablePath = Environment.ProcessPath,
                    AgentStartedAtUtc = _agentStartedAtUtc,
                    UpdatedAtUtc = _timeProvider.GetUtcNow(),
                };
                await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(publishedState, RuntimeStateSerializerOptions) + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    CancellationToken.None).ConfigureAwait(false);
                PrivateFilePermissions.HardenFile(temporaryPath);
                File.Move(temporaryPath, path, overwrite: true);
                temporaryPath = null;
                _hasPublishedRuntimeState = true;
                _lastPublishedRuntimeState = publishedState;
                _lastRuntimeStateWriteAtUtc = publishedState.UpdatedAtUtc;
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // A stale temporary file is never loaded as runtime state.
                    }
                }
            }

            if (attempt < 2 && lastError is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            break;
        }

        if (lastError is not null && _runtimeStateDiagnosticWriter is not null)
        {
            try
            {
                await _runtimeStateDiagnosticWriter(
                    $"无法写入 runtime-state.json；Router 监督继续运行，但 Launcher 状态识别可能暂时不准确："
                    + $"{lastError.GetType().Name}: {lastError.Message}").ConfigureAwait(false);
            }
            catch
            {
                // Diagnostics must never interrupt Local inference.
            }
        }
    }

    private Task PublishHeartbeatIfDueAsync()
    {
        if (_lastPublishedRuntimeState is null
            || _lastRuntimeStateWriteAtUtc is null
            || _timeProvider.GetUtcNow() - _lastRuntimeStateWriteAtUtc < RuntimeStateHeartbeatInterval)
        {
            return Task.CompletedTask;
        }

        return TryWriteRuntimeStateAsync(_lastPublishedRuntimeState);
    }

    private async Task StopRouterComponentsAsync(CancellationToken cancellationToken)
    {
        Exception? stopError = null;
        if (_safetyProxy.IsRunning)
        {
            try
            {
                await _safetyProxy.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopError = exception;
            }
        }

        if (_processManager.OwnedProcessId is not null)
        {
            try
            {
                await _processManager.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopError = stopError is null
                    ? exception
                    : new AggregateException("Local 后台组件未能完整停止。", stopError, exception);
            }
        }

        if (stopError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopError).Throw();
        }
    }

    private static string SafeRuntimeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    private sealed record RouterLaunchFingerprint(
        string SelectedModelId,
        string RuntimeRoot,
        int RouterPort,
        bool MetricsEnabled,
        string RouterPresetSha256);
}
