using System.Text.Json.Serialization;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Imaging;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    IgnoreReadOnlyFields = false,
    IgnoreReadOnlyProperties = false,
    IncludeFields = true,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
)]
[JsonSerializable(typeof(ImageMeta))]
internal partial class ImageJsonSerializerContext : JsonSerializerContext
{

}