using System.Text.Json.Serialization;

namespace Launcher.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<ProviderMode>))]
public enum ProviderMode
{
    OpenAI,
    Local,
}

