using System.Text;
using Launcher.ChatGPT.Catalog;
using Launcher.Models.Profiles;
using Launcher.Scripts.RouterPreset;

namespace Launcher.Orchestration.Models;

public static class LocalModelConfigurationWriter
{
    public static async Task WriteAsync(ModelProfile profile, string runtimeRoot, string presetPath, string catalogPath, CancellationToken cancellationToken = default)
    {
        await LocalModelCatalogBuilder.WriteAtomicallyAsync(
            catalogPath,
            new LocalModelCatalogOptions
            {
                Slug = profile.Alias,
                DisplayName = profile.DisplayName,
                ContextWindow = profile.ContextSize,
                ReverseReasoningLevelDisplayOrder = profile.ReverseReasoningLevelDisplayOrder,
                SupportsImageInput = profile.VisionEnabled
                                     && profile.VisionCapabilityStatus == VisionCapabilityStatus.Verified,
                SupportedReasoningLevels = (profile.ThinkingEnabled ?? profile.DefaultThinkingEnabled) != false && profile.ReasoningResponsesVerified && profile.ReasoningClientCompatible == true && profile.ExposeReasoningEffortInChatGpt
                    && profile.ReasoningCapabilityStatus == ReasoningCapabilityStatus.Verified
                        ? profile.SupportedReasoningLevels.Where(CodexReasoningLevels.IsRecognized).ToArray()
                        : Array.Empty<string>(),
                DefaultReasoningLevel = ReasoningDefaultSelection.ForCodex(profile),
            },
            cancellationToken).ConfigureAwait(false);
        await WriteTextAtomicallyAsync(
            presetPath,
            RouterPresetGenerator.Generate(profile, runtimeRoot, loadOnStartup: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("生成文件必须位于一个目录中。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original transaction result; generated temp files are never consumed.
            }
        }
    }

}
