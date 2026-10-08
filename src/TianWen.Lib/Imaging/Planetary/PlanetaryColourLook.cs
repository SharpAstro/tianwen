using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A planetary master's colour look (#1273, <see cref="PlanetaryColourLook"/>): each pixel's OKLab chroma about grey mapped through a curve,
/// its hue and lightness kept, so what is colourful grows and what is near neutral stays so; and, when given, the planet's mean colour taken
/// to <see cref="Cast"/> first, a white balance. The curve is either <see cref="TargetChroma"/>, the chroma the interior is taken to at each of
/// <see cref="PlanetaryColourReading.QuantileGrid"/>'s quantiles (a reference's, fitted to), or <see cref="ChromaGains"/>, a gain at each of
/// them on the master's own chroma (the preset). Or, in place of a curve, every pixel moved about the planet's own colour
/// (<see cref="AboutPlanet"/>, the posts' look, #1305).
/// </summary>
public sealed record ColourLook
{
    /// <summary>The mean colour the master is white-balanced to first, its OKLab a, b; null leaves the tint as balanced.</summary>
    public OkLab? Cast { get; init; }

    /// <summary>The interior's chroma at each quantile of <see cref="PlanetaryColourReading.QuantileGrid"/> the look takes it to, or empty.</summary>
    public ImmutableArray<double> TargetChroma { get; init; } = [];

    /// <summary>The gain on the interior's own chroma at each quantile of <see cref="PlanetaryColourReading.QuantileGrid"/>, or empty.</summary>
    public ImmutableArray<double> ChromaGains { get; init; } = [];

    /// <summary>
    /// The look about the planet's own colour (C2, #1305), in place of a chroma curve: each pixel's saturation s, its OKLab (a, b) over L, taken
    /// to <c>Mean * m + Spread * (s - m)</c> about the interior's mean saturation m, its lightness kept. Spread widens every pixel's departure
    /// from the planet's colour, along its hue and across it, and Mean moves the planet's colour itself toward grey (below 1) or away. Null
    /// when the look is a chroma curve.
    /// </summary>
    public (double Spread, double Mean)? AboutPlanet { get; init; }

    /// <summary>Whether <see cref="AboutPlanet"/> is the planet's own, filled in by <see cref="For"/> (<see cref="Posted"/>).</summary>
    public bool PerPlanet { get; init; }

    /// <summary>
    /// The posts' look (#1305): C2 with each planet's (Spread, Mean) fitted through this routine to two outside observers' posts of it, read
    /// as shown; held out within the planet (fitted on one capture, applied to the other) it still landed nearer the post than True colour and
    /// Boosted, both ways, on both planets. Saturn's posts keep more of the planet's colour than Jupiter's. Resolve it with <see cref="For"/>.
    /// </summary>
    public static ColourLook Posted { get; } = new ColourLook { PerPlanet = true };

    // Posted's (Spread, Mean) per planet, fitted on the EdgeHD 11 and Meade 16 Saturns and on the 678MC and 12-inch SCT Jupiters (#1305): the
    // study's fit as shown missed the Meade by 26 % once ported here, so these are the refit through this routine.
    private static readonly (double Spread, double Mean) PostedSaturn = (2.80, 0.65);
    private static readonly (double Spread, double Mean) PostedJupiter = (2.95, 0.30);

    /// <summary>This look on <paramref name="planet"/>: <see cref="Posted"/> with the planet's own (Spread, Mean); any other look as it is.</summary>
    public ColourLook For(CatalogIndex planet)
    {
        if (!PerPlanet)
        {
            return this;
        }
        return this with { PerPlanet = false, AboutPlanet = planet == CatalogIndex.Saturn ? PostedSaturn : PostedJupiter };
    }

    /// <summary>
    /// The look a capture with no reference gets on asking, the owner's pick by eye (2026-10-05, #1273): an S-curve on each pixel's chroma
    /// about grey, its hue kept, the tint as balanced. It holds the less coloured half of the planet below the master's chroma (0.69 to 0.97,
    /// rings and zones whiter) and raises the more coloured half (1.04 to 1.68, belts redder). It is the mean, at every quantile, of four
    /// observers' posts' chroma over our default master's (the EdgeHD 11 and 16-inch Saturns, the 678MC Jupiter, the 12-inch SCT Jupiter).
    /// Built from three of them, the curve met the fourth within 25 % on three captures of four (#1273's V5, one value of twelve over): a
    /// taste the posts share, which the owner chose by eye.
    /// </summary>
    public static ColourLook Boosted { get; } = new ColourLook { ChromaGains = [.. BoostedGains] };

