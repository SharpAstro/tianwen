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
public sealed record StarShapeChannel(double RealLight, double DrawnLight, double WingGain,
    ImmutableArray<double> RealRings = default, ImmutableArray<double> DrawnRings = default);

/// <summary>One brightness band of a master's measured star shape: its stars from <paramref name="MinSignificance"/> up.</summary>
public sealed record StarShapeBand(float MinSignificance, int Stars, ImmutableArray<StarShapeChannel> Channels);

/// <summary>A master's measured star shape, per brightness band and channel.</summary>
public sealed record StarShapeRow(string SessionId, ImmutableArray<StarShapeBand> Bands);

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
    /// 0.97 / 0.90), as the sky's bias would; every star, the fainter ones too, takes the bright band's gains.
    /// </summary>
    public static readonly ImmutableArray<float> DefaultBandEdges = [1000f];

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
    /// the stamp leaves the plane or touches an absent pixel.
    /// </summary>
    public static double Light(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, BitMatrix? absent = null)
    {
        var (x0, y0) = ((int)Math.Round(cx), (int)Math.Round(cy));
        if (x0 - StampRadius < 0 || y0 - StampRadius < 0 || x0 + StampRadius >= width || y0 + StampRadius >= height)
        {
            return double.NaN;
        }
        var sky = new List<double>();
        double sum = 0;
        var count = 0;
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
    /// pixel.
    /// </summary>
    public static double[] Rings(ReadOnlySpan<float> plane, int width, int height, double cx, double cy, BitMatrix? absent = null)
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
            sums[k] -= counts[k] * level;
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

    /// <summary>
    /// A master's star shape: its unsaturated, isolated first-pass Moffat stars on dark smooth sky, by brightness band, each
    /// channel's real light per amplitude against its field profile's at each star's place, and the wing gain at which the
    /// drawn median meets the real one (bisection in log gain; the light rises with the gain). A band with too few stars
    /// takes the nearest band's gains, and a master with too few in any band keeps gain 1. <paramref name="bandEdges"/>
    /// defaults to <see cref="DefaultBandEdges"/>.
    /// </summary>
    public static StarShapeRow Measure(string sessionId, Image master, ImmutableArray<FittedStar> catalogue, StarlessFieldProfile field, BitMatrix? absent,
        ImmutableArray<float> bandEdges = default)
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
        for (var b = 0; b < edges.Length; b++)
        {
            var stars = picked[b];
            var result = new StarShapeChannel[channels];
            var used = 0;
            for (var c = 0; c < channels; c++)
            {
                var plane = master.GetChannelSpan(c);
                var real = new List<double>();
                var realRings = new List<double[]>();
                var places = new List<(double Ox, double Oy)>();
                foreach (var s in stars)
                {
                    var light = Light(plane, width, height, s.X, s.Y, absent);
                    if (double.IsFinite(light))
                    {
                        var amplitude = s.ChannelAmplitudes[c];
                        real.Add(light / amplitude);
                        realRings.Add([.. Rings(plane, width, height, s.X, s.Y, absent).Select(v => v / amplitude)]);
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
                        RingMedians(realRings), drawnPlaces.Count > 0 ? DrawnRingsAt(0) : RingMedians([]));
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
                result[c] = new StarShapeChannel(target, DrawnAt(0), Math.Exp(logGain), RingMedians(realRings), DrawnRingsAt(logGain));
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
            bands.Add(new StarShapeBand(edges[b], measured[b].Stars, [.. channelsOut]));
        }
        return new StarShapeRow(sessionId, bands.MoveToImmutable());
    }

    /// <summary>The log wing gains a star of <paramref name="significance"/> is drawn at, per channel: its band's (a star
    /// fainter than every band takes the faintest's; a saturated one is given the brightest's by its caller).</summary>
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
        return [.. band.Channels.Select(static c => Math.Log(c.WingGain))];
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
