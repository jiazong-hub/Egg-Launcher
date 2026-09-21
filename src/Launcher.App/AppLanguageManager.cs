using System.Globalization;
using System.Windows;
using Launcher.Core.Configuration;

namespace Launcher.App;

internal static class AppLanguageManager
{
    private const string ChineseDictionary = "Localization/Strings.zh-CN.xaml";
    private const string EnglishDictionary = "Localization/Strings.en-US.xaml";
    private static readonly CultureInfo SystemUiCultureAtStartup = CultureInfo.CurrentUICulture;

    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Chinese;

    public static AppLanguage CurrentPreference { get; private set; } = AppLanguage.System;

    public static event EventHandler? LanguageChanged;

    public static void Apply(AppLanguage preference)
    {
        var language = AppLanguagePreferenceResolver.Resolve(preference, SystemUiCultureAtStartup);
        var resources = Application.Current.Resources.MergedDictionaries;
        var source = new Uri(
            language == AppLanguage.English ? EnglishDictionary : ChineseDictionary,
            UriKind.Relative);
        var replacement = new ResourceDictionary { Source = source };
        var existingIndex = -1;
        for (var index = 0; index < resources.Count; index++)
        {
            var original = resources[index].Source?.OriginalString;
            if (original?.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true)
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            resources[existingIndex] = replacement;
        }
        else
        {
            resources.Insert(0, replacement);
        }

        CurrentPreference = preference;
        CurrentLanguage = language;
        CultureInfo.CurrentUICulture = language == AppLanguage.English
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("zh-CN");
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Text(string key)
    {
        if (Application.Current.TryFindResource(key) is string value)
        {
            return value;
        }

        throw new InvalidOperationException($"Missing language resource: {key}");
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    public static bool IsEnglish => CurrentLanguage == AppLanguage.English;

    public static bool FollowsSystem => CurrentPreference == AppLanguage.System;

    public static string Choose(string chinese, string english) => IsEnglish ? english : chinese;
}
