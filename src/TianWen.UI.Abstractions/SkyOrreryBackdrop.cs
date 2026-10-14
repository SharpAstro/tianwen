using System.Numerics;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// A view of the sky map's own stars and Milky Way that is NOT the sky map's (docs/plans/atlas-planet-detail.md, A5, "The backdrop"):
/// the orrery's backdrop at infinity. Set on <see cref="SkyMapState.Backdrop"/> for the length of one backdrop draw, it is what every
/// host's sky rendering reads in place of the map's own view: <see cref="SkyMapUbo"/> writes its matrix, focal length, tint and
/// brightness and selects the gnomonic projection; the pipelines cull by its cone and magnitude limit; the hosts skip the twilight
/// ground and the lines. One renderer, a second view of it.
/// </summary>
/// <param name="View">J2000 equatorial to camera space (camera looking along -Z, x right, y up), as the map's view matrix.</param>
/// <param name="PixelsPerRadian">The gnomonic focal length in pixels: a direction <c>theta</c> off the axis lands <c>f tan theta</c> out.</param>
/// <param name="Axis">The look direction, J2000 unit vector: the star cull's cone axis.</param>
/// <param name="RadiusDeg">The cull cone's radius, degrees: the view's full diagonal, generous as the map's own.</param>
/// <param name="FieldOfViewDeg">The vertical field of view, degrees: what the magnitude limit and the star sizes scale with.</param>
/// <param name="MilkyWayAlpha">The Milky Way's opacity, independent of the Sun (the orrery's view is not from the ground).</param>
/// <param name="Brightness">What every star's and the Milky Way's alpha is multiplied by: the dimming that keeps orbits and labels readable.</param>
/// <param name="Tint">Night's colour, or null: a star and the Milky Way are drawn as their luminance in it, so Night spends no blue.</param>
public readonly record struct SkyOrreryBackdrop(
    Matrix4x4 View,
    double PixelsPerRadian,
    (float X, float Y, float Z) Axis,
    double RadiusDeg,
    double FieldOfViewDeg,
    float MilkyWayAlpha,
    float Brightness,
    RGBAColor32? Tint);
