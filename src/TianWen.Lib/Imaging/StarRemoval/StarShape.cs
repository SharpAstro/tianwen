using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>One channel of one brightness band of a master's measured star shape (<see cref="StarShape"/>).</summary>
/// <param name="RealLight">The master's real stars: the light from <see cref="StarShape.LightFromPx"/> to
/// <see cref="StarShape.LightToPx"/> over each star's catalogued amplitude, the median over them.</param>
/// <param name="DrawnLight">The same, of the field profile drawn at amplitude 1 at each star's own sub-pixel place.</param>
/// <param name="WingGain">The wing gain (<see cref="StarProfile.HaloLogGain"/>, as a factor) at which the drawn light meets
/// the real; 1 where the band has too few stars to say.</param>
/// <param name="RealRings">The real stars' light in each of <see cref="StarShape.RingEdgesPx"/>'s rings over their amplitude,
/// the median over them; empty on a row measured before the rings were read.</param>
/// <param name="DrawnRings">The same of the drawn star at <paramref name="WingGain"/>.</param>
/// <param name="SkyBias">The per-pixel level (in the master's units) the real light was read off by: the median of
/// <see cref="StarShape.Light"/> over the pixels it counts, at positions with no catalogued star about them
/// (<see cref="StarShape.BlankField"/>), taken off every real star's light before it is divided by its amplitude; 0 on a row
/// measured before, or with the correction off.</param>
/// <param name="Stars">The stars this channel's light was read on (a star whose stamp left the plane or touched an absent pixel is
/// not one); 0 on a row measured before.</param>
/// <param name="Weight">How much this band's gain counts in a slope's fit: its stars over the scatter of one star's light
/// ratio, a fixed intrinsic part plus the light's own error against the sky's (<see cref="StarShape.BandWeight"/>); 0
/// where the band had too few stars to say, and on a row measured before.</param>
public sealed record StarShapeChannel(double RealLight, double DrawnLight, double WingGain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<double> RealRings = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<double> DrawnRings = default,
    double SkyBias = 0.0, int Stars = 0, double Weight = 0.0);

/// <summary>One brightness band of a master's measured star shape: its stars from <paramref name="MinSignificance"/> up.</summary>
/// <param name="MedianSignificance">The median significance of the band's stars, where the band's gain is read (the trend's
/// abscissa, <see cref="StarShape.LogGains"/>); 0 on a row measured before, which reads as <paramref name="MinSignificance"/>.</param>
public sealed record StarShapeBand(float MinSignificance, int Stars, ImmutableArray<StarShapeChannel> Channels, float MedianSignificance = 0f);

/// <summary>A master's measured star shape, per brightness band and channel.</summary>
/// <param name="Rig">The master's camera (<c>INSTRUME</c>), the key its slopes are pooled over
/// (<see cref="StarShape.PoolSlopesPerRig"/>); empty where unknown, and such a row keeps its own slopes.</param>
/// <param name="LogSlopes">Per channel, how the log wing gain rises with the log significance (<see cref="StarShape.LogGains"/>):
/// the master's own fit over its bands, or the rig's pooled slope once pooled. Empty on a row measured before, which draws every
/// star at its band's gain, as every row did.</param>
public sealed record StarShapeRow(string SessionId, ImmutableArray<StarShapeBand> Bands, string Rig = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<double> LogSlopes = default);

/// <summary>
/// How much light a master's real stars carry just outside their core, per catalogued amplitude, against the stars the
/// injector draws for it, per channel and brightness band, and the wing gain that closes each gap (R2s2,
/// docs/plans/star-remover-training.md). An injected star is drawn at a catalogued amplitude with the field profile, so the
/// measure is per amplitude: the light from <see cref="LightFromPx"/> to <see cref="LightToPx"/> above the sky (the median
/// <see cref="SkyFromPx"/> to <see cref="SkyToPx"/> out) over the star's channel amplitude, the drawn star read the same way
/// from <see cref="StarProfile.PixelMean"/> at the real star's own sub-pixel place.
/// <para>
/// Read on the stars from significance 1000, the field profile draws short of the real light 3 to 8 px out: on broadband
/// nights 0.85 / 0.88 / 0.80 of it (red / green / blue, the median of 15 masters), on dual-band nights 0.79 / 1.03 / 1.12
/// (59 masters), so the gains are about 1.2 on broadband nights and in a dual-band night's red, and about 1 in its green and
/// blue. Every master is calibrated from its own stars, so the gains follow its filter and its optics with no filter list.
/// The owner expected an OIII halo around a dual-band night's brightest stars; it showed only on giants, whose catalogued
/// amplitude is their first ring and which the injector never draws.
/// </para>
/// </summary>
public static class StarShape
{
    public const int StampRadius = 30;
    public const double LightFromPx = 3.0, LightToPx = 8.0, SkyFromPx = 22.0, SkyToPx = 28.0;

