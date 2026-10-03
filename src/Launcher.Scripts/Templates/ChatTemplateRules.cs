using System.Text.RegularExpressions;

namespace Launcher.Scripts.Templates;

public enum ChatTemplateMatchKind { NotMatched, Repairable, Ambiguous }

public sealed record ChatTemplateRuleMatch(ChatTemplateMatchKind Kind, string? RepairedTemplate = null);

/// <summary>Rules only transform a recognized structure; file management and validation are shared.</summary>
public interface IChatTemplateCompatibilityRule
{
    string Id { get; }
    int Version { get; }
    IReadOnlyList<string> ToolMarkers { get; }
    ChatTemplateRuleMatch Match(string template);
}

public sealed record ChatTemplateAnalysis(
    ChatTemplateMatchKind Kind, string? RuleId = null, int RuleVersion = 0, string? RepairedTemplate = null,
    IReadOnlyList<string>? ToolMarkers = null);

public static class ChatTemplateRules
{
    // Add a rule here and provide structural and native-rendering fixtures for that family.
    private static readonly IChatTemplateCompatibilityRule[] Registered =
        [new QwenStrictSystemRule(), new UnslothMergedSystemRule()];

    public static string VersionSignature => string.Join("|", Registered.Select(rule => $"{rule.Id}:{rule.Version}"));

    public static ChatTemplateAnalysis Analyze(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 1024 * 1024)
            return new(ChatTemplateMatchKind.NotMatched);
        template = template.Replace("\r\n", "\n", StringComparison.Ordinal);
        (IChatTemplateCompatibilityRule Rule, ChatTemplateRuleMatch Match)[] matches;
        try
        {
            matches = Registered.Select(rule => (Rule: rule, Match: rule.Match(template)))
                .Where(item => item.Match.Kind != ChatTemplateMatchKind.NotMatched).ToArray();
        }
        catch (RegexMatchTimeoutException) { return new(ChatTemplateMatchKind.Ambiguous); }
        if (matches.Length == 0) return new(ChatTemplateMatchKind.NotMatched);
        if (matches.Length != 1 || matches[0].Match.Kind == ChatTemplateMatchKind.Ambiguous)
            return new(ChatTemplateMatchKind.Ambiguous);
        var selected = matches[0];
        return new(ChatTemplateMatchKind.Repairable, selected.Rule.Id, selected.Rule.Version, selected.Match.RepairedTemplate, selected.Rule.ToolMarkers);
    }
}

internal static class TemplateRulePatterns
{
    internal const string SystemRole = "message\\.role[ \\t]*==[ \\t]*[\"']system[\"']";
    internal const string DeveloperRole = "message\\.role[ \\t]*==[ \\t]*[\"']developer[\"']";
    internal const string Error = "\\{\\{-[ \\t]*raise_exception\\([\"']System message must be at the beginning\\.[\"']\\)[ \\t]*\\}\\}";
    internal static Regex Create(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static readonly Regex ErrorCount = Create(Error);
    internal const string RenderSystem = "{{- '<|im_start|>system\\n' + content + '<|im_end|>\\n' }}";
}

internal sealed class QwenStrictSystemRule : IChatTemplateCompatibilityRule
{
    public string Id => "qwen-strict-system";
    public int Version => 2;
    public IReadOnlyList<string> ToolMarkers => ["<tool_call>", "<tool_response>"];
    private static readonly Regex Guard = TemplateRulePatterns.Create(
        "(?<branch>\\{%-[ \\t]*if[ \\t]+" + TemplateRulePatterns.SystemRole + "[ \\t]*%\\})[ \\t]*\\n"
        + "(?<indent>[ \\t]*)\\{%-[ \\t]*if[ \\t]+not[ \\t]+loop\\.first[ \\t]*%\\}[ \\t]*\\n"
        + "[ \\t]*" + TemplateRulePatterns.Error + "[ \\t]*\\n"
        + "[ \\t]*\\{%-[ \\t]*endif[ \\t]*%\\}");
    private static readonly Regex FirstSystem = TemplateRulePatterns.Create("messages\\[0\\]\\.role[ \\t]*==[ \\t]*[\"']system[\"']");

    public ChatTemplateRuleMatch Match(string template)
    {
        var matches = Guard.Matches(template);
        if (matches.Count == 0 || !template.Contains("<|im_start|>", StringComparison.Ordinal)) return new(ChatTemplateMatchKind.NotMatched);
        if (matches.Count != 1 || TemplateRulePatterns.ErrorCount.Matches(template).Count != 1)
            return new(ChatTemplateMatchKind.Ambiguous);
        var repaired = Guard.Replace(template, match =>
            "{%- if message.role == 'system' or message.role == 'developer' %}\n"
            + match.Groups["indent"].Value + "{%- if not loop.first %}\n"
            + match.Groups["indent"].Value + "    " + TemplateRulePatterns.RenderSystem + "\n"
            + match.Groups["indent"].Value + "{%- endif %}");
        repaired = FirstSystem.Replace(repaired, "(messages[0].role == 'system' or messages[0].role == 'developer')");
        return new(ChatTemplateMatchKind.Repairable, repaired);
    }
}

internal sealed class UnslothMergedSystemRule : IChatTemplateCompatibilityRule
{
    public string Id => "unsloth-merged-system";
    public int Version => 1;
    public IReadOnlyList<string> ToolMarkers => ["<tool_call>", "<tool_response>"];
    private static readonly Regex Guard = TemplateRulePatterns.Create(
        "\\{%-[ \\t]*if[ \\t]+" + TemplateRulePatterns.SystemRole + "[ \\t]+or[ \\t]+"
        + TemplateRulePatterns.DeveloperRole + "[ \\t]*%\\}[ \\t]*\\n(?<indent>[ \\t]*)" + TemplateRulePatterns.Error);

    public ChatTemplateRuleMatch Match(string template)
    {
        var matches = Guard.Matches(template);
        if (matches.Count == 0 || !template.Contains("<|im_start|>", StringComparison.Ordinal)) return new(ChatTemplateMatchKind.NotMatched);
        // This rule depends on the specific leading-message merge and loop boundary.
        if (matches.Count != 1 || TemplateRulePatterns.ErrorCount.Matches(template).Count != 1
            || !template.Contains("sysns.count == loop.index0", StringComparison.Ordinal)
            || !template.Contains("set num_sys = sysns.count", StringComparison.Ordinal)
            || !template.Contains("if loop.index0 >= num_sys", StringComparison.Ordinal)
            || !template.Contains("set content = render_content(message.content, true)|trim", StringComparison.Ordinal))
            return new(ChatTemplateMatchKind.Ambiguous);
        var repaired = Guard.Replace(template, match =>
            "{%- if message.role == 'system' or message.role == 'developer' %}\n"
            + match.Groups["indent"].Value + TemplateRulePatterns.RenderSystem);
        return new(ChatTemplateMatchKind.Repairable, repaired);
    }
}
