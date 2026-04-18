using System.Text.Json.Serialization;

namespace TianWen.Lib.Astrometry.Catalogs;

/// <summary>
/// Angular shape of a specific catalog object, identified by the sequential
/// catalog number (<see cref="Seq"/>) within its parent <see cref="Catalog"/>.
/// Loaded from per-catalog <c>*.shapes.json.lz</c> files whose source VizieR
/// tables carry size information the base Simbad pull lacks (Dobashi, Barnard,
/// LDN, Ced, ...). See <c>Get-VizierDarkNebulaShapes.ps1</c>.
/// <para>
/// <see cref="Maj"/> and <see cref="Min"/> are in arcminutes; <see cref="PA"/>
/// is the position angle in degrees measured from north through east. Objects
/// without a published ellipse are written as circular
/// (<c>Maj == Min</c>, <c>PA == 0</c>).
/// </para>
/// </summary>
internal record CatalogObjectShape(
    int Seq,
    double Maj,
    double Min,
    double PA
);

[JsonSerializable(typeof(CatalogObjectShape))]
internal partial class CatalogObjectShapeJsonSerializerContext : JsonSerializerContext
{
}