    /// <summary>
    /// The brightness bands read by default, by the finder's significance: each band's lower edge. One, from 1000: on a star
    /// fainter than that, the light 3 to 8 px out per amplitude is a fraction of a percent of the sky's error 22 to 28 px out,
    /// and the faint band read the opposite of the bright one (dual-band nights' gains 0.83 / 0.66 / 0.59 against 1.26 /
    /// 0.97 / 0.90), as the sky's bias would; every star, the fainter ones too, takes the bright band's gains. The bias is now
    /// measured and taken off (<see cref="BlankField"/>), which is what lets <see cref="TrendBandEdges"/> read fainter.
    /// </summary>
    public static readonly ImmutableArray<float> DefaultBandEdges = [1000f];

    /// <summary>
    /// The bands a brightness trend is read over, down to about 20 sigma (#1241): the review's real-over-synthetic ratio rises
    /// from 0.82 at 20-50 sigma to 1.81 at 100-200, so a band per octave or so from 20 up to the bright band the
    /// <see cref="DefaultBandEdges"/> read.
    /// </summary>
    public static readonly ImmutableArray<float> TrendBandEdges = [20f, 50f, 100f, 200f, 500f, 1000f];

    /// <summary>Blank-field samples (<see cref="BlankField"/>): positions with no catalogued star of at least
    /// <see cref="BlankClearSignificance"/> within <see cref="BlankClearPx"/>, at most <see cref="MaxBlankSamples"/> of them.</summary>
    public const float BlankClearSignificance = 5f;
    public const double BlankClearPx = SkyToPx + 3.0;
    public const int MaxBlankSamples = 600, MinBlankSamples = 10;

    /// <summary>Blank-field positions lie at least this far apart: their stamps overlap, but not their light annuli.</summary>
    public const double MinBlankStridePx = 48.0;

    /// <summary>The scatter of one star's light per amplitude about its band's median that is the stars' own (profile, colour,
    /// sub-pixel place), as a fraction: what <see cref="BandWeight"/> adds the sky's error to.</summary>
    public const double IntrinsicScatter = 0.1;

    /// <summary>On dark, smooth sky (<see cref="FittedStar.SkyAbove"/>, <see cref="FittedStar.Texture"/>).</summary>
    public const float MaxSkyAbove = 5f, MaxTexture = 1.5f;

    /// <summary>With no other catalogued star of this fraction of its amplitude within this many pixels.</summary>
    public const double IsolationPx = 15.0, IsolationFraction = 0.01;

    public const int MaxStarsPerBand = 200, MinStars = 10, DrawnStars = 48;

    /// <summary>The gain is found within these bounds.</summary>
    public const double MinGain = 0.25, MaxGain = 8.0;

    /// <summary>
    /// The light from <see cref="LightFromPx"/> to <see cref="LightToPx"/> about (<paramref name="cx"/>, <paramref name="cy"/>)
    /// above the sky in a row-major plane, the sky the median from <see cref="SkyFromPx"/> to <see cref="SkyToPx"/>; NaN where
    /// the stamp leaves the plane or touches an absent pixel. <paramref name="skyBias"/> is a per-pixel level taken off as well
    /// (<see cref="BlankField"/>), 0 for none.
    /// </summary>
    public static double Light(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, BitMatrix? absent = null, double skyBias = 0.0)
    {
        var light = LightAndCount(plane, width, height, cx, cy, absent, out var count);
        return light - count * skyBias;
    }

