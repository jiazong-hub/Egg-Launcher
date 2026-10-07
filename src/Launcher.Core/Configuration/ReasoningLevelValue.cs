namespace Launcher.Core.Configuration;

/// <summary>Bounds a native identifier without assigning a meaning or changing its case.</summary>
public static class ReasoningLevelValue
{
    public static bool IsValid(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');
}
