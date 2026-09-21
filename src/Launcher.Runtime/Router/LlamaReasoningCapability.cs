namespace Launcher.Runtime.Router;

public enum LlamaReasoningCapabilityStatus
{
    Unknown = 0,
    Unsupported = 1,
    SupportedLevelsUnknown = 2,
    Verified = 3,
}

public sealed record LlamaReasoningCapability(
    LlamaReasoningCapabilityStatus Status,
    IReadOnlyList<string> SupportedLevels,
    string? DefaultLevel);
