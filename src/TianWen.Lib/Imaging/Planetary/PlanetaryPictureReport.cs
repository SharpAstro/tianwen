using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// What <c>planetary compare --json</c> writes (#1367): every master's pictures, a channel at a time, beside the limb each was read on. A
/// number that could not be read is NaN, which the file keeps as a named literal.
/// </summary>
/// <param name="Planet">The planet, as the catalogue names it.</param>
/// <param name="Utc">The instant the masters show it at, ISO 8601.</param>
/// <param name="ApertureMm">The telescope's aperture, or NaN when none was given (and so no glow expected, no edge and no cutoff).</param>
/// <param name="Obstruction">Its central obstruction over its aperture.</param>
public sealed record PlanetaryPictureReport(string Planet, string Utc, double ApertureMm, double Obstruction, ImmutableArray<PlanetaryMasterPictures> Masters);

/// <summary>One master's pictures (<see cref="PlanetaryPictureReport"/>).</summary>
/// <param name="OwnLimb">Whether its limb was its own fit, or the first master's (one the same size).</param>
/// <param name="NorthDeg">The planet's north on the frame, degrees, as the limb fit holds it.</param>
public sealed record PlanetaryMasterPictures(string Label, string Path, int Width, int Height, int Channels, bool OwnLimb, double CenterX, double CenterY,
    double RadiusPx, double NorthDeg, ImmutableArray<PlanetaryChannelPicture> Pictures);

/// <summary>One channel's picture (<see cref="PlanetaryPictureReport"/>).</summary>
/// <param name="SkyLevel">The sky's level in the plane's own units, which the picture's disk units take as 0.</param>
/// <param name="DiskScale">The disk's mean above the sky inside 0.8 radii in the plane's own units, which the disk units take as 1: a
/// value v in the picture is <c>SkyLevel + v * DiskScale</c> in the file (<see cref="PlanetaryMetrics.NormalisationLevels"/>).</param>
/// <param name="CutoffCyclesPerPixel">The pupil's cutoff at <paramref name="WavelengthNm"/>, or NaN without a telescope.</param>
/// <param name="EdgeTransfer">The limb's edge over the pupil's own transfer (<see cref="PlanetaryFinestBand.MasterEdge"/>) at
/// <see cref="EdgeFrequencies"/>, or empty without a telescope.</param>
public sealed record PlanetaryChannelPicture(int Channel, double WavelengthNm, double SkyLevel, double DiskScale, double CutoffCyclesPerPixel,
    ImmutableArray<double> EdgeTransfer, PlanetaryPicture Picture)
{
    /// <summary>The frequencies the edge is read at, cycles a pixel: where it is good (0.1 to 0.3, R8 follow-up 3).</summary>
    public static readonly ImmutableArray<double> EdgeFrequencies = [0.1, 0.2, 0.3];
}

/// <summary>Source-generated (AOT-safe) JSON for <see cref="PlanetaryPictureReport"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    WriteIndented = true)]
[JsonSerializable(typeof(PlanetaryPictureReport))]
public partial class PlanetaryPictureJsonContext : JsonSerializerContext
{
}
