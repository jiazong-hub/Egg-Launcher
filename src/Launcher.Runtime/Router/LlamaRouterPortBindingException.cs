namespace Launcher.Runtime.Router;

public sealed class LlamaRouterPortBindingException : IOException
{
    public LlamaRouterPortBindingException(int port, Exception innerException)
        : base($"llama.cpp 无法绑定回环端口 {port}。", innerException)
    {
        Port = port;
    }

    public int Port { get; }
}
