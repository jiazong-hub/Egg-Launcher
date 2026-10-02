using Launcher.ChatGPT.Configuration;

namespace Launcher.Tests;

public sealed class ShellEnvironmentFilterPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("[shell_environment_policy]\nexclude = ['*']\n")]
    [InlineData("[shell_environment_policy]\ninclude_only = []\n")]
    [InlineData("[shell_environment_policy]\ninclude_only = ['PATH', 'gradle_user_home']\n")]
    [InlineData("[shell_environment_policy]\ninclude_only = ['GRADLE_*']\n")]
    [InlineData("[shell_environment_policy]\ninclude_only = ['GRADLE_USER_HOM?']\n")]
    [InlineData("[shell_environment_policy]\nfilters = { 'GRADLE_*' = 'include', '*' = 'exclude' }\n")]
    [InlineData("[shell_environment_policy.filters]\n'gradle_*' = 'include'\n")]
    [InlineData("[shell_environment_policy.filters]\n'*' = 'exclude'\n")]
    public void AllowsExplicitVariableWhenRetained(string text)
        => ShellEnvironmentFilterPolicy.EnsureExplicitValueSurvives(new TomlRootDocument(text), "GRADLE_USER_HOME");

    [Theory]
    [InlineData("[shell_environment_policy]\ninclude_only = ['PATH']\n")]
    [InlineData("[shell_environment_policy]\nfilters = { 'PATH' = 'include' }\n")]
    [InlineData("[shell_environment_policy]\nexclude = []\nfilters = {}\n")]
    [InlineData("[shell_environment_policy]\nfilters = { '*' = 'unknown' }\n")]
    public void RejectsBlockingOrAmbiguousFiltersWithoutChangingDocument(string text)
    {
        var document = new TomlRootDocument(text);
        Assert.Throws<ChatGptConfigConflictException>(() =>
            ShellEnvironmentFilterPolicy.EnsureExplicitValueSurvives(document, "GRADLE_USER_HOME"));
        Assert.Equal(text, document.ToString());
    }

    [Theory]
    [InlineData("shell_environment_policy = { include_only = ['PATH'] }\n")]
    [InlineData("shell_environment_policy.include_only = ['PATH']\n")]
    [InlineData("[shell_environment_policy]\nfilters.PATH = 'include'\n")]
    public void RejectsUnsupportedForms(string text)
        => Assert.Throws<InvalidDataException>(() =>
            ShellEnvironmentFilterPolicy.EnsureExplicitValueSurvives(new TomlRootDocument(text), "GRADLE_USER_HOME"));
}
