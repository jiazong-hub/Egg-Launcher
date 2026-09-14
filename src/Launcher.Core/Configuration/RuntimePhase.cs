using System.Text.Json.Serialization;

namespace Launcher.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimePhase>))]
public enum RuntimePhase
{
    Stopped,
    Starting,
    Running,
    Stopping,
    SwitchingToOpenAI,
    SwitchingToLocal,
    Recovering,
    Error,
}

