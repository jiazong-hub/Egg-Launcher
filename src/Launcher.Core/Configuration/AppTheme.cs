using System.Text.Json.Serialization;

namespace Launcher.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    Dark,
    Light,
}
