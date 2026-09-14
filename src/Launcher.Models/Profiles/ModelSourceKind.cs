using System.Text.Json.Serialization;

namespace Launcher.Models.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter<ModelSourceKind>))]
public enum ModelSourceKind
{
    LocalFile,
    LlamaCache,
}
