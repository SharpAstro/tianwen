using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.UI.Abstractions;

/// <summary>
/// What a click at the pointer's current position would select: the object, where it is on the
/// sky, and how big the hit region that claimed it was. Produced by
/// <see cref="SkyMapSearchActions.ResolveHoverAtScreenPoint"/> and drawn by
/// <c>SkyMapTab.DrawHoverSpot</c>.
/// </summary>
/// <remarks>
/// Position is carried as RA/Dec rather than screen pixels so the wash follows the object for as
/// long as the target lives -- a repaint at a different DPI or a one-pixel scroll does not need a
/// re-resolve to land in the right place. The RADIUS is a screen quantity and is therefore a
/// snapshot: it is the hit radius at the instant the pointer was tested, and a zoom that happens
/// without a pointer move would make it stale, which is exactly why
/// <see cref="SkyMapState.HoverTarget"/> is cleared on a view change instead of being redrawn.
/// <para>A comet or planet resolves here too: those are ephemeris positions at the viewing
/// instant, not catalogue coordinates, which is the other reason this carries RA/Dec of its own
/// rather than an index for the caller to look up.</para>
/// </remarks>
/// <param name="Index">The object's catalogue index. A comet's index is its designation's, which
/// is not in the object DB -- see <see cref="IsEphemeris"/>.</param>
/// <param name="RA">Right ascension in hours, at the viewing instant for a moving body.</param>
/// <param name="Dec">Declination in degrees, at the viewing instant for a moving body.</param>
/// <param name="HitRadiusPx">The screen radius that claimed the pointer, before clamping to
/// <see cref="Overlays.OverlayEngine.HoverSpotMinRadiusPx"/> /
/// <see cref="Overlays.OverlayEngine.HoverSpotMaxRadiusPx"/>.</param>
/// <param name="IsEphemeris">True for a planet, the Sun, the Moon or a comet: its position is
/// computed for the viewing instant, so it is only valid while the clock is not scrubbed.</param>
/// <param name="ObjType">The catalogue object's type, which decides whether <paramref name="Shape"/>
/// is drawn as an ellipse (a star carrying a stray shape still washes as a spot). Meaningless for
/// an ephemeris body, which carries no shape.</param>
/// <param name="Shape">The catalogue shape, when the object has one, so the wash can take the
/// object's own ellipse rather than a spot of the hit radius. Looked up ONCE, at resolve time: the
/// wash paints every frame the target lives.</param>
public readonly record struct SkyMapHoverTarget(
    CatalogIndex Index,
    double RA,
    double Dec,
    float HitRadiusPx,
    bool IsEphemeris,
    ObjectType ObjType = default,
    CelestialObjectShape? Shape = null);
