namespace Launcher.ChatGPT.Catalog;

public static class CodexReasoningLevels
{
    // The protocol permits model-defined values. Client compatibility is checked separately.
    public static bool IsRecognized(string level) => Launcher.Core.Configuration.ReasoningLevelValue.IsValid(level);
}
