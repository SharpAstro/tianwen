using System;
using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The info box's night curve (#2059, docs/plans/yearly-planner.md section A): the selected object's altitude across the planner's
/// night, the part the planner counts shaded, a now mark and the line under it. The MODEL is <see cref="AtlasNightCurve"/>, scored
/// once per (object, night, minimum altitude, moon radius) off the render thread in <see cref="SkyMapState.GetNightCurveCached"/>;
/// this file asks for it and paints it.
/// </summary>
public partial class SkyMapTab<TSurface>
{
    /// <summary>The curve the last frame's panel drew, or null: what the tests read.</summary>
    internal AtlasNightCurve? LastNightCurve { get; private set; }

    /// <summary>Where the last frame drew the curve's slot, or null with none: what the tests read.</summary>
    internal RectF32? LastNightCurveRect { get; private set; }

    /// <summary>Where the last frame laid the panel's text rows: the inset the night curve section shares. What the tests read.</summary>
    internal RectF32? LastPanelTextRect { get; private set; }

    // Everything the score needs besides its key, by value, so the score reads nothing the render thread also writes. The
    // dictionary and the repository are immutable snapshots / thread-safe reads, as the planner's own background rescoring uses them.
    private readonly record struct NightCurveJob(
        SkyMapState.NightCurveKey Key,
        string Name,
        ObjectType ObjType,
        ImmutableDictionary<Target, ScoredTarget> Scored,
        ICometRepository? Comets,
        ITimeProvider Clock);

    /// <summary>
    /// The curve for the selection, or nothing: a host with no site or no night (the FITS viewer's atlas, a planner not yet
    /// computed), the mount's own entry and the zenith (whose position is not the object's but changes every frame) get none.
    /// <c>Unavailable</c> is the other kind of nothing: the host WOULD show a curve but the score failed (an unplaceable body), so
    /// the section stays, with a note, rather than the panel shrinking and jumping when the answer lands.
    /// </summary>
    private (AtlasNightCurve? Curve, bool Pending, bool Unavailable) NightCurveFor(PlannerState plannerState, in SkyMapInfoPanelData info)
    {
        if (_timeProvider is not { } clock
            || double.IsNaN(plannerState.SiteLatitude) || double.IsNaN(plannerState.SiteLongitude)
            || plannerState.AstroTwilight <= plannerState.AstroDark
            || info.IsMount || info.FixedPoint != SkyFixedPoint.None
            || double.IsNaN(info.RA) || double.IsNaN(info.Dec))
        {
            return default;
        }

        // A moving body is keyed by its index alone: its live RA/Dec follows the scrub, the night's curve does not.
        var moving = info.Index is { } index && index.IsSolarSystemObject;
        var tonightsBest = plannerState.TonightsBest;
        var key = new SkyMapState.NightCurveKey(
            info.Index,
            moving ? 0d : info.RA,
            moving ? 0d : info.Dec,
            plannerState.AstroDark,
            plannerState.AstroTwilight,
            plannerState.MinHeightAboveHorizon,
            ObservationScheduler.DefaultMoonAvoidanceRadiusDeg,
            tonightsBest.Length > 0 ? tonightsBest[0].CombinedScore : 0d,
            plannerState.SiteLatitude,
            plannerState.SiteLongitude,
            plannerState.SiteElevation);

        // The key says what the curve is of; the rest is what the score is run with once, and is not part of its identity.
        var job = new NightCurveJob(key, info.Name, info.ObjType, plannerState.ScoredTargets, plannerState.Comets, clock);
        var (curve, pending) = State.GetNightCurveCached(in key, job, static job => ComputeNightCurve(in job));
        return (curve, pending, curve is null && !pending);
    }

    // Runs on a thread-pool thread: pure over its argument, and the planner's own scoring and the ephemerides it calls are pure.
    private static AtlasNightCurve? ComputeNightCurve(in NightCurveJob job)
    {
        var key = job.Key;
        var (ra, dec) = (key.RA, key.Dec);

        // A moving body is placed at the night's midpoint: its drift over a night is below the 15-minute grid's resolution for
        // everything but the Moon, which gets the curve with no shade (AtlasNightCurve.Shaded) for that reason.
        if (key.Index is { } body && body.IsSolarSystemObject)
        {
            var midpoint = key.AstroDark + (key.AstroTwilight - key.AstroDark) / 2;
            var placed = body.ToCatalog() == Catalog.Comet
                ? job.Comets is { } comets && comets.TryGetPosition(body, midpoint, out ra, out dec, out _)
                : VSOP87a.ReduceJ2000(body, midpoint, out ra, out dec, out _);
            if (!placed)
            {
                return null;
            }
        }

        // The planner's own row for this object, when it scored it tonight: its type and bonus make the rating the row's.
        var type = job.ObjType;
        var bonus = 1.0;
        foreach (var scored in job.Scored.Values)
        {
            if (IsSameObject(scored.Target, key, ra, dec))
            {
                type = scored.ObjectType;
                bonus = (double)scored.ObjectBonus;
                break;
            }
        }

        var transform = new Transform(job.Clock)
        {
            SiteLatitude = key.Latitude,
            SiteLongitude = key.Longitude,
            SiteElevation = key.Elevation
        };
        var curve = AtlasNightCurve.Create(
            new Target(ra, dec, job.Name, key.Index), transform, key.AstroDark, key.AstroTwilight, key.MinAltitude,
            key.BestScore, type, bonus, key.MoonRadiusDeg);

        // No planner list yet (BestScore 0): the object is the best there is, as a list of one would say, so five stars. The
        // planner's own fallback for an empty list is ScoreToRating(score, 1.0) (PlannerActions.FormatTonightsBestLines), which is
        // five stars for any score of 1 or more and so says the same for every object worth drawing; this states it outright
        // rather than lean on that, and ScoreToRating(score, 0) would answer 0.5 for everything.
        return key.BestScore > 0 ? curve : curve with { Rating = 5.0f };
    }

