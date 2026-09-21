using System.Globalization;
using Launcher.Core.Configuration;

namespace Launcher.Tests;

public sealed class AppLanguagePreferenceResolverTests
{
    [Theory]
    [InlineData("zh-CN", AppLanguage.Chinese)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("zh-HK", AppLanguage.Chinese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("ja-JP", AppLanguage.English)]
    public void Resolve_WhenFollowingSystem_MapsSupportedLanguage(string cultureName, AppLanguage expected)
    {
        var actual = AppLanguagePreferenceResolver.Resolve(
            AppLanguage.System,
            CultureInfo.GetCultureInfo(cultureName));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(AppLanguage.Chinese)]
    [InlineData(AppLanguage.English)]
    public void Resolve_WhenLanguageIsExplicit_IgnoresSystemLanguage(AppLanguage preference)
    {
        var actual = AppLanguagePreferenceResolver.Resolve(
            preference,
            CultureInfo.GetCultureInfo("ja-JP"));

        Assert.Equal(preference, actual);
    }
}
