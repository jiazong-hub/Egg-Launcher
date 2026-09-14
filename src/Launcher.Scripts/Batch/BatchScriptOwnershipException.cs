namespace Launcher.Scripts.Batch;

public sealed class BatchScriptOwnershipException(string message) : InvalidOperationException(message);
