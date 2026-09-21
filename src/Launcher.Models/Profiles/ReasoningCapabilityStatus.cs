namespace Launcher.Models.Profiles;

/// <summary>
/// Describes only capability information verified from the active llama.cpp model.
/// Unknown or incomplete information must never be advertised to ChatGPT as a preset list.
/// </summary>
public enum ReasoningCapabilityStatus
{
    Unknown = 0,
    Unsupported = 1,
    SupportedLevelsUnknown = 2,
    Verified = 3,
}
