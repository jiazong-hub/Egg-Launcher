using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public enum ReasoningValidationStateKind { Current, Expired, Unreadable }

public sealed record ReasoningValidationCheck(ReasoningValidationStateKind State, string? Signature, string? Reason)
{
    public ModelProfile ApplyTo(ModelProfile profile) => State == ReasoningValidationStateKind.Current ? profile : profile with
    {
        ReasoningCapabilityStatus = ReasoningCapabilityStatus.Unknown,
        SupportedReasoningLevels = [],
        DefaultReasoningLevel = null,
        SupportsThinkingSwitch = null,
        DefaultThinkingEnabled = null,
        ThinkingEnabled = null,
        ReasoningResponsesVerified = false,
        ExposeReasoningEffortInChatGpt = false,
        ReasoningClientCompatible = null,
        ReasoningClientExecutablePath = null,
        ReasoningLevelAliases = new Dictionary<string, string>(),
        ReasoningCapabilitySignature = null,
        ReasoningCapabilityCheckedAtUtc = null,
        ReasoningValidationDetails = Reason,
    };
}

public static class ReasoningValidationState
{
    public static ReasoningValidationCheck Check(ModelProfile profile, string root)
    {
        try
        {
            var signature = ReasoningValidationFingerprint.Compute(profile, root);
            return signature == profile.ReasoningCapabilitySignature
                ? new(ReasoningValidationStateKind.Current, signature, null)
                : new(ReasoningValidationStateKind.Expired, signature, "模型、模板、运行时或验证规则已变化，请重新检测思考能力。");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return new(ReasoningValidationStateKind.Unreadable, null, "思考验证信息读取失败：" + exception.Message);
        }
    }
}