    // The planner's identity for a body (PlannerActions.IsSameObject: a solar-system body is its index, never its position at
    // an instant), else the same catalogue index at the same place.
    private static bool IsSameObject(Target target, in SkyMapState.NightCurveKey key, double ra, double dec)
        => key.Index is { } index
            ? target.CatalogIndex == index && (index.IsSolarSystemObject || (Math.Abs(target.RA - ra) < 1e-6 && Math.Abs(target.Dec - dec) < 1e-6))
            : target.CatalogIndex is null && Math.Abs(target.RA - ra) < 1e-6 && Math.Abs(target.Dec - dec) < 1e-6;

    /// <summary>Alpha of the lightest shaded sample (a moon-washed one) and of the strongest (no Moon counted).</summary>
    private const byte NightShadeMinAlpha = 0x30;
    private const byte NightShadeMaxAlpha = 0x90;

    /// <summary>The altitude the curve's scale reaches at least, so a low object does not fill the slot to look high.</summary>
    private const double NightCurveMinScaleDeg = 60d;

    /// <summary>
    /// Paints the night curve into <paramref name="rect"/>: the horizon, the profile's minimum altitude, the altitude line, the
    /// counting samples shaded between that level and the line (paler where the Moon penalises them, from the sample's own
    /// weight), and a now mark when <paramref name="viewingTime"/> is inside the night. A scrub only moves the mark.
    /// </summary>
    private void DrawNightCurve(AtlasNightCurve curve, RectF32 rect, DateTimeOffset viewingTime)
    {
        var samples = curve.Samples;
        var span = curve.AstroTwilight - curve.AstroDark;
        if (samples.Length < 2 || span <= TimeSpan.Zero || rect.Width < 2f || rect.Height < 2f)
        {
            return;
        }

        var dpiScale = DpiScale;
        var peak = 0d;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, sample.Altitude);
        }
        // The scale rounds up to a whole 15 degrees so it moves in steps, never with every object.
        var scaleMax = Math.Min(90d, Math.Max(NightCurveMinScaleDeg, Math.Ceiling(peak / 15d) * 15d));

        var x0 = rect.X;
        var w = rect.Width;
        var baseline = rect.Y + rect.Height;
        float Y(double altitude) => baseline - (float)(Math.Clamp(altitude, 0d, scaleMax) / scaleMax) * rect.Height;
        float X(DateTimeOffset time) => x0 + w * (float)Math.Clamp((time - curve.AstroDark) / span, 0d, 1d);

        var lineColour = Night ? GuiTheme.Palette.Accent : SparklineLine;
        var minY = Y(curve.MinAltitude);

        // The shade, one thin column at a time between the minimum level and the line: the altitude is interpolated between
        // samples so the edge where the line crosses the level is a slope, and the weight is interpolated between the COUNTING
        // neighbours only. A sample at or below the minimum has weight 0 (it does not count), so taking the nearest sample's
        // weight dropped the shade for up to half a step (about 7 minutes) before the line crossed the level; a column whose
        // interpolated altitude is above the level takes the weight of whichever neighbour counts. The renderer has no polygon
        // fill, so a column stays the primitive; the columns are one device pixel wide, so the stepped top edge is a pixel's.
        if (curve.Shaded)
        {
            var columnW = MathF.Max(1f, MathF.Round(dpiScale));
            var last = samples.Length - 1;
            for (var cx = 0f; cx < w; cx += columnW)
            {
                var (altitude, weight) = curve.ShadeAt((cx + columnW / 2f) / w * last);
                if (altitude <= curve.MinAltitude || weight <= 0f)
                {
                    continue;
                }

                var alpha = (byte)(NightShadeMinAlpha + Math.Clamp(weight, 0f, 1f) * (NightShadeMaxAlpha - NightShadeMinAlpha));
                var top = Y(altitude);
                FillRect(x0 + cx, top, MathF.Min(columnW, w - cx), minY - top, lineColour with { Alpha = alpha });
            }
        }

        // The horizon and the minimum altitude the planner counts above.
        DrawLine(x0, baseline, x0 + w, baseline, Night ? GuiTheme.Palette.Separator : SparklineAxis);
        DrawLine(x0, minY, x0 + w, minY, SparklineLabelColour with { Alpha = 0xA0 });

        var prevX = 0f;
        var prevY = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var sx = X(samples[i].Time);
            var sy = Y(samples[i].Altitude);
            if (i > 0)
            {
                DrawLine(prevX, prevY, sx, sy, lineColour);
            }
            prevX = sx;
            prevY = sy;
        }

        // The viewing instant, when it is in the night: a mark on the line at the altitude the line has there.
        if (viewingTime >= curve.AstroDark && viewingTime <= curve.AstroTwilight)
        {
            var nowX = X(viewingTime);
            var now = Night ? GuiTheme.Palette.BodyText : SparklineNow;
            DrawLine(nowX, rect.Y, nowX, baseline, now with { Alpha = 0x60 });
            var position = (viewingTime - curve.AstroDark) / span * (samples.Length - 1);
            var j = Math.Min((int)position, samples.Length - 2);
            var nowAltitude = samples[j].Altitude + (samples[j + 1].Altitude - samples[j].Altitude) * (position - j);
            FillCircle(nowX, Y(nowAltitude), 2.5f * dpiScale, now);
        }
    }
}