    // The S-curve above, at each of PlanetaryColourReading.QuantileGrid's quantiles (#1273's study, read linear against the posts as shown).
    private static ReadOnlySpan<double> BoostedGains =>
        [0.679, 0.694, 0.725, 0.776, 0.824, 0.869, 0.907, 0.939, 0.969, 1.004, 1.044, 1.092, 1.166, 1.216, 1.260, 1.303, 1.352, 1.404, 1.460, 1.536, 1.677];

    /// <summary>A look raising the chroma by one <paramref name="gain"/> at every quantile, the tint as balanced.</summary>
    public static ColourLook Uniform(double gain)
    {
        var gains = ImmutableArray.CreateBuilder<double>(PlanetaryColourReading.QuantileGrid.Length);
        for (var k = 0; k < PlanetaryColourReading.QuantileGrid.Length; k++)
        {
            gains.Add(gain);
        }
        return new ColourLook { ChromaGains = gains.MoveToImmutable() };
    }

    /// <summary>The look that takes a master to a reference read as <paramref name="reference"/>: its cast, then its chroma at every quantile.</summary>
    public static ColourLook FittedTo(in ColourReading reference)
    {
        var target = ImmutableArray.CreateBuilder<double>(PlanetaryColourReading.QuantileGrid.Length);
        foreach (var q in PlanetaryColourReading.QuantileGrid)
        {
            target.Add(reference.ChromaAt(q));
        }
        return new ColourLook { Cast = reference.Cast, TargetChroma = target.MoveToImmutable() };
    }

    /// <summary>The look in words, for a log line.</summary>
    public string Describe()
    {
        if (AboutPlanet is { } about)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"the posts' colour: every pixel's saturation about the planet's own taken {about.Spread:0.00} times as far from it, the planet's colour {about.Mean:0.00} of itself, each pixel's lightness kept");
        }
        if (!TargetChroma.IsDefaultOrEmpty)
        {
            var tint = Cast is { } cast
                ? string.Create(CultureInfo.InvariantCulture, $"its tint (chroma {cast.Chroma:0.0000} at hue {cast.HueDeg:0.0} deg), then ")
                : "";
            return $"the colour fitted to the reference: {tint}its chroma at every quantile, each pixel's hue kept";
        }
        return ReferenceEquals(this, Boosted)
            ? "the boosted colour: an S-curve on the chroma, the less coloured half whiter and the more coloured half stronger, each pixel's hue kept, the tint as balanced"
            : "the chroma raised by the gains given, each pixel's hue kept, the tint as balanced";
    }

    /// <summary>FITS cards recording the look, so a master says what was done to its colour.</summary>
    public IReadOnlyDictionary<string, (object Value, string Comment)> HeaderCards()
    {
        string kind;
        if (AboutPlanet is not null)
        {
            kind = "posted";
        }
        else if (!TargetChroma.IsDefaultOrEmpty)
        {
            kind = "fitted";
        }
        else
        {
            kind = ReferenceEquals(this, Boosted) ? "boosted" : "gains";
        }
        var cards = new Dictionary<string, (object Value, string Comment)>
        {
            ["CLOOK"] = (kind, "colour look baked in: not scene-linear (#1273)"),
        };
        if (AboutPlanet is { } about)
        {
            cards["CLOOKSP"] = (about.Spread, "colour look: the spread about the planet's colour (#1305)");
            cards["CLOOKMN"] = (about.Mean, "colour look: the planet's colour kept (#1305)");
        }
        if (Cast is { } cast)
        {
            cards["CLOOKCA"] = (cast.A, "colour look: the mean colour's OKLab a (#1273)");
            cards["CLOOKCB"] = (cast.B, "colour look: the mean colour's OKLab b (#1273)");
        }
        return cards;
    }
}

