namespace Launcher.Models.Profiles;

public enum VisionCapabilityStatus
{
    Unknown = 0,
    BuiltInCandidate = 1,
    ExternalConfigured = 2,
    Verified = 3,
    Unsupported = 4,
    ValidationFailed = 5,
}
