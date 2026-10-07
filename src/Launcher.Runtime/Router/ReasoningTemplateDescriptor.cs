using System.Text.RegularExpressions;
using Launcher.Core.Configuration;

namespace Launcher.Runtime.Router;

/// <summary>Reads finite native declarations and simple bindings; rendering remains authoritative.</summary>
public sealed record ReasoningTemplateDescriptor(
    IReadOnlyList<string> Levels, string? DefaultLevel, IReadOnlyDictionary<string, string> Aliases,
    bool HasThinkingControl, bool? DefaultThinking)
{
    private const string Identifier = @"[A-Za-z_]\w*";
    private const string Value = @"[A-Za-z0-9_.:-]{1,64}";
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    public static ReasoningTemplateDescriptor Read(string template)
    {
        if (template.Length > 1024 * 1024) throw new ArgumentException("Template is too large.", nameof(template));
        template = Regex.Replace(template, @"\{#.*?#\}", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        var statements = string.Join("\n", Matches(template, @"\{%[-+]?\s*(?<statement>[\s\S]*?)\s*[-+]?%\}")
            .Select(match => match.Groups["statement"].Value));
        var variables = new HashSet<string>(StringComparer.Ordinal) { "reasoning_effort" };
        var bindings = Matches(statements, @"\bset\s+(?<target>" + Identifier + @")\s*=\s*(?<source>" + Identifier + @")(?=\s*(?:\||\bor\b|$))");
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var changed = false;
            foreach (Match binding in bindings)
                if (variables.Contains(binding.Groups["source"].Value)) changed |= variables.Add(binding.Groups["target"].Value);
            if (!changed) break;
        }
        var declarations = new List<string[]>();
        foreach (Match guard in Matches(statements, @"\b(?<variable>" + Identifier + @")\s+not\s+in\s*[\(\[](?<levels>[^\)\]]+)[\)\]]"))
        {
            if (!variables.Contains(guard.Groups["variable"].Value)) continue;
            var raw = guard.Groups["levels"].Value;
            var values = Matches(raw, "['\"](?<value>" + Value + ")['\"]");
            var remainder = Regex.Replace(raw, "['\"]" + Value + "['\"]|[\\s,]", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (remainder.Length == 0 && values.Count is > 0 and <= 32)
                declarations.Add(values.Select(value => value.Groups["value"].Value).Where(ReasoningLevelValue.IsValid).Distinct(StringComparer.Ordinal).ToArray());
        }
        var levels = declarations.Count > 0 && declarations.All(list => list.ToHashSet(StringComparer.Ordinal).SetEquals(declarations[0]))
            ? declarations[0].ToList() : [];
        var defaults = Matches(template, "reasoning_effort\\s*\\|\\s*default\\(['\"](?<value>" + Value + ")['\"]\\)");
        var defaultLevel = defaults.Count == 1 ? defaults[0].Groups["value"].Value : null;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match alias in Matches(template,
                     "\\{%[-+]?\\s*if\\s+(?<var>" + Identifier + ")\\s*==\\s*['\"](?<from>" + Value + ")['\"]\\s*[-+]?%\\}\\s*\\{%[-+]?\\s*set\\s+\\k<var>\\s*=\\s*['\"](?<to>" + Value + ")['\"]\\s*[-+]?%\\}\\s*\\{%[-+]?\\s*endif\\s*[-+]?%\\}"))
        {
            var from = alias.Groups["from"].Value;
            var to = alias.Groups["to"].Value;
            if (variables.Contains(alias.Groups["var"].Value) && levels.Contains(to) && from != to)
            {
                aliases[from] = to; levels.Remove(from);
                if (defaultLevel == from) defaultLevel = to;
            }
        }
        if (!levels.Contains(defaultLevel, StringComparer.Ordinal)) defaultLevel = null;
        var thinking = Matches(statements, @"\benable_thinking\b").Count > 0;
        bool? defaultThinking = Matches(template, @"enable_thinking\s+is\s+undefined\s+or\s+enable_thinking\s+is\s+true").Count > 0 ? true : null;
        return new(levels, defaultLevel, aliases, thinking, defaultThinking);
    }
}
