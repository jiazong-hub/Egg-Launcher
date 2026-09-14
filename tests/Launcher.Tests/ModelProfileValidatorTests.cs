using Launcher.Models.Profiles;

namespace Launcher.Tests;

public sealed class ModelProfileValidatorTests
{
    [Fact]
    public void Validate_WhenRelativePathEscapesRuntime_ReturnsError()
    {
        var profile = ValidProfile() with { ModelRelativePath = @"..\outside.gguf" };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("逃逸", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenExtraArgumentOverridesManagedValue_ReturnsError()
    {
        var profile = ValidProfile() with
        {
            ExtraArguments = new Dictionary<string, string?> { ["ctx-size"] = "8192" },
        };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("ctx-size", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenIdleSleepIsZero_ReturnsError()
    {
        var profile = ValidProfile() with { IdleSleepSeconds = 0 };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("空闲休眠", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenCompactionReserveIsNotSmallerThanContext_ReturnsError()
    {
        var profile = ValidProfile() with
        {
            ContextSize = 8_192,
            CompactionSafetyReserve = 8_192,
        };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("压缩安全余量", StringComparison.Ordinal)
            && error.Contains("小于 Context", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenCompactionReserveIsValid_ExposesExpectedCodexThreshold()
    {
        var profile = ValidProfile() with
        {
            ContextSize = 32_768,
            CompactionSafetyReserve = 8_192,
        };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Empty(errors);
        Assert.Equal(24_576, profile.AutoCompactTokenLimit);
    }

    [Fact]
    public void Validate_WhenChatTemplateEscapesRuntime_ReturnsError()
    {
        var profile = ValidProfile() with { ChatTemplateRelativePath = @"..\outside.jinja" };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("Chat Template", StringComparison.Ordinal)
            && error.Contains("逃逸", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenChatTemplateIsSetWithoutJinja_ReturnsError()
    {
        var profile = ValidProfile() with
        {
            Jinja = false,
            ChatTemplateRelativePath = @"scripts\templates\coder.jinja",
        };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("必须启用 Jinja", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenPathsContainPresetInjectionNewlines_ReturnsErrorsWithoutThrowing()
    {
        var profile = ValidProfile() with
        {
            ModelRelativePath = "models\\coder.gguf\nmodels-max = 99",
            ChatTemplateRelativePath = "scripts\\template.jinja\nport = 0",
        };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(errors, error => error.Contains("模型路径", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Chat Template 路径", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhenModelSpecificDefaultIsInvalid_ReturnsScopedError()
    {
        var profile = ValidProfile();
        profile = profile with
        {
            DefaultParameters = ModelParameterDefaults.FromProfile(profile) with
            {
                IdleSleepSeconds = 0,
            },
        };

        var errors = ModelProfileValidator.Validate(profile, @"D:\llama.cpp");

        Assert.Contains(
            errors,
            error => error.Contains("此模型的默认参数无效", StringComparison.Ordinal)
                && error.Contains("空闲休眠", StringComparison.Ordinal));
    }

    private static ModelProfile ValidProfile() => new()
    {
        Id = "coder-q4",
        DisplayName = "Coder Q4",
        ModelRelativePath = @"models\coder-Q4_K_M.gguf",
        Alias = "coder-q4",
    };
}
