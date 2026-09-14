using Launcher.ChatGPT.Processes;
using Launcher.Core.Configuration;
using Launcher.Core.Persistence;
using Launcher.Core.Security;
using Launcher.Orchestration.Agent;
using Launcher.Runtime.Router;
using Launcher.Runtime.Transport;
using System.Text.Json;
using System.Net.Sockets;
using Launcher.Orchestration.ModeSwitch;

namespace Launcher.Tests;

public sealed class LocalRouterSupervisorTests
{
    [Fact]
    public async Task Reconcile_WhenRuntimeStateCannotBeWritten_LogsAndContinuesSupervision()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            Directory.CreateDirectory(paths.RuntimeStateFile);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            string? diagnostic = null;
            await using var supervisor = new LocalRouterSupervisor(
                new MutableSettingsStore(new LauncherSettings
                {
                    SelectedMode = ProviderMode.Local,
                    SelectedModelId = "local-coder",
                    LlamaRoot = runtimeRoot,
                    RouterPort = 18080,
                }),
                new FakeProcessManager(),
                new FakeSafetyProxy(),
                new FakeRouterControlClient(),
                paths,
                runtimeStateDiagnosticWriter: message =>
                {
                    diagnostic = message;
                    return Task.CompletedTask;
                });

            var result = await supervisor.ReconcileAsync();

            Assert.Equal(LocalRouterReconcileAction.Started, result.Action);
            Assert.Contains("runtime-state.json", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenUpstreamPortRaces_RetriesOnlyPortBindingFailures()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var manager = new FakeProcessManager { PortBindingFailuresRemaining = 2 };
            await using var supervisor = new LocalRouterSupervisor(
                new MutableSettingsStore(new LauncherSettings
                {
                    SelectedMode = ProviderMode.Local,
                    SelectedModelId = "local-coder",
                    LlamaRoot = runtimeRoot,
                    RouterPort = 18080,
                }),
                manager,
                new FakeSafetyProxy(),
                new FakeRouterControlClient(),
                paths);

            var result = await supervisor.ReconcileAsync();

            Assert.Equal(LocalRouterReconcileAction.Started, result.Action);
            Assert.Equal(3, manager.StartCount);
            Assert.NotNull(manager.OwnedProcessId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenUpstreamFailsForAnotherReason_DoesNotRetry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var manager = new FakeProcessManager { StartFailure = new InvalidDataException("bad model") };
            await using var supervisor = new LocalRouterSupervisor(
                new MutableSettingsStore(new LauncherSettings
                {
                    SelectedMode = ProviderMode.Local,
                    SelectedModelId = "local-coder",
                    LlamaRoot = runtimeRoot,
                    RouterPort = 18080,
                }),
                manager,
                new FakeSafetyProxy(),
                new FakeRouterControlClient(),
                paths);

            await Assert.ThrowsAsync<InvalidDataException>(() => supervisor.ReconcileAsync());

            Assert.Equal(1, manager.StartCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenPublicPortIsOccupied_MigratesAndPublishesActualEndpoint()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var settingsStore = new MutableSettingsStore(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-coder",
                LlamaRoot = runtimeRoot,
                RouterPort = 18080,
            });
            var proxy = new FakeSafetyProxy
            {
                AddressInUseFailuresRemaining = 1,
                DynamicPort = 19090,
            };
            var migration = new FakeEndpointMigrationCoordinator(settingsStore);
            var routerControl = new FakeRouterControlClient();
            await using var supervisor = new LocalRouterSupervisor(
                settingsStore,
                new FakeProcessManager(),
                proxy,
                routerControl,
                paths,
                endpointMigrationCoordinator: migration);

            await supervisor.ReconcileAsync();

            Assert.Equal(2, proxy.StartCount);
            Assert.Equal(1, migration.MigrationCount);
            Assert.Equal(19090, settingsStore.Settings.RouterPort);
            Assert.Equal(new Uri("http://127.0.0.1:19090/"), routerControl.LastResponsesProbeBaseUri);
            var state = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(paths.RuntimeStateFile));
            Assert.Equal("http://127.0.0.1:19090/", state!.PublicProxyBaseUri);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_LocalLifecycle_LeavesIdleModelManagementToLlamaAndStopsOwnedRouter()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");

            var settingsStore = new MutableSettingsStore(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-coder",
                LlamaRoot = runtimeRoot,
                RouterPort = 18080,
            });
            var manager = new FakeProcessManager();
            var safetyProxy = new FakeSafetyProxy();
            var routerControl = new FakeRouterControlClient();
            const string routerApiKey = "test-local-router-key";
            await using var supervisor = new LocalRouterSupervisor(
                settingsStore,
                manager,
                safetyProxy,
                routerControl,
                paths,
                routerApiKey: routerApiKey);

