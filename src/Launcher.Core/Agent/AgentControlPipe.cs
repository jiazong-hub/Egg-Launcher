using System.IO.Pipes;
using System.Text;

namespace Launcher.Core.Agent;

public static class AgentControlPipe
{
    public const string DefaultPipeName = "ChatGPTLocalLauncher.Agent.Control.v1";

    private const string ShutdownCommand = "shutdown-v1";
    private const string AcceptedResponse = "ok-v1";

    public static async Task ListenForShutdownAsync(
        Action requestShutdown,
        CancellationToken cancellationToken = default,
        string pipeName = DefaultPipeName)
    {
        ArgumentNullException.ThrowIfNull(requestShutdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(
                server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                server,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(command, ShutdownCommand, StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("unsupported-v1").ConfigureAwait(false);
                continue;
            }

            await writer.WriteLineAsync(AcceptedResponse).ConfigureAwait(false);
            requestShutdown();
            return;
        }
    }

    public static async Task<AgentControlResult> RequestShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string pipeName = DefaultPipeName)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Agent 控制超时必须为正数。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            using var reader = new StreamReader(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            await writer.WriteLineAsync(ShutdownCommand).ConfigureAwait(false);
            var response = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            return string.Equals(response, AcceptedResponse, StringComparison.Ordinal)
                ? new AgentControlResult(true, null)
                : new AgentControlResult(false, "后台 Agent 拒绝了退出请求。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentControlResult(false, "等待后台 Agent 响应超时。");
        }
        catch (IOException exception)
        {
            return new AgentControlResult(false, $"无法连接后台 Agent：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new AgentControlResult(false, $"无权连接后台 Agent：{exception.Message}");
        }
    }
}

public sealed record AgentControlResult(bool Succeeded, string? Diagnostic);
