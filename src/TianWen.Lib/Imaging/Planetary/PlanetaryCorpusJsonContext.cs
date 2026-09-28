using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Source-generated (AOT-safe) JSON for the planetary corpus's files (docs/plans/planetary-restoration.md, R0): the survey's
/// manifest, each crop's sidecar and each converted FITS video's. Enums are written by NAME: these are files a person reads, not the node's wire.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CorpusManifest))]
[JsonSerializable(typeof(CropSidecar))]
[JsonSerializable(typeof(FitsVideoSidecar))]
internal partial class PlanetaryCorpusJsonContext : JsonSerializerContext
{
}