            var started = await supervisor.ReconcileAsync();
            var runningStateJson = await File.ReadAllTextAsync(paths.RuntimeStateFile);
            var runningState = JsonSerializer.Deserialize<RuntimeState>(runningStateJson);
            var stable = await supervisor.ReconcileAsync();
            settingsStore.Settings = settingsStore.Settings with { SelectedMode = ProviderMode.OpenAI };
            var stopped = await supervisor.ReconcileAsync();

            Assert.Equal(LocalRouterReconcileAction.Started, started.Action);
            Assert.Equal(LocalRouterReconcileAction.None, stable.Action);
            Assert.Equal(LocalRouterReconcileAction.Stopped, stopped.Action);
            Assert.Equal(1, manager.StartCount);
            Assert.Equal(1, manager.StopCount);
            Assert.Equal(1, safetyProxy.StartCount);
            Assert.Equal(1, safetyProxy.StopCount);
            Assert.Equal(1, routerControl.ResponsesProbeCount);
            Assert.Equal(new Uri("http://127.0.0.1:18080/"), routerControl.LastResponsesProbeBaseUri);
            Assert.Equal(new Uri("http://127.0.0.1:18080/"), safetyProxy.PublicBaseUriAtStart);
            var runtimeState = JsonSerializer.Deserialize<RuntimeState>(
                await File.ReadAllTextAsync(paths.RuntimeStateFile));
            Assert.NotNull(runtimeState);
            Assert.Equal(RuntimeState.CurrentAgentProtocolVersion, runtimeState.AgentProtocolVersion);
            Assert.Equal(Environment.ProcessId, runtimeState.AgentProcessId);
            Assert.Equal(Environment.ProcessPath, runtimeState.AgentExecutablePath);
            Assert.Equal(routerApiKey, manager.LastRequest?.Options.ApiKey);
            Assert.Equal(routerApiKey, safetyProxy.UpstreamApiKeyAtStart);
            Assert.Equal(
                routerApiKey,
                CurrentUserSecretProtector.UnprotectString(runningState!.ProtectedRouterApiKey!));
            Assert.DoesNotContain(routerApiKey, runningStateJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_OpenAiOnFreshSupervisor_ReplacesStaleAgentHandshake()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.GetDirectoryName(paths.RuntimeStateFile)!);
            await File.WriteAllTextAsync(paths.RuntimeStateFile, "{\"AgentProtocolVersion\":0}\n");
            await using var supervisor = new LocalRouterSupervisor(
                new MutableSettingsStore(new LauncherSettings { SelectedMode = ProviderMode.OpenAI }),
                new FakeProcessManager(),
                new FakeSafetyProxy(),
                new FakeRouterControlClient(),
                paths);

            await supervisor.ReconcileAsync();

