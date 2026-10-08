using Launcher.ChatGPT.Catalog;
using Launcher.Models.Profiles;

namespace Launcher.Orchestration.Models;

/// <summary>Resolves the exact verified default without changing native capability evidence.</summary>
public static class ReasoningDefaultSelection
{
    public static string? ForCodex(ModelProfile profile)
    {
        if (!profile.ExposeReasoningEffortInChatGpt || !profile.ReasoningResponsesVerified
            || profile.ReasoningClientCompatible != true || profile.ReasoningCapabilityStatus != ReasoningCapabilityStatus.Verified)
            return null;
        bool Supported(string? level) => level is not null && CodexReasoningLevels.IsRecognized(level)
            && profile.SupportedReasoningLevels.Contains(level, StringComparer.Ordinal);
        return Supported(profile.PreferredReasoningLevel) ? profile.PreferredReasoningLevel
            : Supported(profile.DefaultReasoningLevel) ? profile.DefaultReasoningLevel : null;
    }
}
