namespace Launcher.ChatGPT.Configuration;

internal static class ShellEnvironmentFilterPolicy
{
    public static void EnsureExplicitValueSurvives(TomlRootDocument document, string variable)
    {
        const string table = "shell_environment_policy";
        document.RequireExplicitTableForm(table);
        var include = document.GetTableStringArrayValue(table, "include_only");
        var exclude = document.GetTableStringArrayValue(table, "exclude");
        var filters = document.GetTableStringMap(table, "filters");
        if (filters is not null && (include is not null || exclude is not null))
            throw new ChatGptConfigConflictException("Codex 命令环境同时使用 filters 与旧版 include_only/exclude；无法确认过滤规则，请先统一配置格式。");
        if (filters is not null)
        {
            if (filters.Any(pair => pair.Value is not ("include" or "exclude")))
                throw new ChatGptConfigConflictException("Codex 命令环境 filters 包含无法识别的值，仅支持 include/exclude。");
            include = filters.Where(pair => pair.Value == "include").Select(pair => pair.Key).ToArray();
        }
        // Codex applies exclusions before explicit set values, and the allowlist
        // after them. Exclusions cannot remove a value supplied by this launcher.
        if (include is { Count: > 0 } && !include.Any(pattern => Matches(pattern, variable)))
            throw new ChatGptConfigConflictException($"Codex 命令环境白名单未包含 {variable}，会移除指定的 Gradle 用户目录。请将 {variable} 加入 include_only 或 filters 的 include 规则；启动器不会自动扩大白名单。");
    }

    private static bool Matches(string pattern, string name)
    {
        // EnvironmentVariablePattern uses case-insensitive '*' and '?' wildcards.
        var previous = new bool[name.Length + 1];
        previous[0] = true;
        foreach (var character in pattern)
        {
            var current = new bool[name.Length + 1];
            current[0] = character == '*' && previous[0];
            for (var index = 1; index <= name.Length; index++)
                current[index] = character == '*'
                    ? previous[index] || current[index - 1]
                    : previous[index - 1] && (character == '?' || char.ToUpperInvariant(character) == char.ToUpperInvariant(name[index - 1]));
            previous = current;
        }
        return previous[name.Length];
    }
}