            var runtimeState = JsonSerializer.Deserialize<RuntimeState>(
                await File.ReadAllTextAsync(paths.RuntimeStateFile));
            Assert.NotNull(runtimeState);
            Assert.Equal(RuntimeState.CurrentAgentProtocolVersion, runtimeState.AgentProtocolVersion);
            Assert.Equal(Environment.ProcessId, runtimeState.AgentProcessId);
            Assert.Equal(RuntimePhase.Stopped, runtimeState.Phase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenSelectedLocalModelChanges_RestartsOwnedRouterAndProxy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");

            var settingsStore = new MutableSettingsStore(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-a",
                LlamaRoot = runtimeRoot,
                RouterPort = 18080,
            });
            var manager = new FakeProcessManager();
            var safetyProxy = new FakeSafetyProxy();
            await using var supervisor = new LocalRouterSupervisor(
                settingsStore,
                manager,
                safetyProxy,
                new FakeRouterControlClient(),
                paths);

            var started = await supervisor.ReconcileAsync();
            settingsStore.Settings = settingsStore.Settings with { SelectedModelId = "local-b" };
            var restarted = await supervisor.ReconcileAsync();
            var stable = await supervisor.ReconcileAsync();

            Assert.Equal(LocalRouterReconcileAction.Started, started.Action);
            Assert.Equal(LocalRouterReconcileAction.Restarted, restarted.Action);
            Assert.Equal(LocalRouterReconcileAction.None, stable.Action);
            Assert.Equal(2, manager.StartCount);
            Assert.Equal(1, manager.StopCount);
            Assert.Equal(2, safetyProxy.StartCount);
            Assert.Equal(1, safetyProxy.StopCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenSelectedModelPresetChanges_RestartsOwnedRouterAndProxy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\nc = 8192\n");

            var settingsStore = new MutableSettingsStore(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-coder",
                LlamaRoot = runtimeRoot,
                RouterPort = 18080,
            });
            var manager = new FakeProcessManager();
            var safetyProxy = new FakeSafetyProxy();
            await using var supervisor = new LocalRouterSupervisor(
                settingsStore,
                manager,
                safetyProxy,
                new FakeRouterControlClient(),
                paths);

            var started = await supervisor.ReconcileAsync();
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\nc = 16384\n");
            var restarted = await supervisor.ReconcileAsync();
            var stable = await supervisor.ReconcileAsync();

            Assert.Equal(LocalRouterReconcileAction.Started, started.Action);
            Assert.Equal(LocalRouterReconcileAction.Restarted, restarted.Action);
            Assert.Equal(LocalRouterReconcileAction.None, stable.Action);
            Assert.Equal(2, manager.StartCount);
            Assert.Equal(1, manager.StopCount);
            Assert.Equal(2, safetyProxy.StartCount);
            Assert.Equal(1, safetyProxy.StopCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenOwnedRouterExits_StopsStaleProxyBeforeRestarting()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var settingsStore = new MutableSettingsStore(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-coder",
                LlamaRoot = runtimeRoot,
                RouterPort = 18080,
            });
            var manager = new FakeProcessManager();
            var safetyProxy = new FakeSafetyProxy();
            await using var supervisor = new LocalRouterSupervisor(
                settingsStore,
                manager,
                safetyProxy,
                new FakeRouterControlClient(),
                paths);

            await supervisor.ReconcileAsync();
            manager.SimulateUnexpectedExit();
            var restarted = await supervisor.ReconcileAsync();

