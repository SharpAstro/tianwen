using System.Text.Json;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Astrometry.Comets;

/// <summary>
/// Source-generated (reflection-free, AOT-safe) JSON context for the SBDB query response and the local
/// comet cache. <see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> is required so a comet
/// with no photometric model round-trips its NaN M1/K1 through the cache as <c>"NaN"</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SbdbQueryResponse))]
[JsonSerializable(typeof(CometCacheFile))]
internal partial class SbdbJsonContext : JsonSerializerContext
{
}
