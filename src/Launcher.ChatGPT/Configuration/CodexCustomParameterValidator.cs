using Launcher.Models.Profiles;

namespace Launcher.ChatGPT.Configuration;

public static class CodexCustomParameterValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<CustomParameter> rows)
    {
        var errors = CustomParameterRules.Validate(new CustomParameterSettings { Codex = rows }).ToList();
        if (errors.Count > 0) return errors;
        for (var index = 0; index < rows.Count; index++)
        {
            try { TomlRootDocument.ValidateCustomValue(rows[index].Name, rows[index].Value); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                // Parser diagnostics may contain credentials. Report row/name, never the value.
                errors.Add($"Codex #{index + 1} ({rows[index].Name})：请输入单个合法 TOML 值 / Enter one valid TOML value.");
            }
        }
        return errors;
    }
}