/// <summary>
/// A master made ready for a colour look (<see cref="PlanetaryColourLook.Prepare"/>, #1277): the image the look goes on, the planet's disk on
/// it, the limb fit's options (to fit another picture of the planet the same way), and, where the master had to be balanced first, the
/// balance; then <see cref="Master"/> is a new image its caller owns, and a writer records the balance's cards
/// (<see cref="ColourBalance.HeaderCards"/>), since the master's own file carries none.
/// </summary>
public readonly record struct PreparedMaster(Image Master, MetricDisk Disk, LimbFitOptions Options, ColourBalance? Balance);

/// <summary>
/// A colour look applied to a balanced planetary master (#1273, docs/plans/planetary-restoration.md). Every pixel is read in OKLab exactly as
/// <see cref="PlanetaryColourReading"/> reads it (its sky off, over the interior's mean luminance). First, given a cast, one gain a channel over
/// the whole frame takes the interior's mean to it, a white balance never faded. Then each pixel's chroma about grey goes through the look's
/// curve, its place among the interior's chroma read from the master's own distribution, its hue and lightness kept: the posts raise their
/// colour this way, the colourful growing and the near neutral staying so. The first look moved every pixel away from the planet's MEAN, and a
/// zone, a ring or a gap a little less tinted than the mean was pushed past grey to the opposite hue, blue. The curve is full where a pixel is
/// lit, from <see cref="LitFrom"/> of the interior's mean luminance, and gone below <see cref="DarkBelow"/> (a gap's or a shadow's chroma is
/// noise); full inside <see cref="FullInside"/> of the planet's outline and gone by <see cref="NoneFrom"/> (the limb's colour and any fringe
/// there is never raised). A colour pushed out of gamut is pulled back toward its own, never clipped a channel at a time.
/// </summary>
public static class PlanetaryColourLook
{
    /// <summary>The curve is full inside this fraction of the planet's outline (<see cref="MetricDisk.ClearRadiiAt"/>).</summary>
    public const double FullInside = 0.85;

    /// <summary>The curve has faded to none at this fraction of the outline, inside the rim the colour reading looks at from 0.9.</summary>
    public const double NoneFrom = 0.95;

    /// <summary>The curve is full from this fraction of the interior's mean luminance up, where the chroma distribution it is placed on starts.</summary>
    public const double LitFrom = PlanetaryColourReading.LitLuminance;

    /// <summary>The curve is gone below this fraction of the interior's mean luminance.</summary>
    public const double DarkBelow = 0.05;

    /// <summary>
    /// <paramref name="master"/> (a balanced three-channel master of the planet <paramref name="disk"/> describes) with <paramref name="look"/>
    /// applied, a new image, and the gain the curve put on the interior's chroma at each of <see cref="PlanetaryColourReading.QuantileGrid"/>'s
    /// quantiles.
    /// </summary>
    public static (Image Image, ImmutableArray<double> Gains) Apply(Image master, in MetricDisk disk, ColourLook look)
    {
        var (image, gains) = Applied(master, disk, look);
        return image is null
            ? throw new ArgumentException("The master's planet has no luminance to read its colour at.", nameof(master))
            : (image, gains);
    }

    /// <summary>
    /// <see cref="Apply"/>'s image, or null where the master's planet has no luminance to read its colour at (a live master whose disk is
    /// dark): the live view's colour look (#1277), which shows such a master as balanced rather than stopping.
    /// </summary>
    public static Image? TryApply(Image master, in MetricDisk disk, ColourLook look) => Applied(master, disk, look).Image;

