using System.Text.Json.Serialization;

namespace Launcher.ChatGPT.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<ConfigTransactionStage>))]
public enum ConfigTransactionStage
{
    Prepared,
    LocalApplied,
    LocalUpdatePrepared,
    LocalUpdateApplied,
    OpenAiRestorePrepared,
    Restored,
    OfficialCompatibilityPrepared,
}
