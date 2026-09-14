using Tomlyn;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Launcher.ChatGPT.Configuration;

/// <summary>
/// Lossless editor for the small set of root TOML assignments owned by the launcher.
/// Tomlyn supplies syntax-aware source spans and validates the complete document. The
/// editor only replaces those spans, so comments, ordering, unknown fields and tables
/// outside the managed assignments remain byte-for-byte unchanged.
/// </summary>
internal sealed class TomlRootDocument
{
    private const string SourceName = "ChatGPT config.toml";

    private readonly string _text;
    private readonly DocumentSyntax _syntax;

    public TomlRootDocument(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _syntax = ParseValidated(_text);
    }

    public string? GetAssignment(string key)
    {
        var assignment = FindSingleAssignment(key);
        return assignment is null
            ? null
            : _text.Substring(assignment.Span.Offset, assignment.Span.Length).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Returns true when the requested logical key already exists, is an ancestor of
    /// an existing key/table, or is sealed inside an inline-table ancestor. This is
    /// intentionally broader than <see cref="GetAssignment"/> and is used to prevent
    /// the launcher's fixed provider ID from colliding with user configuration.
    /// </summary>
    public bool ContainsDefinition(string key)
    {
        var target = ParseManagedKey(key);

        foreach (var assignment in _syntax.KeyValues)
        {
            var path = GetKeyPath(assignment.Key);
            if (PathsOverlapAsDefinition(path, target))
            {
                return true;
            }
        }

        foreach (var table in _syntax.Tables)
        {
            var tablePath = GetKeyPath(table.Name);
            if (IsPrefix(target, tablePath))
            {
                return true;
            }

            foreach (var assignment in table.Items)
            {
                var itemPath = tablePath.Concat(GetKeyPath(assignment.Key)).ToArray();
                if (PathsOverlapAsDefinition(itemPath, target))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public TomlRootDocument SetString(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var escapedValue = System.Text.Json.JsonSerializer.Serialize(value);
        return SetRawAssignment(key, $"{key} = {escapedValue}");
    }

    public TomlRootDocument SetRawAssignment(string key, string? assignment)
    {
        var targetPath = ParseManagedKey(key);
        var normalizedAssignment = assignment?.TrimEnd('\r', '\n');
        if (normalizedAssignment is not null)
        {
            ValidateReplacementAssignment(targetPath, normalizedAssignment);
        }

        var existing = FindSingleAssignment(key);
        var lineEnding = DetectLineEnding();

        if (existing is not null)
        {
            var existingText = _text.Substring(existing.Span.Offset, existing.Span.Length);
            var existingEnding = existingText.EndsWith("\r\n", StringComparison.Ordinal)
                ? "\r\n"
                : existingText.EndsWith("\n", StringComparison.Ordinal) ? "\n" : string.Empty;
            var replacement = normalizedAssignment is null
                ? string.Empty
                : normalizedAssignment + existingEnding;
            return new TomlRootDocument(
                _text[..existing.Span.Offset]
                + replacement
                + _text[(existing.Span.Offset + existing.Span.Length)..]);
        }

        if (normalizedAssignment is null)
        {
            return this;
        }

        var rootEnd = _syntax.Tables.ChildrenCount > 0
            ? (_syntax.Tables.GetChild(0)
                ?? throw new InvalidDataException("ChatGPT config.toml 包含缺失的 TOML 表。")).Span.Offset
            : _text.Length;
        var root = _text[..rootEnd];
        var suffix = _text[rootEnd..];

        if (root.Length > 0 && !root.EndsWith("\n", StringComparison.Ordinal))
        {
            root += lineEnding;
        }

        return new TomlRootDocument(root + normalizedAssignment + lineEnding + suffix);
    }

    public override string ToString() => _text;

    private KeyValueSyntax? FindSingleAssignment(string key)
    {
        var target = ParseManagedKey(key);
        var matches = _syntax.KeyValues
            .Where(assignment => PathsEqual(GetKeyPath(assignment.Key), target))
            .ToArray();

        if (matches.Length > 1)
        {
            // Tomlyn's semantic validation should reject this first. Keep the guard
            // so this editor remains fail-closed if parser behavior changes.
            throw new InvalidDataException($"顶层配置 {key} 出现多次，拒绝自动修改。");
        }

        return matches.Length == 0 ? null : matches[0];
    }

    private static DocumentSyntax ParseValidated(string text)
    {
        try
        {
            return SyntaxParser.ParseStrict(text, SourceName, validate: true);
        }
        catch (TomlException)
        {
            // Do not include parser diagnostics: a malformed value may contain a
            // credential. The caller only needs to know that no write is allowed.
            throw new InvalidDataException("ChatGPT config.toml 不是有效的 TOML；为保护官方配置，已停止切换。");
        }
    }

    private static void ValidateReplacementAssignment(string[] targetPath, string assignment)
    {
        DocumentSyntax replacement;
        try
        {
            replacement = SyntaxParser.ParseStrict(assignment, "受管配置", validate: true);
        }
        catch (TomlException)
        {
            throw new InvalidDataException("启动器生成了无效的 TOML 赋值，已在写入前停止切换。");
        }

        var replacementKeyValue = replacement.KeyValues.ChildrenCount == 1
            ? replacement.KeyValues.GetChild(0)
            : null;
        if (replacement.Tables.ChildrenCount != 0
            || replacementKeyValue is null
            || !PathsEqual(GetKeyPath(replacementKeyValue.Key), targetPath))
        {
            throw new InvalidDataException($"赋值不属于受管配置 {string.Join('.', targetPath)}。");
        }
    }

    private string DetectLineEnding() => _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string[] ParseManagedKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Split('.').Any(segment =>
                segment.Length == 0
                || segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
        {
            throw new ArgumentException("TOML 顶层键包含不支持的字符。", nameof(key));
        }

        return key.Split('.');
    }

    private static string[] GetKeyPath(KeySyntax? key)
    {
        if (key is null)
        {
            throw new InvalidDataException("ChatGPT config.toml 包含缺失的 TOML 键。");
        }

        var path = new string[key.DotKeys.ChildrenCount + 1];
        path[0] = GetKeyPart(key.Key);
        for (var index = 0; index < key.DotKeys.ChildrenCount; index++)
        {
            var item = key.DotKeys.GetChild(index)
                ?? throw new InvalidDataException("ChatGPT config.toml 包含缺失的 TOML 键。");
            path[index + 1] = GetKeyPart(item.Key);
        }

        return path;
    }

    private static string GetKeyPart(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bareKey => bareKey.Key?.Text
            ?? throw new InvalidDataException("ChatGPT config.toml 包含缺失的 TOML 键。"),
        StringValueSyntax stringKey => stringKey.Value
            ?? throw new InvalidDataException("ChatGPT config.toml 包含缺失的 TOML 键。"),
        _ => throw new InvalidDataException("ChatGPT config.toml 包含无法识别的 TOML 键。"),
    };

    private static bool PathsOverlapAsDefinition(IReadOnlyList<string> existing, IReadOnlyList<string> target) =>
        IsPrefix(existing, target) || IsPrefix(target, existing);

    private static bool PathsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && IsPrefix(left, right);

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> path)
    {
        if (prefix.Count > path.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (!string.Equals(prefix[index], path[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
