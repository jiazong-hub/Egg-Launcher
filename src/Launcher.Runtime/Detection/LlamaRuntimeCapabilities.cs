namespace Launcher.Runtime.Detection;

public sealed record LlamaRuntimeCapabilities(
    bool SupportsDeviceListing,
    bool SupportsRouter,
    bool SupportsIdleSleep,
    bool SupportsChatTemplateFile,
    bool SupportsMetrics)
{
    public static LlamaRuntimeCapabilities FromHelpText(string? helpText)
    {
        helpText ??= string.Empty;

        var modelsDirectory = ContainsOption(helpText, "--models-dir");
        var modelsPreset = ContainsOption(helpText, "--models-preset");
        var modelsMaximum = ContainsOption(helpText, "--models-max");
        var modelsAutoload = ContainsOption(helpText, "--models-autoload");

        return new LlamaRuntimeCapabilities(
            SupportsDeviceListing: ContainsOption(helpText, "--list-devices"),
            SupportsRouter: modelsDirectory && modelsPreset && modelsMaximum && modelsAutoload,
            SupportsIdleSleep: ContainsOption(helpText, "--sleep-idle-seconds"),
            SupportsChatTemplateFile: ContainsOption(helpText, "--chat-template-file"),
            SupportsMetrics: ContainsOption(helpText, "--metrics"));
    }

    private static bool ContainsOption(string source, string option) =>
        source.Contains(option, StringComparison.OrdinalIgnoreCase);
}
