using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions;

/// <summary>
/// One built planet sprite (<see cref="PlanetDiscSprite.Build"/>): <see cref="Side"/> squared pixels of straight-alpha RGBA, the
/// globe's equatorial radius <see cref="RadiusPx"/> in them, and the key it was built for.
/// </summary>
public sealed record PlanetSprite(CatalogIndex Body, byte[] Rgba, int Side, double RadiusPx, PlanetSpriteKey Key, string Credit);

/// <summary>
/// What a sprite depends on, every continuous input BUCKETED (CLAUDE.md, "a quantized cache key must not derive its grid from a
/// continuous input"): the disc's size in steps of an eighth of an octave, and every angle (central meridian, the Sun's place on the
/// disc, the sub-observer latitude that tilts the pole and opens Saturn's rings, the turn on screen) in steps of the limb error,
/// about one radian over the radius, taken from the BUCKETED size. The palette generation and Night, the texture's version, and
/// whether the body is dimmed below the horizon.
/// </summary>
public readonly record struct PlanetSpriteKey(CatalogIndex Body, int Size, int CentralMeridian, int SunLongitude, int SunLatitude,
    int ObserverLatitude, int Turn, bool Mirrored, int PaletteGeneration, bool Night, int TextureVersion, bool Dimmed);

/// <summary>
/// The sky atlas's planet sprites (docs/plans/atlas-planet-detail.md, "A4: the textured disc"): one per body, rebuilt only when its
/// <see cref="PlanetSpriteKey"/> changes, and built OFF the render thread. The render thread asks <see cref="Get"/> each frame; a
/// changed key starts one build (a Task, the hand-off: no lock, no shared field written from the build) and the stale sprite is drawn
/// until it lands, whereupon <c>requestRedraw</c> asks for the frame that shows it. Render-thread only, apart from that Task.
/// </summary>
public sealed class PlanetSpriteCache(Action? requestRedraw = null)
{
    private const int SizeStepsPerOctave = 8;

    /// <summary>A body below the horizon is drawn at this opacity, as its label is (<c>SkyMapTab.DimmedIf</c>).</summary>
    public const float DimmedOpacity = 0.35f;

    private sealed class Entry
    {
        public PlanetSprite? Current;
        public Task<PlanetSprite>? Pending;
    }

    private readonly Dictionary<CatalogIndex, Entry> _entries = [];
    private int _builds;

    /// <summary>How many builds have been started: what a pan, a zoom or a time scrub costs is counted here, never timed.</summary>
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>
    /// Whether a disc of <paramref name="diameterPx"/> is drawn as a sprite: at least <see cref="PlanetDiscSprite.MinDiscPx"/>, and
    /// at least the dot it replaces (<paramref name="dotRadiusPx"/>), so a growing planet never shrinks at the hand-over.
    /// </summary>
    public static bool WantsSprite(double diameterPx, float dotRadiusPx) => diameterPx >= Math.Max(PlanetDiscSprite.MinDiscPx, 2 * dotRadiusPx);

    /// <summary>
    /// The globe's diameter on screen, in pixels, at <paramref name="pixelsPerRadian"/>: the ONE expression the draw and the click
    /// resolver both read, so the hit area cannot drift from the painted disc.
    /// </summary>
    public static double DiscDiameterPx(in PlanetAspect aspect, double pixelsPerRadian)
        => aspect.AngularDiameterArcsec / 3600 * Math.PI / 180 * pixelsPerRadian;

    /// <summary>The bucketed globe radius for an on-screen diameter, capped so the sprite fits <see cref="PlanetDiscSprite.MaxSpritePx"/>.</summary>
    public static (int Bucket, double RadiusPx) SizeBucket(CatalogIndex body, double diameterPx)
    {
        var radius = Math.Min(diameterPx / 2, PlanetDiscSprite.MaxRadiusPx(body));
        var bucket = (int)Math.Round(Math.Log2(Math.Max(radius, 1)) * SizeStepsPerOctave);
        return (bucket, Math.Min(Math.Pow(2, (double)bucket / SizeStepsPerOctave), PlanetDiscSprite.MaxRadiusPx(body)));
    }

    /// <summary>The angle step for a bucketed radius: the limb moves about one pixel per radian over the radius, never under a quarter degree.</summary>
    public static double AngleStepDeg(double bucketRadiusPx) => Math.Max(0.25, 180 / Math.PI / bucketRadiusPx);

