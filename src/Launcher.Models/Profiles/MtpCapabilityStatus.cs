namespace Launcher.Models.Profiles;

public enum MtpCapabilityStatus
{
    Unknown = 0,
    EmbeddedCandidate = 1,
    ExternalConfigured = 2,
    Verified = 3,
    Unsupported = 4,
    ValidationFailed = 5,
}
