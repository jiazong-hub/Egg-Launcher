using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Launcher.ChatGPT.Catalog;
using Launcher.ChatGPT.Configuration;
using Launcher.ChatGPT.Discovery;
using Launcher.ChatGPT.Processes;
using Launcher.Core.Agent;
using Launcher.Core.Configuration;
using Launcher.Core.Diagnostics;
using Launcher.Core.Persistence;
using Launcher.Models.Profiles;
using Launcher.Models.Scanning;
using Launcher.Orchestration.Agent;
using Launcher.Orchestration.ModeSwitch;
using Launcher.Runtime.Detection;
using Launcher.Runtime.Processes;
using Launcher.Runtime.Router;
using Launcher.Runtime.Transport;
using Launcher.Scripts.RouterPreset;
using Launcher.Scripts.Templates;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    await RunAsync(args);
}
catch (Exception exception)
{
    ReportFatalError(exception);
    Environment.ExitCode = 1;
}

static async Task RunAsync(string[] args)
{
    if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Launcher.Agent self-test succeeded.");
        return;
    }

    var awaitInitialLocalSwitch = args.Contains("--await-local-switch", StringComparer.OrdinalIgnoreCase);
    var paths = LauncherDataPaths.ForCurrentUser();
    var agentLog = new JsonLineDiagnosticLog(paths.AgentLogFile);
    using var settingsStore = new JsonSettingsStore(paths.SettingsFile);
    var codexHome = GetOptionValue(args, "--codex-home");
    var inspector = new ChatGptIntegrationInspector(codexHome);
    var clientDetector = new ChatGptClientDetector();

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    if (args.Contains("--diagnose-agent-log", StringComparer.OrdinalIgnoreCase))
    {
        await agentLog.AppendAsync("info", "Agent diagnostic log self-test succeeded.");
        Console.WriteLine($"Agent diagnostic log: {agentLog.Path}");
        return;
    }

    var codexToolsSmokeRuntimeRoot = GetOptionValue(args, "--smoke-codex-tools");
    var codexCompactionSmokeRuntimeRoot = GetOptionValue(args, "--smoke-codex-compaction");
    var codexSmokeRuntimeRoot = codexToolsSmokeRuntimeRoot
        ?? codexCompactionSmokeRuntimeRoot
        ?? GetOptionValue(args, "--smoke-codex");
    var responsesSmokeRuntimeRoot = GetOptionValue(args, "--smoke-responses");
    var smokeRuntimeRoot = codexSmokeRuntimeRoot ?? responsesSmokeRuntimeRoot ?? GetOptionValue(args, "--smoke-router");
    if (smokeRuntimeRoot is not null)
    {
        await SmokeRouterAsync(
            smokeRuntimeRoot,
            runInference: responsesSmokeRuntimeRoot is not null || codexSmokeRuntimeRoot is not null,
            runCodex: codexSmokeRuntimeRoot is not null,
            requireCodexTool: codexToolsSmokeRuntimeRoot is not null,
            requireCodexCompaction: codexCompactionSmokeRuntimeRoot is not null);

        if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
    }

    await ReportAsync();

    var runtimeRoot = GetOptionValue(args, "--probe-runtime");
    if (runtimeRoot is not null)
    {
        await ReportRuntimeAsync(runtimeRoot);
    }

    var modelsRoot = GetOptionValue(args, "--scan-models");
    if (modelsRoot is not null)
    {
        ReportModels(modelsRoot);
    }

    if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
    {
        return;
    }

    using var singleInstanceMutex = new Mutex(
        initiallyOwned: true,
        name: @"Local\ChatGPTLocalLauncher.Agent",
        createdNew: out var ownsSingleInstance);
    if (!ownsSingleInstance)
    {
        Console.WriteLine("Agent 已在当前用户会话中运行。");
        return;
    }

    try
    {
        var recovery = await new ModeRecoveryCoordinator(
            settingsStore,
            new ChatGptConfigTransactionService(clientDetector),
            paths).RecoverAsync(shutdown.Token);
        if (recovery.Changed)
        {
            await WriteAgentLogAsync("warning", "Recovered an interrupted mode switch before router reconciliation.");
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        await WriteAgentLogAsync("error", $"Interrupted mode switch requires attention: {exception.Message}");
        return;
    }

    await WriteAgentLogAsync("info", "Agent started.");
    var routerApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    using var agentHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    agentHttpClient.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", routerApiKey);
    var agentRouterClient = new RouterHealthClient(agentHttpClient);
    var agentConfigTransactionService = new ChatGptConfigTransactionService(clientDetector);
    await using var supervisor = new LocalRouterSupervisor(
        settingsStore,
        new LlamaRouterProcessManager(agentRouterClient),
        new LoopbackSafetyProxy(paths.ProxyLogFile),
        agentRouterClient,
        paths,
        routerApiKey: routerApiKey,
        endpointMigrationCoordinator: new LocalEndpointMigrationCoordinator(
            settingsStore,
            clientDetector,
            agentConfigTransactionService,
            paths),
        runtimeStateDiagnosticWriter: message => WriteAgentLogAsync("error", message));
    using var controlCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
    var controlTask = AgentControlPipe.ListenForShutdownAsync(
        shutdown.Cancel,
        controlCancellation.Token);
    string? lastSupervisorMessage = null;

    Console.WriteLine("Agent 正在监督持久化模式。按 Ctrl+C 退出。");
    try
    {
        var reconcileResult = await ReconcileSupervisorAsync();
        if (reconcileResult?.SelectedMode == ProviderMode.OpenAI && awaitInitialLocalSwitch)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (reconcileResult?.SelectedMode == ProviderMode.OpenAI
                && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown.Token);
                reconcileResult = await ReconcileSupervisorAsync();
            }
        }

        if (reconcileResult?.SelectedMode == ProviderMode.OpenAI)
        {
            await WriteAgentLogAsync("info", "OpenAI mode needs no resident Local Agent; exiting.");
        }
        else
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(shutdown.Token))
            {
                reconcileResult = await ReconcileSupervisorAsync();
                if (reconcileResult?.SelectedMode == ProviderMode.OpenAI)
                {
                    await WriteAgentLogAsync("info", "Local mode ended; resident Agent is exiting.");
                    break;
                }
            }
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    finally
    {
        controlCancellation.Cancel();
        try
        {
            await controlTask;
        }
        catch (OperationCanceledException) when (controlCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await WriteAgentLogAsync("error", $"Agent control channel stopped unexpectedly: {exception.Message}");
        }
    }

    await WriteAgentLogAsync("info", "Agent stopped.");

    async Task<LocalRouterReconcileResult?> ReconcileSupervisorAsync()
    {
        try
        {
            var result = await supervisor.ReconcileAsync(shutdown.Token);
            var message = result.Message;
            if (!string.IsNullOrWhiteSpace(message) && !string.Equals(message, lastSupervisorMessage, StringComparison.Ordinal))
            {
                Console.WriteLine($"Agent: {message}");
                await WriteAgentLogAsync("info", message);
            }

            lastSupervisorMessage = message;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"Local Router 协调失败：{exception.Message}";
            if (!string.Equals(message, lastSupervisorMessage, StringComparison.Ordinal))
            {
                Console.WriteLine($"Agent: {message}");
                await WriteAgentLogAsync("error", message);
            }

            lastSupervisorMessage = message;
            return null;
        }
    }

    async Task WriteAgentLogAsync(string level, string message)
    {
        try
        {
            await agentLog.AppendAsync(level, message, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Agent supervision must remain available even when diagnostics cannot be written.
            Console.Error.WriteLine($"Agent diagnostic logging failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    async Task ReportAsync()
    {
        var settings = await settingsStore.LoadAsync(shutdown.Token);
        var integration = await inspector.InspectAsync(shutdown.Token);

        Console.WriteLine($"SelectedMode: {settings.SelectedMode}");
        Console.WriteLine($"SelectedModelId: {settings.SelectedModelId ?? "<none>"}");
        Console.WriteLine($"ChatGPT Desktop running: {clientDetector.IsRunning()}");
        Console.WriteLine($"Codex config found: {integration.ConfigExists}");
        Console.WriteLine($"History state found: {integration.HistoryStateExists}");
        Console.WriteLine($"Managed keys present: {string.Join(", ", integration.ManagedKeysPresent)}");
        Console.WriteLine("auth.json 内容未读取。");
    }

    async Task ReportRuntimeAsync(string root)
    {
        var probe = new LlamaRuntimeProbe(new ProcessRunner());
        var result = await probe.ProbeAsync(root, shutdown.Token);

        Console.WriteLine($"llama-server: {result.ExecutablePath}");
        Console.WriteLine($"Runtime valid: {result.IsValid}");
        Console.WriteLine($"Version: {result.VersionText ?? "<unknown>"}");
        Console.WriteLine($"Router supported: {result.Capabilities.SupportsRouter}");
        Console.WriteLine($"Idle sleep supported: {result.Capabilities.SupportsIdleSleep}");
        Console.WriteLine($"Chat template file supported: {result.Capabilities.SupportsChatTemplateFile}");
        Console.WriteLine($"Metrics supported: {result.Capabilities.SupportsMetrics}");
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.WriteLine($"Diagnostic: {diagnostic}");
        }
    }

    static void ReportModels(string root)
    {
        var result = new GgufModelScanner().Scan(root);
        Console.WriteLine($"GGUF models: {result.Models.Count}");
        foreach (var model in result.Models)
        {
            Console.WriteLine($"Model: {model.DisplayName} | shards={model.ShardCount} | bytes={model.TotalSizeBytes}");
        }

        foreach (var excluded in result.ExcludedFiles)
        {
            Console.WriteLine($"Excluded: {Path.GetFileName(excluded.Path)} | {excluded.Reason}");
        }
    }

    async Task SmokeRouterAsync(
        string root,
        bool runInference,
        bool runCodex,
        bool requireCodexTool,
        bool requireCodexCompaction)
    {
        var runtimeRoot = Path.GetFullPath(root);
        var executablePath = Path.Combine(runtimeRoot, "llama-server.exe");
        var modelsRoot = Path.Combine(runtimeRoot, "models");
        var scan = new GgufModelScanner().Scan(modelsRoot, shutdown.Token);
        var candidate = scan.Models.FirstOrDefault()
            ?? throw new InvalidOperationException($"未在 {modelsRoot} 找到可用的 GGUF 主模型。");
        var alias = MakeSafeAlias(candidate.DisplayName);
        var profile = new ModelProfile
        {
            Id = alias,
            DisplayName = candidate.DisplayName,
            ModelRelativePath = Path.GetRelativePath(runtimeRoot, candidate.PrimaryPath),
            Alias = alias,
            ContextSize = runCodex ? 8192 : runInference ? 4096 : 8192,
            GpuLayers = "auto",
            FlashAttention = "auto",
            CacheTypeK = "f16",
            CacheTypeV = "f16",
            Parallel = 1,
            Jinja = true,
        };

        var smokeRoot = Path.Combine(
            Path.GetTempPath(),
            "ChatGPTLocalLauncher.Smoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(smokeRoot);
        var presetPath = Path.Combine(smokeRoot, "models.ini");
        var catalogPath = Path.Combine(smokeRoot, "local-models.json");
        var proxyLogPath = Path.Combine(smokeRoot, "proxy.jsonl");

        try
        {
            string? chatTemplateOverridePath = null;
            var embeddedTemplate = CodexChatTemplateCompatibility.ReadEmbeddedChatTemplate(
                candidate.PrimaryPath,
                shutdown.Token);
            if (CodexChatTemplateCompatibility.TryCreateCompatibleTemplate(
                embeddedTemplate,
                out var compatibleTemplate))
            {
                chatTemplateOverridePath = Path.Combine(smokeRoot, "codex-compatible.jinja");
                await File.WriteAllTextAsync(
                    chatTemplateOverridePath,
                    CodexChatTemplateCompatibility.OwnershipMarker + "\n" + compatibleTemplate,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    shutdown.Token);
            }

            await File.WriteAllTextAsync(
                presetPath,
                RouterPresetGenerator.Generate(
                    profile,
                    runtimeRoot,
                    loadOnStartup: false,
                    chatTemplateOverridePath),
                shutdown.Token);
            await LocalModelCatalogBuilder.WriteAtomicallyAsync(
                catalogPath,
                new LocalModelCatalogOptions
                {
                    Slug = alias,
                    DisplayName = candidate.DisplayName,
                    ContextWindow = profile.ContextSize,
                },
                shutdown.Token);

            var upstreamPort = ReserveAvailableLoopbackPort();
            var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var healthClient = new RouterHealthClient(httpClient);
            await using var manager = new LlamaRouterProcessManager(healthClient);
            var info = await manager.StartAsync(
                new LlamaRouterStartRequest
                {
                    ExecutablePath = executablePath,
                    WorkingDirectory = runtimeRoot,
                    LogDirectory = smokeRoot,
                    Options = new LlamaRouterOptions
                    {
                        Host = IPAddress.Loopback.ToString(),
                        Port = upstreamPort,
                        ModelsPresetPath = presetPath,
                        MaximumLoadedModels = 1,
                        AutoloadModels = true,
                        ApiKey = apiKey,
                    },
                    StartupTimeout = TimeSpan.FromSeconds(20),
                },
                shutdown.Token);

            var publicPort = ReserveAvailableLoopbackPort();
            await using var safetyProxy = new LoopbackSafetyProxy(proxyLogPath);
            var publicBaseUri = new Uri($"http://{IPAddress.Loopback}:{publicPort}/");
            await safetyProxy.StartAsync(publicBaseUri, info.BaseUri, shutdown.Token, apiKey);

            Console.WriteLine($"Router smoke PID: {info.ProcessId}");
            Console.WriteLine($"Router smoke upstream URL: {info.BaseUri}");
            Console.WriteLine($"Desktop safety proxy URL: {publicBaseUri}");
            Console.WriteLine($"Router smoke healthy: {info.InitialHealth.IsHealthy}");
            Console.WriteLine($"Router smoke models: {string.Join(", ", info.InitialHealth.ModelIds)}");

            if (!info.InitialHealth.ModelIds.Contains(alias, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Router 模型清单未返回预期 Alias：{alias}。");
            }

            var publicHealth = await healthClient.ProbeAsync(publicBaseUri, shutdown.Token);
            Console.WriteLine($"Public readiness healthy: {publicHealth.IsHealthy}");
            Console.WriteLine($"Public readiness models: {string.Join(", ", publicHealth.ModelIds)}");
            if (!publicHealth.IsHealthy
                || !publicHealth.ModelIds.Contains(alias, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Desktop-facing 安全代理的 health/models 就绪探测未返回预期模型。");
            }

            var responsesApi = await healthClient.ProbeResponsesRouteAsync(publicBaseUri, shutdown.Token);
            Console.WriteLine($"Responses API route available: {responsesApi.IsAvailable}");
            Console.WriteLine($"Responses API route status: {responsesApi.StatusCode?.ToString() ?? "<none>"}");
            if (!responsesApi.IsAvailable)
            {
                throw new InvalidOperationException(responsesApi.Diagnostic ?? "Responses API 路由探测失败。");
            }

            if (runInference)
            {
                Console.WriteLine("Responses inference: loading local model...");
                using var inferenceHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var inference = await new LocalResponsesClient(inferenceHttpClient).CreateAsync(
                    publicBaseUri,
                    alias,
                    "Do not explain or reason. Reply with exactly LOCAL_SMOKE_OK.",
                    maxOutputTokens: 1024,
                    shutdown.Token);
                Console.WriteLine($"Responses inference succeeded: {inference.Succeeded}");
                Console.WriteLine($"Responses inference status: {inference.StatusCode?.ToString() ?? "<none>"}");
                Console.WriteLine($"Responses object status: {inference.ResponseStatus ?? "<none>"}");
                Console.WriteLine($"Responses incomplete reason: {inference.IncompleteReason ?? "<none>"}");
                Console.WriteLine($"Responses inference elapsed: {inference.Elapsed.TotalSeconds:0.00}s");
                Console.WriteLine($"Responses inference output: {inference.OutputText ?? "<none>"}");
                if (!inference.Succeeded)
                {
                    throw new InvalidOperationException(inference.Diagnostic ?? "Responses 推理失败。");
                }

                if (runCodex)
                {
                    var codexExecutable = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs",
                        "OpenAI",
                        "Codex",
                        "bin",
                        "codex.exe");
                    var codexWorkspace = Path.Combine(smokeRoot, "codex-workspace");
                    Directory.CreateDirectory(codexWorkspace);
                    await File.WriteAllTextAsync(
                        Path.Combine(codexWorkspace, "README.md"),
                        "# ChatGPT Local Launcher\n",
                        shutdown.Token);
                    Console.WriteLine(
                        requireCodexCompaction
                            ? "Codex CLI smoke: starting isolated two-turn compaction task..."
                            : "Codex CLI smoke: starting isolated ephemeral task...");
                    var compactablePrompt = string.Concat(
                        Enumerable.Repeat(
                            "Keep this context while processing the final instruction. ",
                            96))
                        + "Do not use tools. Reply with exactly CODEX_LOCAL_OK.";
                    var codexResult = await new CodexCliSmokeRunner().RunAsync(
                        new CodexCliSmokeRequest
                        {
                            ExecutablePath = codexExecutable,
                            WorkingDirectory = codexWorkspace,
                            IsolatedCodexHome = Path.Combine(smokeRoot, "codex-home"),
                            ModelCatalogPath = catalogPath,
                            ModelSlug = alias,
                            OpenAIBaseUrl = new Uri(publicBaseUri, "v1"),
                            Prompt = requireCodexCompaction
                                ? compactablePrompt
                                : requireCodexTool
                                ? "Use a shell tool to read the first line of README.md. "
                                    + "If and only if it starts with '# ChatGPT Local Launcher', reply exactly CODEX_LOCAL_OK."
                                : "Do not use tools. Reply with exactly CODEX_LOCAL_OK.",
                            FollowUpPrompt = requireCodexCompaction
                                ? "Do not use tools. Reply with exactly CODEX_LOCAL_OK."
                                : null,
                            // Keep this smoke-only threshold low enough to force a compaction turn.
                            AutoCompactTokenLimit = requireCodexCompaction ? 500 : null,
                        },
                        shutdown.Token);
                    Console.WriteLine($"Codex CLI smoke succeeded: {codexResult.Succeeded}");
                    Console.WriteLine($"Codex CLI smoke exit code: {codexResult.ExitCode?.ToString() ?? "<none>"}");
                    Console.WriteLine($"Codex CLI smoke elapsed: {codexResult.Elapsed.TotalSeconds:0.00}s");
                    Console.WriteLine($"Codex CLI command execution attempts: {codexResult.CommandExecutionAttempts}");
                    Console.WriteLine($"Codex CLI successful command executions: {codexResult.SuccessfulCommandExecutions}");
                    Console.WriteLine($"Codex CLI policy-blocked command executions: {codexResult.PolicyBlockedCommandExecutions}");
                    ReportBoundedProcessOutput("Codex CLI smoke stdout", codexResult.StandardOutput);
                    ReportBoundedProcessOutput("Codex CLI smoke stderr", codexResult.StandardError);

                    if (!codexResult.Succeeded)
                    {
                        throw new InvalidOperationException(
                            codexResult.TimedOut
                                ? "隔离 Codex CLI 已超时；连接、模型或工具策略需分别检查。"
                                : $"隔离 Codex CLI 进程失败，退出代码 {codexResult.ExitCode?.ToString() ?? "未知"}。");
                    }

                    if (requireCodexTool && codexResult.SuccessfulCommandExecutions == 0)
                    {
                        throw new InvalidOperationException(
                            codexResult.PolicyBlockedCommandExecutions > 0
                                ? "模型已经生成工具调用，但 Codex 或 Windows 策略拒绝执行；本次工具能力测试未完成，不能判定为 llama.cpp 连接失败。"
                                : codexResult.CommandExecutionAttempts > 0
                                    ? "模型已经生成工具调用，但命令执行失败；请检查命令输出和隔离工作区。"
                                    : "本地模型未生成可执行的 shell 工具调用；严格工具门未通过。");
                    }

                    if (!codexResult.StandardOutput.Contains("CODEX_LOCAL_OK", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("隔离 Codex CLI 未返回预期的 CODEX_LOCAL_OK。");
                    }

                    if (requireCodexCompaction)
                    {
                        var diagnostics = File.Exists(proxyLogPath)
                            ? await File.ReadAllTextAsync(proxyLogPath, shutdown.Token)
                            : string.Empty;
                        var usedLocalSummaryTurn = diagnostics.Contains(
                            "\"protocolShape\":\"codex_local_summary_turn\"",
                            StringComparison.Ordinal);
                        var usedUnsupportedRemoteProtocol = diagnostics.Contains(
                            "\"protocolShape\":\"remote_v2_trigger\"",
                            StringComparison.Ordinal)
                            || diagnostics.Contains(
                                "\"protocolShape\":\"legacy_remote_endpoint\"",
                                StringComparison.Ordinal);
                        Console.WriteLine($"Codex native local compaction observed: {usedLocalSummaryTurn}");
                        if (!usedLocalSummaryTurn || usedUnsupportedRemoteProtocol)
                        {
                            throw new InvalidOperationException(
                                "未观察到 Codex 原生本地摘要轮次，或仍出现了不受支持的远程压缩协议。");
                        }
                    }
                }

                // Model management is intentionally not exposed through the Desktop-facing proxy.
                // The Agent owns the router process and uses its private loopback endpoint instead.
                var unload = await healthClient.UnloadModelAsync(info.BaseUri, alias, shutdown.Token);
                Console.WriteLine($"Responses model unloaded: {unload.Succeeded}");
                if (!unload.Succeeded)
                {
                    throw new InvalidOperationException(unload.Diagnostic ?? "Responses 测试后模型卸载失败。");
                }
            }

            await safetyProxy.StopAsync(CancellationToken.None);
            await manager.StopAsync(CancellationToken.None);
            Console.WriteLine("Router smoke stopped: true");
        }
        catch
        {
            ReportSmokeLogTail(smokeRoot);
            throw;
        }
        finally
        {
            DeleteOwnedSmokeDirectory(smokeRoot);
        }
    }

    static void ReportBoundedProcessOutput(string label, string output)
    {
        const int maximumCharacters = 4000;
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var suffix = trimmed.Length <= maximumCharacters
            ? trimmed
            : "<earlier output omitted>" + Environment.NewLine + trimmed[^maximumCharacters..];
        Console.WriteLine($"{label}: {suffix}");
    }

    static void ReportSmokeLogTail(string smokeRoot)
    {
        foreach (var logPath in Directory.EnumerateFiles(smokeRoot, "*.log", SearchOption.TopDirectoryOnly))
        {
            Console.WriteLine($"--- {Path.GetFileName(logPath)} (tail) ---");
            foreach (var line in File.ReadLines(logPath).TakeLast(40))
            {
                Console.WriteLine(line);
            }
        }
    }

    static int ReserveAvailableLoopbackPort()
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

    static string MakeSafeAlias(string displayName)
    {
        var characters = displayName
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '-')
            .ToArray();
        var alias = new string(characters).Trim('-');
        return string.IsNullOrWhiteSpace(alias) ? "local-model" : alias;
    }

    static void DeleteOwnedSmokeDirectory(string smokeRoot)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher.Smoke"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(smokeRoot);
        if (!target.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理不属于本次 Smoke Test 的目录。");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    static string? GetOptionValue(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

}

static void ReportFatalError(Exception exception)
{
    var summary = $"Launcher.Agent 已安全停止：{exception.GetType().Name}: {exception.Message}";

    try
    {
        Console.Error.WriteLine(summary);
    }
    catch
    {
        // A release WinExe may not have a console attached.
    }

    try
    {
        var directory = Path.Combine(Path.GetTempPath(), "ChatGPTLocalLauncher");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "Launcher.Agent.fatal.log");
        var diagnostic = $"{DateTimeOffset.Now:O} {summary}{Environment.NewLine}{exception}{Environment.NewLine}";
        File.AppendAllText(logPath, diagnostic, Encoding.UTF8);
    }
    catch
    {
        // Failure reporting must never turn a recoverable startup failure into a Windows crash dialog.
    }
}