    /// <summary>
    /// <paramref name="master"/> made ready for a look, ONE routine for the viewer's colour control and <c>planetary-look</c> (#1277): the
    /// planet's limb fitted at <paramref name="instant"/> for the disk the look reads its colour over, and a master left in the camera's colours
    /// balanced to the planet's colour first, as <c>planetary-stack</c> balances it, since on the camera's tint the curve would raise the tint,
    /// most of every pixel's chroma. Where it balanced, <see cref="PreparedMaster.Balance"/> is the balance and its image is a new one the caller
    /// owns; otherwise its image is <paramref name="master"/>. A refusal says why the master could not be made ready.
    /// </summary>
    public static (PreparedMaster? Prepared, string? Refusal) Prepare(Image master, CatalogIndex planet, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (master.ChannelCount != 3)
        {
            return (null, "a colour look needs a three-channel master");
        }
        if (!PhysicalEphemeris.Supports(planet))
        {
            return (null, $"a colour look is for Jupiter and Saturn, whose colour is measured; not {planet}");
        }
        if (PlanetaryLimbFit.FitAt(master, planet, instant) is not { } limb)
        {
            return (null, "the planet's limb could not be fitted");
        }
        if (master.ImageMeta.IsColourBalanced)
        {
            return (new PreparedMaster(master, limb.Disk, limb.Options, Balance: null), null);
        }
        // Over the disk just fitted: a second fit of the same limb was seconds for nothing.
        var (balance, how) = PlanetaryColourBalance.For(master, planet, limb.Disk);
        return balance is null
            ? (null, $"not colour balanced, and it could not be balanced ({how})")
            : (new PreparedMaster(balance.Apply(master), limb.Disk, limb.Options, balance), null);
    }

    /// <summary>
    /// <paramref name="master"/> with <paramref name="look"/> on it, made ready by <see cref="Prepare"/>: the viewer's colour control on a
    /// master it shows (#1277). A new image the caller owns, <paramref name="master"/> untouched; or a refusal.
    /// </summary>
    public static (Image? Looked, string? Refusal) OnMaster(Image master, CatalogIndex planet, DateTimeOffset instant, ColourLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var (prepared, refusal) = Prepare(master, planet, instant);
        if (prepared is not { } ready)
        {
            return (null, refusal);
        }
        try
        {
            return TryApply(ready.Master, ready.Disk, look.For(planet)) is { } looked
                ? (looked, null)
                : (null, "the planet has no luminance to read its colour at");
        }
        finally
        {
            if (ready.Balance is not null)
            {
                ready.Master.Release();
            }
        }
    }

    // Apply's work: null where the planet has no luminance to read its colour at.
    private static (Image? Image, ImmutableArray<double> Gains) Applied(Image master, in MetricDisk disk, ColourLook look)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(look);
        if (master.ChannelCount != 3)
        {
            throw new ArgumentException("A colour look needs a three-channel master.", nameof(master));
        }
        var (width, height) = (master.Width, master.Height);
        var red = master.GetChannelSpan(0);
        var green = master.GetChannelSpan(1);
        var blue = master.GetChannelSpan(2);
        var sky = PlanetaryColour.SkyOrBlack(red, green, blue, width, height, disk);
        // The master with its sky off, as the reading sees it; the sky goes back on at the end.
        var n = width * height;
        var (r, g, b) = (new float[n], new float[n], new float[n]);
        for (var i = 0; i < n; i++)
        {
            (r[i], g[i], b[i]) = ((float)(red[i] - sky.R), (float)(green[i] - sky.G), (float)(blue[i] - sky.B));
        }

        // The tint first, a white balance over the whole frame, never faded: faded at the limb with the rest, the limb kept the old tint and
        // read as a rim off the new one (#1273, a fitted look moved Saturn's 24 degrees in hue).
        if (look.Cast is { } target)
        {
            TakeCastTo(r, g, b, width, height, disk, target);
        }

        // Then the chroma curve, placed on the master's own chroma distribution as the tint left it.
        var reading = PlanetaryColourReading.Read(r, g, b, width, height, disk, default);
        var luminance = reading.Luminance;
        if (!(luminance > 0) || reading.InteriorChroma.IsDefaultOrEmpty)
        {
            return (null, default);
        }
        var grid = PlanetaryColourReading.QuantileGrid;
        var own = new double[grid.Length];
        var gains = new double[grid.Length];
        for (var k = 0; k < grid.Length; k++)
        {
            own[k] = reading.ChromaAt(grid[k]);
            gains[k] = look.AboutPlanet is null ? GainOf(look, k, own[k]) : 1;
        }

