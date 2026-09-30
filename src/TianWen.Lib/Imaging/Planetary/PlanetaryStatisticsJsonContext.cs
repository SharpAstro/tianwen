using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>A capture's statistics as a file (<see cref="PlanetaryCaptureStatistics.SaveAsync"/>), with what they were measured on.</summary>
/// <param name="Version">The measurement's version (<see cref="PlanetaryCaptureStatistics.FileVersion"/>).</param>
/// <param name="Key">The capture, its frames and the options measured with: a file is read back only for the same.</param>
/// <param name="Statistics">The statistics.</param>
public sealed record CaptureStatisticsFile(int Version, string Key, CaptureStatistics Statistics);

/// <summary>
/// Source-generated (AOT-safe) JSON for a capture's statistics. A statistic that could not be measured is NaN, which the file
/// keeps as a named literal.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(CaptureStatisticsFile))]
internal partial class PlanetaryStatisticsJsonContext : JsonSerializerContext
{
}
