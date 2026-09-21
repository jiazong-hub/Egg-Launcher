using System.Text.Json.Serialization;

namespace Launcher.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
public enum AppLanguage
{
    Chinese = 0,
    English = 1,
    System = 2,
}