        var weights = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var (wr, wg, wb) = (weights[0], weights[1], weights[2]);
        // About the planet's own colour (C2, #1305): its mean saturation over the interior the look takes in full.
        var (meanA, meanB) = look.AboutPlanet is not null ? MeanSaturation(r, g, b, width, height, disk, luminance) : (0, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (!float.IsFinite(r[i]) || !float.IsFinite(g[i]) || !float.IsFinite(b[i]))
                {
                    continue;
                }
                var (nr, ng, nb) = (r[i] / luminance, g[i] / luminance, b[i] / luminance);
                var w = Weight(disk.ClearRadiiAt(x, y)) * Lit((wr * nr) + (wg * ng) + (wb * nb));
                if (w <= 0)
                {
                    continue;
                }
                var from = OkLab.FromLinearSrgb(nr, ng, nb);
                OkLab to;
                if (look.AboutPlanet is { } about)
                {
                    if (!(from.L > 0))
                    {
                        continue;
                    }
                    var targetA = ((about.Mean * meanA) + (about.Spread * ((from.A / from.L) - meanA))) * from.L;
                    var targetB = ((about.Mean * meanB) + (about.Spread * ((from.B / from.L) - meanB))) * from.L;
                    to = new OkLab(from.L, from.A + (w * (targetA - from.A)), from.B + (w * (targetB - from.B)));
                }
                else
                {
                    if (!(from.Chroma > 0))
                    {
                        continue;
                    }
                    var scale = 1 + ((GainAt(from.Chroma, own, gains) - 1) * w);
                    to = new OkLab(from.L, from.A * scale, from.B * scale);
                }
                var (outR, outG, outB) = Within(from, to, Math.Min(nr, Math.Min(ng, nb)) >= 0);
                (r[i], g[i], b[i]) = ((float)(outR * luminance), (float)(outG * luminance), (float)(outB * luminance));
            }
        }

        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[height, width];
        }
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var (outR, outG, outB) = ((float)(r[i] + sky.R), (float)(g[i] + sky.G), (float)(b[i] + sky.B));
                (planes[0][y, x], planes[1][y, x], planes[2][y, x]) = (outR, outG, outB);
                min = MathF.Min(min, MathF.Min(outR, MathF.Min(outG, outB)));
                max = MathF.Max(max, MathF.Max(outR, MathF.Max(outG, outB)));
            }
        }
        return (new Image(planes, master.BitDepth, max, min, 0, master.ImageMeta), ImmutableArray.Create(gains));
    }

    // The mean of each pixel's OKLab (a, b) over L across the interior the look takes in full (inside FullInside of the outline, lit from
    // LitFrom), the planet's own colour C2 moves about; the planes are the master's with its sky off, over the interior's mean `luminance`.
    private static (double A, double B) MeanSaturation(float[] r, float[] g, float[] b, int width, int height, in MetricDisk disk, double luminance)
    {
        var weights = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var (wr, wg, wb) = (weights[0], weights[1], weights[2]);
        var (sumA, sumB, count) = (0.0, 0.0, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (!float.IsFinite(r[i]) || !float.IsFinite(g[i]) || !float.IsFinite(b[i]) || disk.ClearRadiiAt(x, y) > FullInside)
                {
                    continue;
                }
                var (nr, ng, nb) = (r[i] / luminance, g[i] / luminance, b[i] / luminance);
                if ((wr * nr) + (wg * ng) + (wb * nb) < LitFrom)
                {
                    continue;
                }
                var lab = OkLab.FromLinearSrgb(nr, ng, nb);
                if (lab.L > 0)
                {
                    (sumA, sumB, count) = (sumA + (lab.A / lab.L), sumB + (lab.B / lab.L), count + 1);
                }
            }
        }
        return count > 0 ? (sumA / count, sumB / count) : (0, 0);
    }

    // The look's gain at grid point k on a master whose chroma there is `own`: the target over it when fitted, the preset's gain when given,
    // one when neither (or when the master holds no chroma there to scale).
    private static double GainOf(ColourLook look, int k, double own)
    {
        if (!look.TargetChroma.IsDefaultOrEmpty)
        {
            return own > 0 && look.TargetChroma[k] > 0 ? look.TargetChroma[k] / own : 1;
        }
        return look.ChromaGains.IsDefaultOrEmpty ? 1 : look.ChromaGains[k];
    }

    // The curve's gain at a chroma: its place among the interior's chroma at the grid's quantiles, read between them; past either end, that
    // end's gain. A chroma the interior holds over a run of quantiles (pixels that tie) takes the run's mean gain: one chroma cannot be told
    // apart into the run's several targets, and the run's first would give every tied pixel the gain of its lowest quantile.
    private static double GainAt(double chroma, double[] own, double[] gains)
    {
        var above = 0;
        while (above < own.Length && own[above] < chroma)
        {
            above++;
        }
        if (above == own.Length)
        {
            return gains[^1];
        }
        if (own[above] == chroma)
        {
            double sum = 0;
            var count = 0;
            for (var k = above; k < own.Length && own[k] == chroma; k++)
            {
                (sum, count) = (sum + gains[k], count + 1);
            }
            return sum / count;
        }
        if (above == 0)
        {
            return gains[0];
        }
        var t = (chroma - own[above - 1]) / (own[above] - own[above - 1]);
        return gains[above - 1] + (t * (gains[above] - gains[above - 1]));
    }

    // One gain a channel over the whole frame, found in a few rounds, taking the interior's mean colour to `target`'s a, b at the mean's own
    // lightness, a pixel of the mean's colour keeping its luminance.
    private static void TakeCastTo(float[] r, float[] g, float[] b, int width, int height, in MetricDisk disk, OkLab target)
    {
        var weights = CameraColorMatrix.SrgbToXyz.Slice(3, 3);
        var (wr, wg, wb) = (weights[0], weights[1], weights[2]);
        for (var round = 0; round < 4; round++)
        {
            var cast = PlanetaryColourReading.Read(r, g, b, width, height, disk, default).Cast;
            if (Math.Sqrt(((cast.A - target.A) * (cast.A - target.A)) + ((cast.B - target.B) * (cast.B - target.B))) < 2e-4)
            {
                return;
            }
            var now = cast.ToLinearSrgb();
            var want = new OkLab(cast.L, target.A, target.B).ToLinearSrgb();
            var held = ((wr * want.R) + (wg * want.G) + (wb * want.B)) / ((wr * now.R) + (wg * now.G) + (wb * now.B));
            var (gr, gg, gb) = (want.R / now.R / held, want.G / now.G / held, want.B / now.B / held);
            if (!(gr > 0) || !(gg > 0) || !(gb > 0) || !double.IsFinite(gr + gg + gb))
            {
                return;
            }
            for (var i = 0; i < r.Length; i++)
            {
                (r[i], g[i], b[i]) = ((float)(r[i] * gr), (float)(g[i] * gg), (float)(b[i] * gb));
            }
        }
    }

    /// <summary>How much of the curve a pixel at <paramref name="outline"/> of the planet's outline takes: one inside <see cref="FullInside"/>, none from <see cref="NoneFrom"/>, a smoothstep between.</summary>
    internal static double Weight(double outline) => Smooth(NoneFrom, FullInside, outline);

    /// <summary>How much of the curve a pixel at <paramref name="luminance"/> of the interior's mean luminance takes: none below <see cref="DarkBelow"/>, one from <see cref="LitFrom"/>, a smoothstep between.</summary>
    internal static double Lit(double luminance) => Smooth(DarkBelow, LitFrom, luminance);

    // A smoothstep: 0 at `zero`, 1 at `one`, whichever way round they lie.
    private static double Smooth(double zero, double one, double v)
    {
        var t = Math.Clamp((v - zero) / (one - zero), 0, 1);
        return t * t * (3 - (2 * t));
    }

    // `to`'s linear sRGB or, when that leaves a channel negative and `from`'s were not, the colour furthest along the line from `from` toward
    // it that stays in gamut, its lightness kept: a channel clipped alone would turn the hue.
    private static (double R, double G, double B) Within(in OkLab from, in OkLab to, bool fromInGamut)
    {
        var rgb = to.ToLinearSrgb();
        if (!fromInGamut || Math.Min(rgb.R, Math.Min(rgb.G, rgb.B)) >= 0)
        {
            return rgb;
        }
        double lo = 0, hi = 1;
        for (var step = 0; step < 20; step++)
        {
            var mid = (lo + hi) / 2;
            var c = Along(from, to, mid).ToLinearSrgb();
            if (Math.Min(c.R, Math.Min(c.G, c.B)) >= 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return Along(from, to, lo).ToLinearSrgb();

        static OkLab Along(in OkLab from, in OkLab to, double t) => new OkLab(from.L, from.A + (t * (to.A - from.A)), from.B + (t * (to.B - from.B)));
    }
}
