using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Launcher.ChatGPT.Configuration;

namespace Launcher.ChatGPT.Processes;

public sealed class CodexCliSmokeRunner
{
    public async Task<CodexCliSmokeResult> RunAsync(
        CodexCliSmokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var codexHome = Path.GetFullPath(request.IsolatedCodexHome);
        Directory.CreateDirectory(codexHome);
        await EnsureIsolatedLocalAuthAsync(codexHome, cancellationToken).ConfigureAwait(false);
        var firstResult = await RunProcessAsync(
                CreateStartInfo(request, codexHome, resumeThreadId: null),
                request.Timeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!firstResult.Succeeded || string.IsNullOrWhiteSpace(request.FollowUpPrompt))
        {
            return firstResult;
        }

        var threadId = CodexCliJsonTranscript.FindThreadId(firstResult.StandardOutput);
        if (threadId is null)
        {
            return firstResult with
            {
                Succeeded = false,
                StandardError = JoinOutput(
                    firstResult.StandardError,
                    "Codex CLI 首轮输出缺少有效的 thread.started/thread_id，无法验证续接压缩。"),
            };
        }

        var secondResult = await RunProcessAsync(
                CreateStartInfo(request, codexHome, threadId),
                request.Timeout,
                cancellationToken)
            .ConfigureAwait(false);
        return new CodexCliSmokeResult(
            secondResult.Succeeded,
            secondResult.ExitCode,
            secondResult.TimedOut,
            JoinOutput(firstResult.StandardOutput, secondResult.StandardOutput),
            JoinOutput(firstResult.StandardError, secondResult.StandardError),
            firstResult.CommandExecutionAttempts + secondResult.CommandExecutionAttempts,
            firstResult.SuccessfulCommandExecutions + secondResult.SuccessfulCommandExecutions,
            firstResult.PolicyBlockedCommandExecutions + secondResult.PolicyBlockedCommandExecutions,
            firstResult.Elapsed + secondResult.Elapsed);
    }

    private static ProcessStartInfo CreateStartInfo(
        CodexCliSmokeRequest request,
        string codexHome,
        string? resumeThreadId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(request.ExecutablePath),
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var variable in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(variable);
        }

        startInfo.Environment["CODEX_HOME"] = codexHome;
        foreach (var proxyVariable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
        {
            startInfo.Environment.Remove(proxyVariable);
        }

        startInfo.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        AddArgument(startInfo, "--disable", "plugins");
        AddArgument(startInfo, "--disable", "apps");
        AddArgument(startInfo, "--disable", "remote_plugin");
        AddArgument(startInfo, "--disable", "enable_request_compression");
        AddArgument(startInfo, "--disable", "skill_search");
        AddArgument(startInfo, "--disable", "workspace_dependencies");
        AddArgument(startInfo, "--disable", "multi_agent");
        AddArgument(startInfo, "--disable", "browser_use");
        AddArgument(startInfo, "--disable", "computer_use");
        AddArgument(startInfo, "--disable", "image_generation");
        AddArgument(startInfo, "--disable", "goals");
        AddArgument(startInfo, "--disable", "tool_suggest");
        AddArgument(startInfo, "-a", "never");
        AddArgument(startInfo, "--sandbox", "workspace-write");
        startInfo.ArgumentList.Add("exec");
        if (resumeThreadId is not null)
        {
            startInfo.ArgumentList.Add("resume");
        }

        AddArgument(startInfo, "--ignore-user-config");
        AddArgument(startInfo, "--ignore-rules");
        if (resumeThreadId is null && string.IsNullOrWhiteSpace(request.FollowUpPrompt))
        {
            AddArgument(startInfo, "--ephemeral");
        }

        AddArgument(startInfo, "--skip-git-repo-check");
        AddArgument(startInfo, "--json");
        if (resumeThreadId is null)
        {
            AddArgument(startInfo, "--color", "never");
            AddArgument(startInfo, "--cd", Path.GetFullPath(request.WorkingDirectory));
        }

        AddArgument(startInfo, "--model", request.ModelSlug);
        AddArgument(startInfo, "--config", $"model_catalog_json={TomlString(Path.GetFullPath(request.ModelCatalogPath))}");
        AddArgument(
            startInfo,
            "--config",
            "model_provider=\"chatgpt_local_launcher\"");
        var provider = "{ name = \"Local llama.cpp\", "
            + $"base_url = {TomlString(LoopbackEndpoint.NormalizeBaseUrl(request.OpenAIBaseUrl))}, "
            + "wire_api = \"responses\", requires_openai_auth = true, supports_websockets = false }";
        AddArgument(
            startInfo,
            "--config",
            $"model_providers.chatgpt_local_launcher={provider}");
        if (request.AutoCompactTokenLimit is { } autoCompactTokenLimit)
        {
            AddArgument(
                startInfo,
                "--config",
                $"model_auto_compact_token_limit={autoCompactTokenLimit}");
        }

        if (resumeThreadId is not null)
        {
            startInfo.ArgumentList.Add(resumeThreadId);
        }

        startInfo.ArgumentList.Add(resumeThreadId is null ? request.Prompt : request.FollowUpPrompt!);
        return startInfo;
    }

