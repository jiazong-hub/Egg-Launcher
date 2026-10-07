using Launcher.Models.Profiles;

namespace Launcher.Scripts.Templates;

public enum ReasoningValidationStateKind { Current, Expired, Unreadable }

public sealed record ReasoningValidationCheck(ReasoningValidationStateKind State, string? Signature, string? Reason, IReadOnlyDictionary<string, string>? Basis = null)
{
    public ModelProfile ApplyTo(ModelProfile profile) => State == ReasoningValidationStateKind.Current
        ? profile.ReasoningCapabilitySignature == Signature && profile.ReasoningValidationBasis is not null
            ? profile
            : profile with { ReasoningCapabilitySignature = Signature, ReasoningValidationBasis = Basis }
        : profile with
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
            ReasoningValidationBasis = null,
            ReasoningCapabilityCheckedAtUtc = null,
            ReasoningValidationDetails = Reason,
        };
}

public static class ReasoningValidationState
{
    // A stored display preference alone never activates a feature or requires a proof.
    public static bool RequiresCurrentProof(ModelProfile profile) =>
        profile.ThinkingEnabled is not null || profile.ExposeReasoningEffortInChatGpt
        || (profile.ShowThinkingProcess && profile.SupportsThinkingSwitch == true
            && (profile.ThinkingEnabled ?? profile.DefaultThinkingEnabled) == true);

    public static ReasoningValidationCheck Check(ModelProfile profile, string root)
    {
        try
        {
            var signature = ReasoningValidationFingerprint.Compute(profile, root);
            var basis = ReasoningValidationFingerprint.CaptureBasis(profile, root);
            if (signature == profile.ReasoningCapabilitySignature
                || ReasoningValidationFingerprint.ComputeLegacy(profile, root) == profile.ReasoningCapabilitySignature)
                return new(ReasoningValidationStateKind.Current, signature, null, basis);
            var labels = new Dictionary<string, string>
            {
                ["model"] = "模型文件",
                ["template"] = "聊天模板或 Jinja 设置",
                ["runtime"] = "llama.cpp 可执行文件",
                ["libraries"] = "运行时 DLL",
                ["client"] = "Desktop 配套 CLI",
                ["arguments"] = "思考验证相关参数",
            };
            var changes = profile.ReasoningValidationBasis is { } previous
                ? labels.Where(pair => !previous.TryGetValue(pair.Key, out var before) || before != basis[pair.Key]).Select(pair => pair.Value).ToArray()
                : [];
            var reason = changes.Length > 0
                ? string.Join("、", changes) + "与检测时不一致，请重新确认思考能力。"
                : profile.ReasoningValidationBasis is null
                    ? "思考验证依据与原记录不一致，请重新确认；旧记录未保存具体变化项。"
                    : "思考验证记录与当前依据不匹配，请重新确认。";
            return new(ReasoningValidationStateKind.Expired, signature, reason, basis);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return new(ReasoningValidationStateKind.Unreadable, null, "思考验证信息读取失败：" + exception.Message);
        }
    }
}