    // The light as Light reads it with no bias taken off, and the number of pixels it sums.
    private static double LightAndCount(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, BitMatrix? absent, out int count)
    {
        count = 0;
        var (x0, y0) = ((int)Math.Round(cx), (int)Math.Round(cy));
        if (x0 - StampRadius < 0 || y0 - StampRadius < 0 || x0 + StampRadius >= width || y0 + StampRadius >= height)
        {
            return double.NaN;
        }
        var sky = new List<double>();
        double sum = 0;
        for (var y = y0 - StampRadius; y <= y0 + StampRadius; y++)
        {
            for (var x = x0 - StampRadius; x <= x0 + StampRadius; x++)
            {
                var r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (r > SkyToPx || r < LightFromPx || (r >= LightToPx && r < SkyFromPx))
                {
                    continue;
                }
                if (absent is { } a && a[y, x])
                {
                    count = 0;
                    return double.NaN;
                }
                var v = plane[y * width + x];
                if (r >= SkyFromPx)
                {
                    sky.Add(v);
                }
                else
                {
                    sum += v;
                    count++;
                }
            }
        }
        return sum - count * Median(sky);
    }

    /// <summary>
    /// The rings a star's whole profile is read in, by radius in pixels: the core, the shoulder, the near wing and the wing
    /// (<see cref="Rings"/>). Round 2's cores: the wing gain matches the light 3 to 8 px out, and these say whether the core
    /// then matches too.
    /// </summary>
    public static readonly ImmutableArray<double> RingEdgesPx = [0.0, 1.5, 3.0, 4.5, 8.0];

    /// <summary>
    /// The light in each ring of <see cref="RingEdgesPx"/> about (<paramref name="cx"/>, <paramref name="cy"/>) above the sky,
    /// the sky read as <see cref="Light"/> reads it; NaN in every ring where the stamp leaves the plane or touches an absent
    /// pixel. <paramref name="skyBias"/> is a per-pixel level taken off every ring as well, as in <see cref="Light"/>.
    /// </summary>
    public static double[] Rings(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, BitMatrix? absent = null, double skyBias = 0.0)
    {
        var rings = RingEdgesPx.Length - 1;
        var sums = new double[rings];
        var counts = new int[rings];
        var (x0, y0) = ((int)Math.Round(cx), (int)Math.Round(cy));
        if (x0 - StampRadius < 0 || y0 - StampRadius < 0 || x0 + StampRadius >= width || y0 + StampRadius >= height)
        {
            Array.Fill(sums, double.NaN);
            return sums;
        }
        var outer = RingEdgesPx[^1];
        var sky = new List<double>();
        for (var y = y0 - StampRadius; y <= y0 + StampRadius; y++)
        {
            for (var x = x0 - StampRadius; x <= x0 + StampRadius; x++)
            {
                var r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (r > SkyToPx || (r >= outer && r < SkyFromPx))
                {
                    continue;
                }
                if (absent is { } a && a[y, x])
                {
                    Array.Fill(sums, double.NaN);
                    return sums;
                }
                var v = plane[y * width + x];
                if (r >= SkyFromPx)
                {
                    sky.Add(v);
                    continue;
                }
                var k = 0;
                while (r >= RingEdgesPx[k + 1])
                {
                    k++;
                }
                sums[k] += v;
                counts[k]++;
            }
        }
        var level = Median(sky);
        for (var k = 0; k < rings; k++)
        {
            sums[k] -= counts[k] * (level + skyBias);
        }
        return sums;
    }

