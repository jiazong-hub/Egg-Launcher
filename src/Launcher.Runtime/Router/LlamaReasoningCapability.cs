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
    string? DefaultLevel)
{
    public bool? SupportsThinkingSwitch { get; init; }
    public bool? DefaultThinkingEnabled { get; init; }
    public bool ResponsesVerified { get; init; }
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();
    public string Details { get; init; } = string.Empty;
}
