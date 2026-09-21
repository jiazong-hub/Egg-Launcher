using System.Globalization;

namespace Launcher.Core.Configuration;

public static class AppLanguagePreferenceResolver
{
    public static AppLanguage Resolve(AppLanguage preference, CultureInfo systemUiCulture)
    {
        ArgumentNullException.ThrowIfNull(systemUiCulture);
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown language preference.");
        }

        if (preference != AppLanguage.System)
        {
            return preference;
        }

        return string.Equals(
            systemUiCulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Chinese
            : AppLanguage.English;
    }
}