    /// <summary>What <paramref name="profile"/> draws at amplitude 1 at sub-pixel offset (<paramref name="ox"/>,
    /// <paramref name="oy"/>), read in <see cref="Rings"/> as a real star is.</summary>
    public static double[] DrawnRings(StarProfile profile, double ox, double oy)
    {
        const int size = 2 * StampRadius + 1;
        var plane = new float[size * size];
        var outer = RingEdgesPx[^1];
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var (dx, dy) = (px - StampRadius - ox, py - StampRadius - oy);
                var r = Math.Sqrt(dx * dx + dy * dy);
                // Only the rings and the sky annulus are read.
                if (r < outer + 1 || r >= SkyFromPx - 1)
                {
                    plane[py * size + px] = (float)profile.PixelMean(px, py, StampRadius + ox, StampRadius + oy);
                }
            }
        }
        return Rings(plane, size, size, StampRadius + ox, StampRadius + oy);
    }

    /// <summary>The light <paramref name="profile"/> draws at amplitude 1, centred at sub-pixel offset (<paramref name="ox"/>,
    /// <paramref name="oy"/>) from a pixel centre, read as <see cref="Light"/> reads a real star.</summary>
    public static double DrawnLight(StarProfile profile, double ox, double oy)
    {
        const int size = 2 * StampRadius + 1;
        var plane = new float[size * size];
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var (dx, dy) = (px - StampRadius - ox, py - StampRadius - oy);
                var r = Math.Sqrt(dx * dx + dy * dy);
                // Only the light and sky annuli are read.
                if (r >= LightFromPx - 1 && (r < LightToPx + 1 || r >= SkyFromPx - 1))
                {
                    plane[py * size + px] = (float)profile.PixelMean(px, py, StampRadius + ox, StampRadius + oy);
                }
            }
        }
        return Light(plane, size, size, StampRadius + ox, StampRadius + oy);
    }

    /// <summary>What <see cref="BlankField"/> read: the per-pixel level the light reads off blank sky, the spread of one position's
    /// light about it (a robust sigma, in light units), and the positions it read.</summary>
    public readonly record struct BlankFieldRead(double Level, double Noise, int Samples);

    /// <summary>
    /// The bias of <see cref="Light"/> on sky with no star in it. The estimator subtracts the MEDIAN of the sky annulus from a
    /// SUM over the light annulus, so any sky whose mean is not its median (skewed noise, the glow of unresolved stars, a
    /// gradient across the stamp) leaves a level, per pixel, in every star's light that has nothing to do with the star. It is
    /// the same added light whatever the star's amplitude, so per catalogued amplitude it is huge on a faint star and nothing
    /// on a bright one, which is what turned the faint bands' wing gain the opposite way to the bright band's
    /// (<see cref="DefaultBandEdges"/>). It is measured where it can be read alone: <see cref="Light"/> at a grid of positions
    /// with no catalogued star of <see cref="BlankClearSignificance"/> within <see cref="BlankClearPx"/>, the median of each
    /// position's light over the pixels it counts being <see cref="BlankFieldRead.Level"/>, and the robust spread of that light
    /// <see cref="BlankFieldRead.Noise"/> (the light's own error against which a band is weighted, <see cref="BandWeight"/>). A
    /// star's own wing in its sky annulus is not in this: the drawn star is read through the same estimator.
    /// </summary>
    public static BlankFieldRead BlankField(ReadOnlySpan<float> plane, int width, int height, ImmutableArray<FittedStar> catalogue, BitMatrix? absent = null)
    {
        const double cell = BlankClearPx;
        var grid = new Dictionary<(int, int), List<(double X, double Y)>>();
        foreach (var s in catalogue)
        {
            if (s.Significance >= BlankClearSignificance && double.IsFinite(s.X) && double.IsFinite(s.Y))
            {
                var key = ((int)Math.Floor(s.X / cell), (int)Math.Floor(s.Y / cell));
                if (!grid.TryGetValue(key, out var list))
                {
                    grid[key] = list = [];
                }
                list.Add((s.X, s.Y));
            }
        }
        bool Clear(double x, double y)
        {
            var (gx, gy) = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (grid.TryGetValue((gx + dx, gy + dy), out var list)
                        && list.Any(o => (o.X - x) * (o.X - x) + (o.Y - y) * (o.Y - y) < BlankClearPx * BlankClearPx))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        const int margin = StampRadius + 1;
        var (spanX, spanY) = (width - 2 * margin, height - 2 * margin);
        if (spanX <= 0 || spanY <= 0)
        {
            return new BlankFieldRead(0.0, double.NaN, 0);
        }
        var stride = Math.Max(MinBlankStridePx, Math.Sqrt((double)spanX * spanY / MaxBlankSamples));
        var lights = new List<double>();
        var levels = new List<double>();
        for (var fy = (double)margin; fy < height - margin; fy += stride)
        {
            for (var fx = (double)margin; fx < width - margin; fx += stride)
            {
                if (!Clear(fx, fy))
                {
                    continue;
                }
                var light = LightAndCount(plane, width, height, fx, fy, absent, out var count);
                if (double.IsFinite(light) && count > 0)
                {
                    lights.Add(light);
                    levels.Add(light / count);
                }
            }
        }
        if (lights.Count < MinBlankSamples)
        {
            return new BlankFieldRead(0.0, double.NaN, lights.Count);
        }
        var centre = Median(lights);
        return new BlankFieldRead(Median(levels), 1.4826 * Median(lights.Select(v => Math.Abs(v - centre))), lights.Count);
    }

    /// <summary>
    /// How much a band's gain counts in a slope's fit: <paramref name="stars"/> over the squared scatter of one star's light
    /// ratio, <see cref="IntrinsicScatter"/> and the light's own error (<paramref name="lightNoise"/>, <see cref="BlankField"/>)
    /// against the light itself (<paramref name="medianAmplitude"/> times <paramref name="realLight"/> per amplitude), in
    /// quadrature. A faint band's light is a fraction of the sky's error, so it counts for little however many stars it holds,
    /// and a bright band's error is nothing against its own scatter.
    /// </summary>
    public static double BandWeight(int stars, double lightNoise, double medianAmplitude, double realLight)
    {
        if (stars <= 0)
        {
            return 0.0;
        }
        var relative = double.IsFinite(lightNoise) && lightNoise > 0
            ? lightNoise / (medianAmplitude * Math.Max(realLight, 1e-12))
            : 0.0;
        return stars / (IntrinsicScatter * IntrinsicScatter + relative * relative);
    }

    /// <summary>
    /// A master's star shape: its unsaturated, isolated first-pass Moffat stars on dark smooth sky, by brightness band, each
    /// channel's real light per amplitude against its field profile's at each star's place, and the wing gain at which the
    /// drawn median meets the real one (bisection in log gain; the light rises with the gain). A band with too few stars
    /// takes the nearest band's gains, and a master with too few in any band keeps gain 1. <paramref name="bandEdges"/>
    /// defaults to <see cref="DefaultBandEdges"/>.
    /// <para>
    /// The real light is read with the sky's bias taken off (<see cref="BlankField"/>; <paramref name="correctSkyBias"/> false
    /// reads it as before, for the comparison). The row also carries each channel's own brightness slope, the weighted
    /// least-squares line of the log gain over the log of each band's median significance (<see cref="OwnSlopes"/>; 0 where
    /// fewer than two bands are readable). It is the master's own, noisy as one master's bands are; a rig's masters pool it
    /// (<see cref="PoolSlopesPerRig"/>). <paramref name="rig"/> is the master's camera, the pooling key.
    /// </para>
    /// </summary>
    public static StarShapeRow Measure(string sessionId, Image master, ImmutableArray<FittedStar> catalogue, StarlessFieldProfile field, BitMatrix? absent,
        ImmutableArray<float> bandEdges = default, string rig = "", bool correctSkyBias = true)
    {
        var channels = master.ChannelCount;
        if (field.Channels.Length != channels)
        {
            throw new ArgumentException($"a field profile of {field.Channels.Length} channels for a {channels}-channel master", nameof(field));
        }
        var edges = bandEdges.IsDefaultOrEmpty ? DefaultBandEdges : bandEdges;
        var (width, height) = (master.Width, master.Height);
        var picked = Pick(catalogue, channels, edges);
        var measured = new (int Stars, StarShapeChannel[] Channels)[edges.Length];
        var blank = new BlankFieldRead[channels];
        for (var c = 0; c < channels; c++)
        {
            blank[c] = BlankField(master.GetChannelSpan(c), width, height, catalogue, absent);
        }
        for (var b = 0; b < edges.Length; b++)
        {
            var stars = picked[b];
            var result = new StarShapeChannel[channels];
            var used = 0;
            for (var c = 0; c < channels; c++)
            {
                var plane = master.GetChannelSpan(c);
                var skyBias = correctSkyBias ? blank[c].Level : 0.0;
                var real = new List<double>();
                var amplitudes = new List<double>();
                var realRings = new List<double[]>();
                var places = new List<(double Ox, double Oy)>();
                foreach (var s in stars)
                {
                    var light = Light(plane, width, height, s.X, s.Y, absent, skyBias);
                    if (double.IsFinite(light))
                    {
                        var amplitude = s.ChannelAmplitudes[c];
                        real.Add(light / amplitude);
                        amplitudes.Add(amplitude);
                        realRings.Add([.. Rings(plane, width, height, s.X, s.Y, absent, skyBias).Select(v => v / amplitude)]);
                        places.Add((s.X - Math.Round(s.X), s.Y - Math.Round(s.Y)));
                    }
                }
                used = Math.Max(used, real.Count);
                var ch = field.Channels[c];
                var profile = new StarProfile(StarProfileFamily.Field, ch.Fwhm, ch.Beta, 1.0, 0.0, ch.Table, ch.TableAlpha);
                var drawnPlaces = places.Count <= DrawnStars ? places : [.. Enumerable.Range(0, DrawnStars).Select(i => places[i * places.Count / DrawnStars])];
                double DrawnAt(double logGain) => Median(drawnPlaces.Select(p => DrawnLight(profile with { HaloLogGain = logGain }, p.Ox, p.Oy)));
                ImmutableArray<double> DrawnRingsAt(double logGain)
                    => RingMedians([.. drawnPlaces.Select(p => DrawnRings(profile with { HaloLogGain = logGain }, p.Ox, p.Oy))]);
                if (real.Count < MinStars)
                {
                    result[c] = new StarShapeChannel(real.Count > 0 ? Median(real) : double.NaN, drawnPlaces.Count > 0 ? DrawnAt(0) : double.NaN, double.NaN,
                        RingMedians(realRings), drawnPlaces.Count > 0 ? DrawnRingsAt(0) : RingMedians([]), skyBias, real.Count, 0.0);
                    continue;
                }
                var target = Median(real);
                var (lo, hi) = (Math.Log(MinGain), Math.Log(MaxGain));
                for (var i = 0; i < 30; i++)
                {
                    var mid = 0.5 * (lo + hi);
                    if (DrawnAt(mid) < target)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                var logGain = 0.5 * (lo + hi);
                result[c] = new StarShapeChannel(target, DrawnAt(0), Math.Exp(logGain), RingMedians(realRings), DrawnRingsAt(logGain), skyBias, real.Count,
                    BandWeight(real.Count, blank[c].Noise, Median(amplitudes), target));
            }
            measured[b] = (used, result);
        }
        // A band without a gain takes the nearest band's, and with none at all the master keeps its profile.
        var bands = ImmutableArray.CreateBuilder<StarShapeBand>(edges.Length);
        for (var b = 0; b < edges.Length; b++)
        {
            var channelsOut = new StarShapeChannel[channels];
            for (var c = 0; c < channels; c++)
            {
                var own = measured[b].Channels[c];
                var gain = Enumerable.Range(0, edges.Length).OrderBy(k => Math.Abs(k - b))
                    .Select(k => measured[k].Channels[c].WingGain).FirstOrDefault(double.IsFinite, 1.0);
                channelsOut[c] = own with { WingGain = double.IsFinite(gain) ? gain : 1.0 };
            }
            var median = picked[b].Count > 0 ? (float)Median(picked[b].Select(static s => (double)s.Significance)) : edges[b];
            bands.Add(new StarShapeBand(edges[b], measured[b].Stars, [.. channelsOut], median));
        }
        var row = new StarShapeRow(sessionId, bands.MoveToImmutable(), rig);
        return row with { LogSlopes = OwnSlopes(row) };
    }

    /// <summary>
    /// The log wing gains a star of <paramref name="significance"/> is drawn at, per channel. Without a slope
    /// (<see cref="StarShapeRow.LogSlopes"/> empty or zero in the channel) that is its band's gain, a star fainter than every
    /// band taking the faintest's and a saturated one being given the brightest's by its caller: exactly what a row measured
    /// before the trend drew. With one it is the TREND: the brightest band's own log gain (each master keeps its own level at
    /// the bright band) plus the slope times the log of the significance over the brightest band's median, the significance
    /// first clamped to the range the bands measured (the faintest band's median to the brightest's), and the result held to
    /// <see cref="MinGain"/> and <see cref="MaxGain"/>. A row of one band has no range, so it draws that band's gain whatever
    /// its slope.
    /// </summary>
    public static ImmutableArray<double> LogGains(StarShapeRow row, float significance)
    {
        var band = row.Bands[0];
        foreach (var b in row.Bands)
        {
            if (significance >= b.MinSignificance)
            {
                band = b;
            }
        }
        var step = band.Channels.Select(static c => Math.Log(c.WingGain)).ToArray();
        if (row.LogSlopes.IsDefaultOrEmpty || row.Bands.Length < 2)
        {
            return [.. step];
        }
        var references = row.Bands.Select(Reference).ToArray();
        var (faint, bright) = (references.Min(), references.Max());
        var brightBand = row.Bands[Array.IndexOf(references, bright)];
        var x = Math.Log(Math.Clamp(double.IsNaN(significance) ? bright : significance, faint, bright));
        for (var c = 0; c < step.Length && c < row.LogSlopes.Length && c < brightBand.Channels.Length; c++)
        {
            if (row.LogSlopes[c] != 0.0 && double.IsFinite(row.LogSlopes[c]))
            {
                var level = Math.Log(brightBand.Channels[c].WingGain);
                step[c] = Math.Clamp(level + row.LogSlopes[c] * (x - Math.Log(bright)), Math.Log(MinGain), Math.Log(MaxGain));
            }
        }
        return [.. step];
    }

    // Where a band's gain is read: its stars' median significance, its lower edge on a row that did not record one.
    private static double Reference(StarShapeBand band) => band.MedianSignificance > 0f ? band.MedianSignificance : band.MinSignificance;

    // A band's point in a channel's fit: log significance, log gain and weight, or none where the band said nothing (too few
    // stars) or its gain sits on a search bound, where the bisection found a limit and not a gain.
    private static IEnumerable<(double X, double Y, double W)> Points(StarShapeRow row, int channel)
    {
        foreach (var band in row.Bands)
        {
            if (channel < band.Channels.Length && band.Channels[channel] is { Weight: > 0.0 } ch
                && double.IsFinite(ch.WingGain) && ch.WingGain > MinGain * 1.01 && ch.WingGain < MaxGain / 1.01)
            {
                yield return (Math.Log(Reference(band)), Math.Log(ch.WingGain), ch.Weight);
            }
        }
    }

    /// <summary>
    /// The weighted within-master slope of log gain over log significance of one channel across several masters (the least-squares
    /// line of each master's points about that master's own weighted means, pooled), NaN where no master has two points at
    /// different significances. Each master's level drops out, so a rig's masters of different gains pool into one slope.
    /// </summary>
    private static double PooledSlope(IEnumerable<IReadOnlyList<(double X, double Y, double W)>> masters)
    {
        double cross = 0, spread = 0;
        foreach (var points in masters)
        {
            if (points.Count < 2)
            {
                continue;
            }
            var w = points.Sum(static p => p.W);
            var (mx, my) = (points.Sum(static p => p.W * p.X) / w, points.Sum(static p => p.W * p.Y) / w);
            cross += points.Sum(p => p.W * (p.X - mx) * (p.Y - my));
            spread += points.Sum(p => p.W * (p.X - mx) * (p.X - mx));
        }
        return spread > 1e-12 ? cross / spread : double.NaN;
    }

    /// <summary>A master's own slope per channel (<see cref="StarShapeRow.LogSlopes"/>): the weighted line over its readable
    /// bands (<see cref="BandWeight"/>), 0 in a channel with fewer than two.</summary>
    public static ImmutableArray<double> OwnSlopes(StarShapeRow row)
    {
        var channels = row.Bands.Length == 0 ? 0 : row.Bands[0].Channels.Length;
        return [.. Enumerable.Range(0, channels).Select(c =>
        {
            var slope = PooledSlope([[.. Points(row, c)]]);
            return double.IsFinite(slope) ? slope : 0.0;
        })];
    }

    /// <summary>
    /// Each rig's brightness slope, pooled: a master's camera is the rig (<see cref="StarShapeRow.Rig"/>), and its masters'
    /// bands are fitted together, per channel, each about its own mean so that every master keeps its own level and only the
    /// shared rise with brightness is pooled (<see cref="PooledSlope"/>). A master of a rig with no pooled slope in a channel
    /// (no master of it has two readable bands), and a master with no rig, keeps its own. Rows that do not agree in channel
    /// count are different rigs. Returns new rows; the input is unchanged.
    /// </summary>
    public static Dictionary<string, StarShapeRow> PoolSlopesPerRig(IReadOnlyDictionary<string, StarShapeRow> rows)
    {
        var pooled = new Dictionary<(string Rig, int Channels), double[]>();
        foreach (var group in rows.Values.Where(static r => r.Rig.Length > 0 && r.Bands.Length > 0).GroupBy(static r => (r.Rig, r.Bands[0].Channels.Length)))
        {
            var members = group.ToList();
            pooled[group.Key] = [.. Enumerable.Range(0, group.Key.Item2)
                .Select(c => PooledSlope(members.Select(r => (IReadOnlyList<(double X, double Y, double W)>)[.. Points(r, c)])))];
        }
        var result = new Dictionary<string, StarShapeRow>(rows.Count, StringComparer.Ordinal);
        foreach (var (id, row) in rows)
        {
            if (row.Rig.Length > 0 && row.Bands.Length > 0 && pooled.TryGetValue((row.Rig, row.Bands[0].Channels.Length), out var slopes))
            {
                var own = row.LogSlopes.IsDefaultOrEmpty ? OwnSlopes(row) : row.LogSlopes;
                result[id] = row with { LogSlopes = [.. slopes.Select((s, c) => double.IsFinite(s) ? s : (c < own.Length ? own[c] : 0.0))] };
            }
            else
            {
                result[id] = row;
            }
        }
        return result;
    }

    // The catalogue's readable stars per band, isolated, evenly spread over the significance order up to MaxStarsPerBand.
    private static List<FittedStar>[] Pick(ImmutableArray<FittedStar> catalogue, int channels, ImmutableArray<float> edges)
    {
        const double cell = IsolationPx;
        var grid = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < catalogue.Length; i++)
        {
            var key = ((int)Math.Floor(catalogue[i].X / cell), (int)Math.Floor(catalogue[i].Y / cell));
            if (!grid.TryGetValue(key, out var list))
            {
                grid[key] = list = [];
            }
            list.Add(i);
        }
        bool Isolated(int i)
        {
            var s = catalogue[i];
            var (gx, gy) = ((int)Math.Floor(s.X / cell), (int)Math.Floor(s.Y / cell));
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((gx + dx, gy + dy), out var list))
                    {
                        continue;
                    }
                    foreach (var j in list)
                    {
                        var o = catalogue[j];
                        if (j != i && o.Amplitude > IsolationFraction * s.Amplitude
                            && (o.X - s.X) * (o.X - s.X) + (o.Y - s.Y) * (o.Y - s.Y) < IsolationPx * IsolationPx)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }
        var bands = new List<FittedStar>[edges.Length];
        for (var b = 0; b < edges.Length; b++)
        {
            var (lo, hi) = (edges[b], b + 1 < edges.Length ? edges[b + 1] : float.PositiveInfinity);
            var readable = Enumerable.Range(0, catalogue.Length)
                .Where(i => catalogue[i] is { Outcome: StarFitOutcome.Subtracted, Model: StarFitModel.Moffat, Saturated: false, SecondPass: false } s
                    && s.Significance >= lo && s.Significance < hi
                    && s.SkyAbove < MaxSkyAbove && s.Texture < MaxTexture
                    && s.ChannelAmplitudes.Length == channels && s.ChannelAmplitudes.All(static a => a > 0 && float.IsFinite(a)))
                .Where(Isolated)
                .OrderByDescending(i => catalogue[i].Significance)
                .ToList();
            bands[b] = readable.Count <= MaxStarsPerBand
                ? [.. readable.Select(i => catalogue[i])]
                : [.. Enumerable.Range(0, MaxStarsPerBand).Select(k => catalogue[readable[k * readable.Count / MaxStarsPerBand]])];
        }
        return bands;
    }

    // Each ring's median over the stars' rows.
    private static ImmutableArray<double> RingMedians(IReadOnlyList<double[]> rows)
        => [.. Enumerable.Range(0, RingEdgesPx.Length - 1).Select(k => Median(rows.Select(r => r[k])))];

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(static v => v).ToArray();
        return sorted.Length == 0 ? double.NaN
            : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
    }

    /// <summary>Appends one master's row to a JSON-lines file.</summary>
    public static async Task AppendAsync(string path, StarShapeRow row, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(row, StarShapeJsonContext.Default.StarShapeRow);
        await File.AppendAllTextAsync(path, line + "\n", cancellationToken);
    }

    /// <summary>Every master's row in a JSON-lines file, by session (a later row of a session wins); empty where there is none.</summary>
    public static async Task<Dictionary<string, StarShapeRow>> ReadAllAsync(string path, CancellationToken cancellationToken = default)
    {
        var rows = new Dictionary<string, StarShapeRow>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return rows;
        }
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize(line, StarShapeJsonContext.Default.StarShapeRow) is { } row)
            {
                rows[row.SessionId] = row;
            }
        }
        return rows;
    }
}

[JsonSerializable(typeof(StarShapeRow))]
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal partial class StarShapeJsonContext : JsonSerializerContext;