            Assert.Equal(LocalRouterReconcileAction.Restarted, restarted.Action);
            Assert.Equal(2, manager.StartCount);
            Assert.Equal(1, safetyProxy.StopCount);
            Assert.Equal(2, safetyProxy.StartCount);
            Assert.True(safetyProxy.IsRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_StableLocalRouter_RefreshesRuntimeStateHeartbeatAfterFiveSeconds()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));
            await using var supervisor = new LocalRouterSupervisor(
                new MutableSettingsStore(new LauncherSettings
                {
                    SelectedMode = ProviderMode.Local,
                    SelectedModelId = "local-coder",
                    LlamaRoot = runtimeRoot,
                    RouterPort = 18080,
                }),
                new FakeProcessManager(),
                new FakeSafetyProxy(),
                new FakeRouterControlClient(),
                paths,
                time);

            await supervisor.ReconcileAsync();
            var first = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(paths.RuntimeStateFile));
            time.Advance(TimeSpan.FromSeconds(4));
            await supervisor.ReconcileAsync();
            var beforeDue = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(paths.RuntimeStateFile));
            time.Advance(TimeSpan.FromSeconds(1));
            await supervisor.ReconcileAsync();
            var afterDue = JsonSerializer.Deserialize<RuntimeState>(await File.ReadAllTextAsync(paths.RuntimeStateFile));

            Assert.Equal(first!.UpdatedAtUtc, beforeDue!.UpdatedAtUtc);
            Assert.Equal(time.GetUtcNow(), afterDue!.UpdatedAtUtc);
            Assert.Equal(first.AgentStartedAtUtc, afterDue.AgentStartedAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenResponsesRouteIsUnavailable_StopsComponentsAndPublishesError()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var manager = new FakeProcessManager();
            var safetyProxy = new FakeSafetyProxy();
            var routerControl = new FakeRouterControlClient
            {
                ResponsesProbeResult = new ResponsesApiRouteProbeResult(
                    false,
                    404,
                    "Router 未暴露可用的 POST /v1/responses 路由。"),
            };
            await using var supervisor = new LocalRouterSupervisor(
                new MutableSettingsStore(new LauncherSettings
                {
                    SelectedMode = ProviderMode.Local,
                    SelectedModelId = "local-coder",
                    LlamaRoot = runtimeRoot,
                    RouterPort = 18080,
                }),
                manager,
                safetyProxy,
                routerControl,
                paths);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => supervisor.ReconcileAsync());

            Assert.Contains("/v1/responses", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, routerControl.ResponsesProbeCount);
            Assert.Equal(1, safetyProxy.StartCount);
            Assert.Equal(1, safetyProxy.StopCount);
            Assert.Equal(1, manager.StopCount);
            Assert.Null(manager.OwnedProcessId);
            var runtimeState = JsonSerializer.Deserialize<RuntimeState>(
                await File.ReadAllTextAsync(paths.RuntimeStateFile));
            Assert.NotNull(runtimeState);
            Assert.Equal(RuntimePhase.Error, runtimeState.Phase);
            Assert.Contains("/v1/responses", runtimeState.LastError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_WhenProxyStopFails_StillStopsOwnedRouter()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = LauncherDataPaths.ForCurrentUser(Path.Combine(root, "data"));
            var runtimeRoot = Path.Combine(root, "llama.cpp");
            Directory.CreateDirectory(runtimeRoot);
            Directory.CreateDirectory(paths.GeneratedDirectory);
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "llama-server.exe"), string.Empty);
            await File.WriteAllTextAsync(paths.RouterPresetFile, "version = 1\n");
            var settingsStore = new MutableSettingsStore(new LauncherSettings
            {
                SelectedMode = ProviderMode.Local,
                SelectedModelId = "local-coder",
                LlamaRoot = runtimeRoot,
                RouterPort = 18080,
            });
            var manager = new FakeProcessManager();
            var safetyProxy = new FakeSafetyProxy();
            await using var supervisor = new LocalRouterSupervisor(
                settingsStore,
                manager,
                safetyProxy,
                new FakeRouterControlClient(),
                paths);
            await supervisor.ReconcileAsync();
            settingsStore.Settings = settingsStore.Settings with { SelectedMode = ProviderMode.OpenAI };
            safetyProxy.ThrowOnStop = true;

            await Assert.ThrowsAsync<IOException>(() => supervisor.ReconcileAsync());

            Assert.Equal(1, manager.StopCount);
            Assert.Null(manager.OwnedProcessId);
            safetyProxy.ThrowOnStop = false;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeSafetyProxy : ILoopbackSafetyProxy
    {
        public bool IsRunning { get; private set; }

        public Uri? PublicBaseUri { get; private set; }

        public Uri? PublicBaseUriAtStart { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public string? UpstreamApiKeyAtStart { get; private set; }

        public bool ThrowOnStop { get; set; }

        public int AddressInUseFailuresRemaining { get; set; }

        public int DynamicPort { get; set; } = 19090;

        public Task StartAsync(
            Uri publicBaseUri,
            Uri upstreamBaseUri,
            CancellationToken cancellationToken = default,
            string? upstreamApiKey = null)
        {
            StartCount++;
            if (AddressInUseFailuresRemaining > 0)
            {
                AddressInUseFailuresRemaining--;
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            IsRunning = true;
            PublicBaseUri = publicBaseUri.Port == 0
                ? new Uri($"http://127.0.0.1:{DynamicPort}/")
                : publicBaseUri;
            PublicBaseUriAtStart = publicBaseUri;
            UpstreamApiKeyAtStart = upstreamApiKey;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnStop)
            {
                throw new IOException("injected proxy stop failure");
            }

            if (IsRunning)
            {
                StopCount++;
                IsRunning = false;
                PublicBaseUri = null;
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private sealed class FakeRouterControlClient : IRouterControlClient
    {
        public ResponsesApiRouteProbeResult ResponsesProbeResult { get; set; } =
            new(true, 400, null);

        public int ResponsesProbeCount { get; private set; }

        public Uri? LastResponsesProbeBaseUri { get; private set; }

        public Task<ResponsesApiRouteProbeResult> ProbeResponsesRouteAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
        {
            ResponsesProbeCount++;
            LastResponsesProbeBaseUri = baseUri;
            return Task.FromResult(ResponsesProbeResult);
        }

        public Task<RouterHealthSnapshot> ProbeAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RouterHealthSnapshot(true, 200, 200, ["local-coder"], null));

        public Task<RouterHealthSnapshot> WaitUntilReadyAsync(
            Uri baseUri,
            TimeSpan timeout,
            Func<bool>? hasExited = null,
            CancellationToken cancellationToken = default) =>
            ProbeAsync(baseUri, cancellationToken);

        public Task<RouterModelActionResult> UnloadModelAsync(
            Uri baseUri,
            string modelId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RouterModelActionResult(true, 200, null));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MutableSettingsStore(LauncherSettings settings) : ISettingsStore
    {
        public LauncherSettings Settings { get; set; } = settings;

        public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(LauncherSettings value, CancellationToken cancellationToken = default)
        {
            Settings = value;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;

        public void Advance(TimeSpan duration) => _value += duration;
    }

    private sealed class FakeProcessManager : ILlamaRouterProcessManager
    {
        public int? OwnedProcessId { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public LlamaRouterStartRequest? LastRequest { get; private set; }

        public int PortBindingFailuresRemaining { get; set; }

        public Exception? StartFailure { get; set; }

        public void SimulateUnexpectedExit() => OwnedProcessId = null;

        public Task<LlamaRouterProcessInfo> StartAsync(
            LlamaRouterStartRequest request,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastRequest = request;
            if (PortBindingFailuresRemaining > 0)
            {
                PortBindingFailuresRemaining--;
                throw new LlamaRouterPortBindingException(
                    request.Options.Port,
                    new IOException("injected bind failure"));
            }

            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            OwnedProcessId = 42;
            return Task.FromResult(new LlamaRouterProcessInfo(
                42,
                DateTimeOffset.UtcNow,
                new Uri("http://127.0.0.1:18080/"),
                "stdout.log",
                "stderr.log",
                new RouterHealthSnapshot(true, 200, 200, ["local-coder"], null)));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (OwnedProcessId is not null)
            {
                StopCount++;
                OwnedProcessId = null;
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }

    private sealed class FakeEndpointMigrationCoordinator(MutableSettingsStore settingsStore)
        : ILocalEndpointMigrationCoordinator
    {
        public int MigrationCount { get; private set; }

        public Task<LauncherSettings> MigrateAsync(
            LauncherSettings expectedSettings,
            Uri publicBaseUri,
            CancellationToken cancellationToken = default)
        {
            MigrationCount++;
            settingsStore.Settings = expectedSettings with
            {
                RouterPort = publicBaseUri.Port,
                PublicProxyPortInitialized = true,
            };
            return Task.FromResult(settingsStore.Settings);
        }
    }

}
