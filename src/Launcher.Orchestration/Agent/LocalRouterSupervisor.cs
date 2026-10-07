using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Core.Persistence;
using Launcher.Core.Security;
using Launcher.Runtime.Transport;
using Launcher.Runtime.Router;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Models.Profiles;

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
    private readonly Action<bool>? _diagnosticModeChanged;
    private readonly Action<string?>? _diagnosticRouterRunIdChanged;
    private readonly Func<string, string, string, Task>? _diagnosticEventWriter;
    private readonly Func<DiagnosticEvent, Task>? _structuredDiagnosticEventWriter;
    private readonly LlamaModelManagementClient? _modelManagementClient;
    private LlamaRouterProcessInfo? _routerInfo;
    private RouterLaunchFingerprint? _activeFingerprint;
    private RuntimeState? _lastPublishedRuntimeState;
    private DateTimeOffset? _lastRuntimeStateWriteAtUtc;
    private ProviderMode _lastKnownSelectedMode = ProviderMode.OpenAI;
    private bool _hasPublishedRuntimeState;
    private bool? _lastDetailedDiagnosticsEnabled;
    private DateTimeOffset? _lastModelHealthProbeAtUtc;
    private string? _lastModelHealthState;
    private bool _disposed;
    private readonly Launcher.ChatGPT.Processes.IChatGptClientDetector _clientDetector;

    public LocalRouterSupervisor(
        ISettingsStore settingsStore,
        ILlamaRouterProcessManager processManager,
        ILoopbackSafetyProxy safetyProxy,
        IRouterControlClient routerControlClient,
        LauncherDataPaths dataPaths,
        TimeProvider? timeProvider = null,
        string? routerApiKey = null,
        ILocalEndpointMigrationCoordinator? endpointMigrationCoordinator = null,
        Func<string, Task>? runtimeStateDiagnosticWriter = null,
        Action<bool>? diagnosticModeChanged = null,
        Action<string?>? diagnosticRouterRunIdChanged = null,
        Func<string, string, string, Task>? diagnosticEventWriter = null,
        LlamaModelManagementClient? modelManagementClient = null,
        Func<DiagnosticEvent, Task>? structuredDiagnosticEventWriter = null,
        Launcher.ChatGPT.Processes.IChatGptClientDetector? clientDetector = null)
    {
        _settingsStore = settingsStore;
        _clientDetector = clientDetector ?? new Launcher.ChatGPT.Processes.ChatGptClientDetector();
        _processManager = processManager;
        _safetyProxy = safetyProxy;
        _routerControlClient = routerControlClient;
        _dataPaths = dataPaths;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _endpointMigrationCoordinator = endpointMigrationCoordinator;
        _runtimeStateDiagnosticWriter = runtimeStateDiagnosticWriter;
        _diagnosticModeChanged = diagnosticModeChanged;
        _diagnosticRouterRunIdChanged = diagnosticRouterRunIdChanged;
        _diagnosticEventWriter = diagnosticEventWriter;
        _structuredDiagnosticEventWriter = structuredDiagnosticEventWriter;
        _modelManagementClient = modelManagementClient;
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
            await ApplyDiagnosticModeAsync(settings.DetailedDiagnosticsEnabled).ConfigureAwait(false);
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
            await CaptureModelHealthIfDueAsync(settings, cancellationToken).ConfigureAwait(false);

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
        var startupStopwatch = Stopwatch.StartNew();
        var startupStage = "create_start_request";
        LlamaRouterStartRequest request;
        try
        {
            ModelProfile? startupProfile = null;
            if (settings.LlamaRoot is string runtimeRoot)
            {
                var profiles = await new JsonModelProfileStore().LoadAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);
                var profile = profiles.Profiles.FirstOrDefault(item => item.Id == settings.SelectedModelId);
                if (profile is not null)
                {
                    var guarded = await Launcher.Orchestration.Models.ReasoningStartupGuard.EnsureAsync(
                        profile, settings, _settingsStore, _dataPaths, _clientDetector, cancellationToken).ConfigureAwait(false);
                    if (!ReferenceEquals(guarded, profile)) settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                    startupProfile = guarded;
                }
            }
            request = CreateStartRequest(settings);
            if (startupProfile is { } validated)
            {
                request = request with
                {
                    ContextShiftRequested = validated.ContextShiftEnabled,
                    ContextShiftDisabledObserver = reason => ContextShiftCapabilityCache.Write(validated, settings.LlamaRoot!,
                        new ContextShiftCapabilityResult(false, reason, DateTimeOffset.UtcNow)),
                };
            }
        }
        catch (Exception exception)
        {
            var failureProperties = DiagnosticSanitizer.CreateExceptionProperties(
                exception,
                includeStackTrace: _lastDetailedDiagnosticsEnabled == true);
            failureProperties["stage"] = startupStage;
            failureProperties["attemptCount"] = 0;
            failureProperties["startupDurationMs"] = startupStopwatch.ElapsedMilliseconds;
            await WriteDiagnosticEventAsync(
                "router_start_failed",
                "error",
                "The Router start request could not be created.",
                failureProperties).ConfigureAwait(false);
            throw;
        }

        await TryWriteRuntimeStateAsync(new RuntimeState
        {
            SelectedMode = ProviderMode.Local,
            Phase = RuntimePhase.Starting,
            RouterExecutablePath = request.ExecutablePath,
            SelectedModelId = settings.SelectedModelId,
            AgentProcessId = Environment.ProcessId,
        }).ConfigureAwait(false);
        var attempt = 0;
        try
        {
            while (true)
            {
                attempt++;
                startupStage = "start_router";
                await WriteDiagnosticEventAsync(
                    "router_start_attempt",
                    "info",
                    "Starting the owned llama.cpp Router process.",
                    new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["maximumPortAttempts"] = MaximumUpstreamPortAttempts,
                        ["upstreamPort"] = request.Options.Port,
                    }).ConfigureAwait(false);
                try
                {
                    _routerInfo = await _processManager.StartAsync(request, cancellationToken).ConfigureAwait(false);
                    _lastModelHealthProbeAtUtc = null;
                    _lastModelHealthState = null;
                    _safetyProxy.SetRouterRunId(_routerInfo.DiagnosticRunId);
                    _diagnosticRouterRunIdChanged?.Invoke(_routerInfo.DiagnosticRunId);
                    var runtimeVersion = ReadRuntimeVersion(request.ExecutablePath);
                    await WriteDiagnosticEventAsync(
                        "router_started",
                        "info",
                        "llama.cpp Router process passed its initial health checks.",
                        new Dictionary<string, object?>
                        {
                            ["processId"] = _routerInfo.ProcessId,
                            ["runtimeVersion"] = SafeDiagnosticToken(runtimeVersion),
                            ["routerRunId"] = _routerInfo.DiagnosticRunId,
                            ["attemptCount"] = attempt,
                            ["startupDurationMs"] = startupStopwatch.ElapsedMilliseconds,
                            ["healthy"] = _routerInfo.InitialHealth.IsHealthy,
                            ["healthStatusCode"] = _routerInfo.InitialHealth.HealthStatusCode,
                            ["modelsStatusCode"] = _routerInfo.InitialHealth.ModelsStatusCode,
                        }).ConfigureAwait(false);
                    break;
                }
                catch (LlamaRouterPortBindingException exception) when (attempt < MaximumUpstreamPortAttempts)
                {
                    var previousPort = request.Options.Port;
                    request = CreateStartRequest(settings);
                    await WriteDiagnosticEventAsync(
                        "router_port_binding_retry",
                        "warning",
                        "The selected upstream port could not be bound; retrying with a new loopback port.",
                        new Dictionary<string, object?>(DiagnosticSanitizer.CreateExceptionProperties(
                            exception,
                            includeStackTrace: _lastDetailedDiagnosticsEnabled == true), StringComparer.Ordinal)
                        {
                            ["attempt"] = attempt,
                            ["nextAttempt"] = attempt + 1,
                            ["previousPort"] = previousPort,
                            ["retryPort"] = request.Options.Port,
                            ["maximumPortAttempts"] = MaximumUpstreamPortAttempts,
                        }).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            var failedRunId = _processManager.LastDiagnosticRunId;
            var failureProperties = new Dictionary<string, object?>(
                DiagnosticSanitizer.CreateExceptionProperties(
                    exception,
                    includeStackTrace: _lastDetailedDiagnosticsEnabled == true),
                StringComparer.Ordinal)
            {
                ["stage"] = startupStage,
                ["attemptCount"] = attempt,
                ["startupDurationMs"] = startupStopwatch.ElapsedMilliseconds,
                ["upstreamPort"] = request.Options.Port,
                ["routerRunId"] = failedRunId ?? _routerInfo?.DiagnosticRunId,
            };
            await WriteDiagnosticEventAsync(
                "router_start_failed",
                "error",
                "Local Router startup did not complete.",
                failureProperties).ConfigureAwait(false);
            await TryWriteRuntimeStateAsync(new RuntimeState
            {
                SelectedMode = ProviderMode.Local,
                Phase = RuntimePhase.Error,
                RouterExecutablePath = request.ExecutablePath,
                SelectedModelId = settings.SelectedModelId,
                AgentProcessId = Environment.ProcessId,
                LastError = SafeRuntimeError(exception),
            }).ConfigureAwait(false);
            _safetyProxy.SetRouterRunId(null);
            _diagnosticRouterRunIdChanged?.Invoke(null);
            throw;
        }

        try
        {
            startupStage = "start_safety_proxy_and_probe_responses";
            settings = await StartAndValidateSafetyProxyAsync(settings, cancellationToken).ConfigureAwait(false);
            _activeFingerprint = CreateFingerprint(settings);
            await TryWriteRuntimeStateAsync(CreateRunningRuntimeState(settings, request.ExecutablePath))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failureProperties = new Dictionary<string, object?>(
                DiagnosticSanitizer.CreateExceptionProperties(
                    exception,
                    includeStackTrace: _lastDetailedDiagnosticsEnabled == true),
                StringComparer.Ordinal)
            {
                ["stage"] = startupStage,
                ["attemptCount"] = attempt,
                ["startupDurationMs"] = startupStopwatch.ElapsedMilliseconds,
                ["processId"] = _routerInfo?.ProcessId,
                ["routerRunId"] = _routerInfo?.DiagnosticRunId,
            };
            await WriteDiagnosticEventAsync(
                "router_start_failed",
                "error",
                "Router started, but the Desktop-facing proxy validation did not complete.",
                failureProperties).ConfigureAwait(false);
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

        var proxyStartupStopwatch = Stopwatch.StartNew();
        var profiles = await new JsonModelProfileStore().LoadAsync(settings.LlamaRoot!, cancellationToken).ConfigureAwait(false);
        var activeProfile = profiles.Profiles.FirstOrDefault(profile => profile.Id == settings.SelectedModelId);
        if (activeProfile is not null && (activeProfile.ThinkingEnabled is not null || activeProfile.ExposeReasoningEffortInChatGpt)
            && Launcher.Scripts.Templates.ReasoningValidationState.Check(activeProfile, settings.LlamaRoot!).State != Launcher.Scripts.Templates.ReasoningValidationStateKind.Current)
            throw new InvalidOperationException("思考验证已过期，请关闭 Codex 后重新保存或检测设置。");
        _safetyProxy.SetThinkingEnabled(activeProfile?.SupportsThinkingSwitch == true ? activeProfile.ThinkingEnabled : null);
        var configuredPort = settings.RouterPort;
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
            await WriteDiagnosticEventAsync(
                "safety_proxy_port_conflict",
                "warning",
                "The configured Desktop-facing loopback port is already in use.",
                new Dictionary<string, object?>
                {
                    ["configuredPort"] = settings.RouterPort,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["hResult"] = $"0x{exception.HResult:X8}",
                }).ConfigureAwait(false);
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
            await WriteDiagnosticEventAsync(
                "endpoint_migration_started",
                "warning",
                "The safety proxy bound an alternate loopback port and is updating the managed client endpoint.",
                new Dictionary<string, object?>
                {
                    ["previousPort"] = configuredPort,
                    ["newPort"] = migratedBaseUri.Port,
                }).ConfigureAwait(false);
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
            await WriteDiagnosticEventAsync(
                "endpoint_migration_completed",
                "info",
                "The managed client endpoint was updated to the alternate loopback port.",
                new Dictionary<string, object?>
                {
                    ["previousPort"] = configuredPort,
                    ["newPort"] = migratedBaseUri.Port,
                }).ConfigureAwait(false);
        }

        publicBaseUri = _safetyProxy.PublicBaseUri ?? publicBaseUri;
        var routeProbe = await _routerControlClient.ProbeResponsesRouteAsync(publicBaseUri, cancellationToken)
            .ConfigureAwait(false);
        if (!routeProbe.IsAvailable)
        {
            await WriteDiagnosticEventAsync(
                "responses_route_probe_failed",
                "error",
                "The Desktop-facing proxy could not validate POST /v1/responses.",
                new Dictionary<string, object?>
                {
                    ["statusCode"] = routeProbe.StatusCode,
                    ["proxyStartupDurationMs"] = proxyStartupStopwatch.ElapsedMilliseconds,
                    ["routerRunId"] = _routerInfo.DiagnosticRunId,
                    ["diagnostic"] = DiagnosticSanitizer.SanitizeText(routeProbe.Diagnostic),
                }).ConfigureAwait(false);
            var status = routeProbe.StatusCode is { } statusCode ? $"HTTP {statusCode}" : "无 HTTP 响应";
            throw new InvalidOperationException(
                $"当前 llama.cpp 与 ChatGPT Desktop 不兼容：POST /v1/responses 探测失败（{status}）。"
                + $" {routeProbe.Diagnostic ?? "请更新 llama.cpp Runtime。"}");
        }

        await WriteDiagnosticEventAsync(
            "safety_proxy_ready",
            "info",
            "The Desktop-facing proxy passed the Responses route probe.",
            new Dictionary<string, object?>
            {
                ["publicPort"] = publicBaseUri.Port,
                ["upstreamPort"] = _routerInfo.BaseUri.Port,
                ["statusCode"] = routeProbe.StatusCode,
                ["proxyStartupDurationMs"] = proxyStartupStopwatch.ElapsedMilliseconds,
                ["routerRunId"] = _routerInfo.DiagnosticRunId,
            }).ConfigureAwait(false);

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
        if (cleanupError is not null)
        {
            await WriteDiagnosticEventAsync(
                "router_startup_cleanup_failed",
                "error",
                "One or more Local background components did not stop after startup failed.",
                new Dictionary<string, object?>
                {
                    ["startupExceptionType"] = exception.GetType().FullName,
                    ["cleanupException"] = DiagnosticSanitizer.CreateExceptionProperties(
                        cleanupError,
                        includeStackTrace: _lastDetailedDiagnosticsEnabled == true),
                }).ConfigureAwait(false);
        }

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
        var writeAttempts = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            writeAttempts = attempt;
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
                    ContextShiftDisabledReason = state.Phase == RuntimePhase.Running
                        ? _processManager.ContextShiftDisabledReason : null,
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

        if (lastError is not null
            && (_structuredDiagnosticEventWriter is not null || _runtimeStateDiagnosticWriter is not null))
        {
            try
            {
                if (_structuredDiagnosticEventWriter is not null)
                {
                    var properties = DiagnosticSanitizer.CreateExceptionProperties(
                        lastError,
                        includeStackTrace: _lastDetailedDiagnosticsEnabled == true);
                    properties["stateFile"] = "runtime-state.json";
                    properties["writeAttempts"] = writeAttempts;
                    await _structuredDiagnosticEventWriter(new DiagnosticEvent(
                        "runtime_state_write_failed",
                        "error",
                        "Could not publish runtime-state.json; Router supervision continues.",
                        properties)).ConfigureAwait(false);
                }
                else if (_runtimeStateDiagnosticWriter is not null)
                {
                    await _runtimeStateDiagnosticWriter(
                        $"无法写入 runtime-state.json；Router 监督继续运行，但 Launcher 状态识别可能暂时不准确："
                        + $"{lastError.GetType().Name}: {lastError.Message}").ConfigureAwait(false);
                }
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
                await WriteDiagnosticEventAsync(
                    "safety_proxy_stop_failed",
                    "error",
                    "The Local safety proxy did not stop cleanly.",
                    DiagnosticSanitizer.CreateExceptionProperties(
                        exception,
                        includeStackTrace: _lastDetailedDiagnosticsEnabled == true)).ConfigureAwait(false);
                stopError = exception;
            }
        }

        var processWasRunning = _processManager.OwnedProcessId is not null;
        var stoppedProcessId = _routerInfo?.ProcessId ?? _processManager.OwnedProcessId;
        if (processWasRunning || _routerInfo is not null)
        {
            try
            {
                await _processManager.StopAsync(cancellationToken).ConfigureAwait(false);
                await WriteDiagnosticEventAsync(
                    "router_stopped",
                    processWasRunning ? "info" : "warning",
                    "The owned llama.cpp Router process has stopped.",
                    new Dictionary<string, object?>
                    {
                        ["processId"] = stoppedProcessId,
                        ["stopRequestedWhileAlive"] = processWasRunning,
                        ["unexpectedExitObserved"] = _routerInfo is not null && !processWasRunning,
                        ["exitCode"] = _processManager.LastExitCode,
                        ["routerRunId"] = _routerInfo?.DiagnosticRunId,
                    })
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await WriteDiagnosticEventAsync(
                    "router_stop_failed",
                    "error",
                    "The owned llama.cpp Router did not stop cleanly.",
                    new Dictionary<string, object?>
                    {
                        ["processId"] = stoppedProcessId,
                        ["routerRunId"] = _routerInfo?.DiagnosticRunId,
                        ["exception"] = DiagnosticSanitizer.CreateExceptionProperties(
                            exception,
                            includeStackTrace: _lastDetailedDiagnosticsEnabled == true),
                    }).ConfigureAwait(false);
                stopError = stopError is null
                    ? exception
                    : new AggregateException("Local 后台组件未能完整停止。", stopError, exception);
            }
        }

        _safetyProxy.SetRouterRunId(null);
        _diagnosticRouterRunIdChanged?.Invoke(null);

        if (stopError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopError).Throw();
        }
    }

    private async Task ApplyDiagnosticModeAsync(bool detailedDiagnosticsEnabled)
    {
        _safetyProxy.SetDetailedDiagnosticsEnabled(detailedDiagnosticsEnabled);
        _processManager.SetDetailedDiagnosticsEnabled(detailedDiagnosticsEnabled);
        _diagnosticModeChanged?.Invoke(detailedDiagnosticsEnabled);

        if (_lastDetailedDiagnosticsEnabled == detailedDiagnosticsEnabled)
        {
            return;
        }

        var previousMode = _lastDetailedDiagnosticsEnabled is true ? "full" : "concise";
        _lastDetailedDiagnosticsEnabled = detailedDiagnosticsEnabled;
        var selectedMode = detailedDiagnosticsEnabled ? "full" : "concise";
        await WriteDiagnosticEventAsync(
            "diagnostic_mode_changed",
            "info",
            "Diagnostic recording mode changed.",
            new Dictionary<string, object?>
            {
                ["mode"] = selectedMode,
                ["previousMode"] = previousMode,
            }).ConfigureAwait(false);
    }

    private async Task CaptureModelHealthIfDueAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken)
    {
        if (_modelManagementClient is null || _routerInfo is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (_lastModelHealthProbeAtUtc is { } previous
            && now - previous < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastModelHealthProbeAtUtc = now;
        var probeStopwatch = Stopwatch.StartNew();
        try
        {
            var models = await _modelManagementClient.ListAsync(
                _routerInfo.BaseUri,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var selected = models.FirstOrDefault(model =>
                string.Equals(model.Id, settings.SelectedModelId, StringComparison.Ordinal));
            var status = selected is null ? "not_reported" : SafeDiagnosticToken(selected.Status);
            var failed = selected?.Failed ?? false;
            var exitCode = selected?.ExitCode;
            var state = $"{status}|{failed}|{exitCode?.ToString() ?? "unknown"}";
            if (_lastDetailedDiagnosticsEnabled == true
                || !string.Equals(state, _lastModelHealthState, StringComparison.Ordinal))
            {
                await WriteDiagnosticEventAsync(
                    "router_model_worker_health",
                    failed ? "error" : "info",
                    "Observed the selected llama.cpp model worker state.",
                    new Dictionary<string, object?>
                    {
                        ["status"] = status,
                        ["failed"] = failed,
                        ["exitCode"] = exitCode,
                        ["modelReported"] = selected is not null,
                        ["probeDurationMs"] = probeStopwatch.ElapsedMilliseconds,
                        ["routerRunId"] = _routerInfo.DiagnosticRunId,
                    }).ConfigureAwait(false);
            }

            _lastModelHealthState = state;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var state = $"probe_failed|{exception.GetType().Name}";
            if (_lastDetailedDiagnosticsEnabled == true
                || !string.Equals(state, _lastModelHealthState, StringComparison.Ordinal))
            {
                await WriteDiagnosticEventAsync(
                    "router_model_worker_health_probe_failed",
                    "warning",
                    "The model worker health probe failed.",
                    new Dictionary<string, object?>(
                        DiagnosticSanitizer.CreateExceptionProperties(
                            exception,
                            includeStackTrace: _lastDetailedDiagnosticsEnabled == true),
                        StringComparer.Ordinal)
                    {
                        ["probeDurationMs"] = probeStopwatch.ElapsedMilliseconds,
                        ["routerRunId"] = _routerInfo.DiagnosticRunId,
                    }).ConfigureAwait(false);
            }

            _lastModelHealthState = state;
        }
    }

    private async Task WriteDiagnosticEventAsync(
        string eventName,
        string level,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (_structuredDiagnosticEventWriter is null && _diagnosticEventWriter is null)
        {
            return;
        }

        try
        {
            if (_structuredDiagnosticEventWriter is not null)
            {
                await _structuredDiagnosticEventWriter(new DiagnosticEvent(eventName, level, message, properties))
                    .ConfigureAwait(false);
            }
            else if (_diagnosticEventWriter is not null)
            {
                await _diagnosticEventWriter(eventName, level, message).ConfigureAwait(false);
            }
        }
        catch
        {
            // Diagnostics must never interrupt Local inference supervision.
        }
    }

    private static string SafeDiagnosticToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var safe = new string(value
            .Take(80)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            .ToArray());
        return string.IsNullOrEmpty(safe) ? "unknown" : safe;
    }

    private static string ReadRuntimeVersion(string executablePath)
    {
        try
        {
            return SafeDiagnosticToken(FileVersionInfo.GetVersionInfo(executablePath).ProductVersion);
        }
        catch
        {
            return "unknown";
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