    /// <summary>
    /// The key for <paramref name="aspect"/> drawn <paramref name="diameterPx"/> across with its pole at
    /// <paramref name="poleScreenAngleDeg"/>.
    /// </summary>
    public static PlanetSpriteKey KeyFor(in PlanetAspect aspect, double diameterPx, double poleScreenAngleDeg, bool mirrored,
        int paletteGeneration, bool night, int textureVersion, bool dimmed = false)
    {
        var (size, radius) = SizeBucket(aspect.Planet, diameterPx);
        var step = AngleStepDeg(radius);
        int Bucket(double angleDeg) => double.IsFinite(angleDeg) ? (int)Math.Round(Wrap(angleDeg) / step) : int.MinValue;
        return new PlanetSpriteKey(aspect.Planet, size,
            Bucket(aspect.CentralMeridianIII),
            Bucket(aspect.SubSolarLongitudeIII - aspect.CentralMeridianIII),
            Bucket(aspect.SubSolarLatitudeCentric),
            Bucket(aspect.SubObserverLatitudeCentric),
            Bucket(poleScreenAngleDeg),
            mirrored, paletteGeneration, night, textureVersion, dimmed);
    }

    /// <summary>
    /// The sprite to draw for this frame, or null when none has been built yet (the host draws the dot). A key that differs from the
    /// one in hand starts a build unless one is already running; what it returns meanwhile is the stale sprite.
    /// </summary>
    public PlanetSprite? Get(in PlanetAspect aspect, double diameterPx, double poleScreenAngleDeg, bool mirrored,
        PlanetColourMap? map, string credit, RGBAColor32 flat, RGBAColor32? nightTint, int paletteGeneration, int textureVersion,
        bool dimmed = false)
    {
        var key = KeyFor(aspect, diameterPx, poleScreenAngleDeg, mirrored, paletteGeneration, nightTint.HasValue, textureVersion, dimmed);
        if (!_entries.TryGetValue(aspect.Planet, out var entry))
        {
            _entries[aspect.Planet] = entry = new Entry();
        }
        if (entry.Pending is { IsCompleted: true } done)
        {
            entry.Pending = null;
            if (done.IsCompletedSuccessfully)
            {
                entry.Current = done.Result;
            }
        }
        if (entry.Current?.Key == key || entry.Pending is not null)
        {
            return entry.Current;
        }

        Interlocked.Increment(ref _builds);
        var snapped = Snapped(aspect, key);
        var (_, radius) = SizeBucket(aspect.Planet, diameterPx);
        var turn = key.Turn * AngleStepDeg(radius);
        var body = aspect.Planet;
        entry.Pending = Task.Run(() =>
        {
            var rgba = PlanetDiscSprite.Build(body, map, flat, snapped, radius, turn, mirrored, nightTint, out var side, dimmed ? DimmedOpacity : 1);
            return new PlanetSprite(body, rgba, side, radius, key, credit);
        });
        if (requestRedraw is { } redraw)
        {
            entry.Pending.ContinueWith(_ => redraw(), TaskScheduler.Default);
        }
        return entry.Current;
    }

    /// <summary>Waits for every running build, for a test that wants the sprite the next <see cref="Get"/> returns.</summary>
    internal async Task WhenBuiltAsync()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Pending is { } pending)
            {
                await pending;
            }
        }
    }

    // The aspect at its bucket centres, so a sprite is a function of its key alone and two frames with one key draw one picture.
    private static PlanetAspect Snapped(in PlanetAspect aspect, in PlanetSpriteKey key)
    {
        if (key.CentralMeridian == int.MinValue || key.SunLongitude == int.MinValue)
        {
            return aspect;
        }
        var (_, radius) = SizeBucket(aspect.Planet, Math.Pow(2, (double)key.Size / SizeStepsPerOctave) * 2);
        var step = AngleStepDeg(radius);
        var meridian = key.CentralMeridian * step;
        var observer = key.ObserverLatitude * step;
        var sunLatitude = key.SunLatitude * step;
        return aspect with
        {
            CentralMeridianIII = meridian,
            SubSolarLongitudeIII = meridian + (key.SunLongitude * step),
            SubObserverLatitude = aspect.SubObserverLatitude + (Unwrapped(observer) - aspect.SubObserverLatitudeCentric),
            SubObserverLatitudeCentric = Unwrapped(observer),
            SubSolarLatitude = aspect.SubSolarLatitude + (Unwrapped(sunLatitude) - aspect.SubSolarLatitudeCentric),
            SubSolarLatitudeCentric = Unwrapped(sunLatitude),
        };
    }

    // An angle into [0, 360).
    private static double Wrap(double degrees) => ((degrees % 360) + 360) % 360;

    // A latitude bucketed through Wrap back into [-180, 180).
    private static double Unwrapped(double degrees) => degrees >= 180 ? degrees - 360 : degrees;
}