    private static async Task<CodexCliSmokeResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Codex CLI Smoke Test。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await KillAsync(process).ConfigureAwait(false);
            stopwatch.Stop();
            var timedOutOutput = await IgnoreCancellationAsync(outputTask).ConfigureAwait(false);
            var timedOutTranscript = CodexCliJsonTranscript.Analyze(timedOutOutput);
            return new CodexCliSmokeResult(
                false,
                process.HasExited ? process.ExitCode : null,
                true,
                timedOutOutput,
                await IgnoreCancellationAsync(errorTask).ConfigureAwait(false),
                timedOutTranscript.CommandExecutionAttempts,
                timedOutTranscript.SuccessfulCommandExecutions,
                timedOutTranscript.PolicyBlockedCommandExecutions,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }

        stopwatch.Stop();
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var transcript = CodexCliJsonTranscript.Analyze(output);
        return new CodexCliSmokeResult(
            process.ExitCode == 0,
            process.ExitCode,
            false,
            output,
            error,
            transcript.CommandExecutionAttempts,
            transcript.SuccessfulCommandExecutions,
            transcript.PolicyBlockedCommandExecutions,
            stopwatch.Elapsed);
    }

    private static string JoinOutput(string first, string second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return second;
        }

        if (string.IsNullOrEmpty(second))
        {
            return first;
        }

        return first.TrimEnd() + Environment.NewLine + second.TrimStart();
    }

    private static void Validate(CodexCliSmokeRequest request)
    {
        if (!File.Exists(request.ExecutablePath))
        {
            throw new FileNotFoundException("找不到 Codex CLI。", request.ExecutablePath);
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Codex 工作目录不存在：{request.WorkingDirectory}");
        }

        if (!File.Exists(request.ModelCatalogPath))
        {
            throw new FileNotFoundException("找不到本地模型 Catalog。", request.ModelCatalogPath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.FollowUpPrompt is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FollowUpPrompt);
        }
        if (!request.OpenAIBaseUrl.IsAbsoluteUri
            || request.OpenAIBaseUrl.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(request.OpenAIBaseUrl.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("Codex CLI Smoke Test 只允许 HTTP 回环 Provider。", nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Codex CLI Smoke Test timeout 必须为正数。");
        }

        if (request.AutoCompactTokenLimit is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Codex CLI Smoke Test 自动压缩阈值必须为正数。");
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string TomlString(string value) => JsonSerializer.Serialize(value);

    private static async Task EnsureIsolatedLocalAuthAsync(
        string codexHome,
        CancellationToken cancellationToken)
    {
        var authPath = Path.Combine(codexHome, "auth.json");
        if (File.Exists(authPath))
        {
            return;
        }

        var content = JsonSerializer.Serialize(
            new
            {
                OPENAI_API_KEY = "chatgpt-local-launcher-smoke-only",
                auth_mode = "apikey",
            },
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        await File.WriteAllTextAsync(authPath, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task KillAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<string> IgnoreCancellationAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
