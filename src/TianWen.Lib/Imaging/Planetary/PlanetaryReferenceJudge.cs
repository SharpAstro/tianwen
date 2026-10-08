using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Where another program's result of the same capture lies on a master (<see cref="PlanetaryReferenceJudge.Place"/>): a reference point is
/// its centre plus the master point's offset from the master's centre over <paramref name="Scale"/> (master pixels a reference pixel), turned
/// by <paramref name="RotationDeg"/> after a mirror in x when <paramref name="Mirrored"/>, and shifted by (<paramref name="ShiftX"/>,
/// <paramref name="ShiftY"/>) reference pixels. <paramref name="Correlation"/> is the band-passed detail's at that placement, over the planet.
/// </summary>
public readonly record struct ReferencePlacement(double Scale, double RotationDeg, bool Mirrored, double ShiftX, double ShiftY, double Correlation);

/// <summary>
/// One a trous band of a master against a reference placed on it and matched to its tone: their correlation, the master's RMS over the
/// reference's (<paramref name="EnergyRatio"/>), and the master's least-squares gain on the reference's band (<paramref name="SharedGain"/>,
/// the correlation times the energy ratio). A master that lifts what the reference holds raises the gain; one that adds what the reference
/// lacks (noise, ringing, a lattice) raises the energy ratio alone and lowers the correlation.
/// </summary>
public readonly record struct BandJudgement(int Band, double Correlation, double EnergyRatio, double SharedGain);

/// <summary>A master judged against a reference: the placement, then the bands on the globe and, for Saturn, on the rings off it.</summary>
public sealed record ReferenceJudgement(ReferencePlacement Placement, ImmutableArray<BandJudgement> Globe, ImmutableArray<BandJudgement> Rings,
    int GlobePixels, int RingPixels);

/// <summary>
/// A master judged against another program's result of the same capture (a <c>_stack</c> or <c>_post</c> beside it): the goal's judge, since
/// no truth exists for a real capture. The reference is a display picture at its own scale, turn and tone, so it is first PLACED on the master
/// (<see cref="Place"/>: the scale from the two limb fits, the turn and a mirror searched on the band-passed detail over the planet, then all
/// of them refined), resampled onto the master's grid (bilinear, which blurs the reference a little, never the master), and its tone matched
/// to the master's by histogram over the planet (<see cref="MatchTone"/>: any monotone curve a processor drew is undone, so band energies
/// compare). Then each a trous band is read inside 0.9 radii of the globe and, for Saturn, on the rings clear of the globe
/// (<see cref="Judge"/>). Every plane is a luminance, the master's in its own linear units.
/// </summary>
public static class PlanetaryReferenceJudge
{
    /// <summary>How many a trous bands are judged, finest first.</summary>
    public const int Bands = 5;

    // The globe is judged inside this many radii, clear of the limb's edge, whose sharpening differs most and says nothing of detail.
    private const double GlobeRadii = 0.9;

    /// <summary>
    /// Where <paramref name="reference"/> lies on <paramref name="master"/>: the scale from the two disks' radii, the centres matched, then the
    /// turn searched in 2 degree steps both unmirrored and mirrored, and the best refined in turn, scale and shift. Judged by the correlation
    /// of each plane's detail over the master's planet, its rings included and the globe's limb left out: each plane equalised over its own
    /// planet first (its values by rank, so no display curve matters), then a difference of Gaussians 1 and 4 master pixels wide, the
    /// reference's widths in its own pixels, since a band-pass at each plane's own pixel scale reads two different scales of the planet
    /// (a reference 1.5 times larger placed 1.8 % small at a correlation of 0.79).
    /// </summary>
    public static ReferencePlacement Place(ReadOnlySpan<float> master, int width, int height, MetricDisk masterDisk,
        ReadOnlySpan<float> reference, int referenceWidth, int referenceHeight, MetricDisk referenceDisk)
    {
        var scale = masterDisk.Radius / referenceDisk.Radius;
        var m = Detail(Equalised(master, width, height, masterDisk), width, height, 1);
        var r = Detail(Equalised(reference, referenceWidth, referenceHeight, referenceDisk), referenceWidth, referenceHeight, 1 / scale);
        // Clear of the globe's limb, whose edge a display curve or a sharpening moves (a square-root curve placed a reference 1.2 % small).
        var region = Region(width, height, masterDisk, 1.0, rings: true, clearOfLimb: true);
        var context = new SearchContext(m, r, width, referenceWidth, referenceHeight, region, masterDisk, referenceDisk);

        var best = new ReferencePlacement(scale, 0, false, 0, 0, double.NegativeInfinity);
        foreach (var mirrored in (ReadOnlySpan<bool>)[false, true])
        {
            for (var angle = 0.0; angle < 360; angle += 2)
            {
                var candidate = best with { RotationDeg = angle, Mirrored = mirrored };
                var c = context.Correlation(candidate);
                if (c > best.Correlation)
                {
                    best = candidate with { Correlation = c };
                }
            }
        }
        // Refined one coordinate at a time, twice over: the turn, the scale, then the shift.
        for (var pass = 0; pass < 2; pass++)
        {
            best = Refine(context, best, p => p.RotationDeg, (p, v) => p with { RotationDeg = v }, 2.5, 0.1);
            best = Refine(context, best, p => p.Scale, (p, v) => p with { Scale = v }, best.Scale * 0.02, best.Scale * 0.002);
            best = Refine(context, best, p => p.ShiftX, (p, v) => p with { ShiftX = v }, 1.5, 0.1);
            best = Refine(context, best, p => p.ShiftY, (p, v) => p with { ShiftY = v }, 1.5, 0.1);
        }
        return best;
    }

    /// <summary><paramref name="reference"/> resampled onto the master's grid at <paramref name="placement"/>; NaN where it does not reach.</summary>
    public static float[] Resample(ReadOnlySpan<float> reference, int referenceWidth, int referenceHeight, MetricDisk referenceDisk,
        int width, int height, MetricDisk masterDisk, in ReferencePlacement placement)
    {
        var result = new float[width * height];
        var map = new Mapping(masterDisk, referenceDisk, placement);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (u, v) = map.At(x, y);
                result[(y * width) + x] = Sample(reference, referenceWidth, referenceHeight, u, v);
            }
        }
        return result;
    }

    /// <summary>
    /// <paramref name="reference"/> (on the master's grid) mapped onto <paramref name="master"/>'s tone: each value taken to the master's value
    /// at the same rank over the master's planet (<see cref="MetricDisk.ClearRadiiAt"/> under 1). A monotone curve between them is undone; a
    /// value outside the planet takes the nearest end of the curve.
    /// </summary>
    public static float[] MatchTone(ReadOnlySpan<float> master, ReadOnlySpan<float> reference, int width, int height, MetricDisk masterDisk)
    {
        var region = Region(width, height, masterDisk, 1.0, rings: true);
        var ours = new List<float>(region.Length);
        var theirs = new List<float>(region.Length);
        foreach (var i in region)
        {
            if (float.IsFinite(master[i]) && float.IsFinite(reference[i]))
            {
                ours.Add(master[i]);
                theirs.Add(reference[i]);
            }
        }
        ours.Sort();
        theirs.Sort();
        var result = new float[reference.Length];
        if (theirs.Count < 2)
        {
            reference.CopyTo(result);
            return result;
        }
        var quantiles = new Quantiles(theirs);
        for (var i = 0; i < result.Length; i++)
        {
            var v = reference[i];
            if (!float.IsFinite(v))
            {
                result[i] = float.NaN;
                continue;
            }
            // The reference value's rank, read off the master's values at the same rank.
            var q = quantiles.Of(v) * (ours.Count - 1);
            var k = Math.Min((int)q, ours.Count - 2);
            result[i] = (float)(ours[k] + ((q - k) * (ours[k + 1] - ours[k])));
        }
        return result;
    }

    /// <summary>
    /// <paramref name="master"/> against <paramref name="reference"/> already placed on its grid and matched to its tone, band by band, on the
    /// globe inside 0.9 radii clear of the rings, and on the rings off the globe (Saturn).
    /// </summary>
    public static ReferenceJudgement Judge(ReadOnlySpan<float> master, ReadOnlySpan<float> reference, int width, int height, MetricDisk masterDisk,
        in ReferencePlacement placement)
    {
        // Where the reference does not reach, the master stands in, so the transform has a plane to run over; no such pixel is read.
        var filled = new float[reference.Length];
        for (var i = 0; i < filled.Length; i++)
        {
            filled[i] = float.IsFinite(reference[i]) ? reference[i] : master[i];
        }
        var m = ATrousWaveletTransform.Decompose(master, width, height, Bands);
        var r = ATrousWaveletTransform.Decompose(filled, width, height, Bands);
        var globe = Region(width, height, masterDisk, GlobeRadii, rings: false, reference);
        var rings = masterDisk.Rings is null ? [] : RingRegion(width, height, masterDisk, reference);
        return new ReferenceJudgement(placement, ReadBands(m, r, globe), ReadBands(m, r, rings), globe.Length, rings.Length);

        static ImmutableArray<BandJudgement> ReadBands(WaveletDecomposition m, WaveletDecomposition r, int[] region)
        {
            if (region.Length == 0)
            {
                return [];
            }
            var result = ImmutableArray.CreateBuilder<BandJudgement>(Bands);
            for (var j = 0; j < Bands; j++)
            {
                var mj = m.Detail(j);
                var rj = r.Detail(j);
                double mm = 0, rr = 0, mr = 0;
                foreach (var i in region)
                {
                    mm += (double)mj[i] * mj[i];
                    rr += (double)rj[i] * rj[i];
                    mr += (double)mj[i] * rj[i];
                }
                var correlation = mm > 0 && rr > 0 ? mr / Math.Sqrt(mm * rr) : double.NaN;
                var ratio = rr > 0 ? Math.Sqrt(mm / rr) : double.NaN;
                result.Add(new BandJudgement(j + 1, correlation, ratio, rr > 0 ? mr / rr : double.NaN));
            }
            return result.MoveToImmutable();
        }
    }

    /// <summary>
    /// The colour of <paramref name="master"/> and of <paramref name="reference"/> placed on it at <paramref name="placement"/>, as the eye reads
    /// each (#1273, <see cref="PlanetaryColourReading"/>): the reference decoded from sRGB, resampled onto the master's grid, its luminance (the
    /// channels' mean) taken to the master's by rank and its chromaticity kept, then both read in OKLab over the master's planet.
    /// </summary>
    public static (ColourReading Master, ColourReading Reference) ReadColours(Image master, in MetricDisk masterDisk, Image reference, in MetricDisk referenceDisk,
        in ReferencePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(reference);
        var (width, height) = (master.Width, master.Height);
        var (r, g, b, _, _) = PlanetaryColour.LinearFromSrgb(reference);
        var red = Resample(r, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement);
        var green = Resample(g, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement);
        var blue = Resample(b, reference.Width, reference.Height, referenceDisk, width, height, masterDisk, placement);
        var (theirs, ours) = (new float[red.Length], new float[red.Length]);
        var mr = master.GetChannelSpan(0);
        var mg = master.GetChannelSpan(1);
        var mb = master.GetChannelSpan(2);
        for (var i = 0; i < theirs.Length; i++)
        {
            theirs[i] = (red[i] + green[i] + blue[i]) / 3;
            ours[i] = (mr[i] + mg[i] + mb[i]) / 3;
        }
        var (tr, tg, tb) = PlanetaryColourReading.WithLuminance(red, green, blue, MatchTone(ours, theirs, width, height, masterDisk));
        return (
            PlanetaryColourReading.Read(mr, mg, mb, width, height, masterDisk, PlanetaryColour.SkyOrBlack(mr, mg, mb, width, height, masterDisk)),
            PlanetaryColourReading.Read(tr, tg, tb, width, height, masterDisk, PlanetaryColour.SkyOrBlack(tr, tg, tb, width, height, masterDisk)));
    }

    /// <summary>
    /// <paramref name="master"/> as the planetary preview SHOWS it (#1273): through <see cref="Image.ComputePlanetaryStretchUniforms"/>
    /// and <see cref="Image.RenderStretchedRgba16"/>, as <see cref="Stacking.MasterPreviewRenderer.RenderPlanetaryAsync"/> writes its PNG,
    /// three sRGB-encoded planes in [0, 1]. The preview's mid-tone lift bends each channel on its own and the sRGB curve again on
    /// screen, so a master is SHOWN at 1.7 to 2 times the chroma its linear planes read; a post is a picture as shown, so it is
    /// compared with this, never with the linear master.
    /// </summary>
    public static Image AsShown(Image master)
    {
        ArgumentNullException.ThrowIfNull(master);
        var (width, height) = (master.Width, master.Height);
        var rgba = new ushort[width * height * 4];
        master.RenderStretchedRgba16(master.ComputePlanetaryStretchUniforms(), rgba);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = planes[c] = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] = rgba[((((y * width) + x) * 4) + c)] / (float)ushort.MaxValue;
                }
            }
        }
        return new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
    }

    /// <summary>
    /// The colour of <paramref name="master"/> as its preview shows it (<see cref="AsShown"/>), read exactly as a reference is: decoded from
    /// sRGB, its luminance taken to the master's by rank and its chromaticity kept (<see cref="ReadColours"/> at the identity placement).
    /// </summary>
    public static ColourReading ReadShown(Image master, in MetricDisk disk)
    {
        var shown = AsShown(master);
        try
        {
            return ReadColours(master, disk, shown, disk, new ReferencePlacement(1, 0, false, 0, 0, 1)).Reference;
        }
        finally
        {
            shown.Release();
        }
    }

    // A plane's values replaced by their rank over its own planet, 0 to 1: any monotone curve two pictures differ by is gone.
    private static float[] Equalised(ReadOnlySpan<float> plane, int width, int height, MetricDisk disk)
    {
        var values = new List<float>();
        foreach (var i in Region(width, height, disk, 1.0, rings: true))
        {
            if (float.IsFinite(plane[i]))
            {
                values.Add(plane[i]);
            }
        }
        var result = new float[plane.Length];
        if (values.Count < 2)
        {
            plane.CopyTo(result);
            return result;
        }
        values.Sort();
        var quantiles = new Quantiles(values);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = float.IsFinite(plane[i]) ? (float)quantiles.Of(plane[i]) : 0;
        }
        return result;
    }

    // The detail a registration is judged on, clear of the finest scale's noise and of the planet's own shading: a difference of Gaussians
    // 1 and 4 master pixels wide, `pixels` of the plane's own a master pixel.
    private static float[] Detail(ReadOnlySpan<float> plane, int width, int height, double pixels)
    {
        var source = plane.ToArray();
        var fine = Image.SeparableGaussianBlur(source, width, height, (float)pixels);
        var coarse = Image.SeparableGaussianBlur(source, width, height, (float)(4 * pixels));
        for (var i = 0; i < fine.Length; i++)
        {
            fine[i] -= coarse[i];
        }
        return fine;
    }

    // The pixels of the planet within the given radii: the globe alone, or its rings too (ClearRadiiAt), less any the reference does not reach,
    // and less the globe's limb (0.9 to 1.1 radii) when asked.
    private static int[] Region(int width, int height, MetricDisk disk, double radii, bool rings, ReadOnlySpan<float> reference = default,
        bool clearOfLimb = false)
    {
        var result = new List<int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var inside = rings ? disk.ClearRadiiAt(x, y) < radii : disk.RadiiAt(x, y) < radii && !disk.RingTouched(x, y);
                if (clearOfLimb && Math.Abs(disk.RadiiAt(x, y) - 1) <= 0.1)
                {
                    inside = false;
                }
                if (inside && (reference.IsEmpty || float.IsFinite(reference[i])))
                {
                    result.Add(i);
                }
            }
        }
        return [.. result];
    }

    // The rings off the globe, two pixels clear of the globe's limb and of the rings' outer edge, where the reference reaches.
    private static int[] RingRegion(int width, int height, MetricDisk disk, ReadOnlySpan<float> reference)
    {
        var result = new List<int>();
        var rings = disk.Rings ?? throw new ArgumentException("No rings to read.", nameof(disk));
        var margin = 2 / disk.Radius;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var rho = disk.RingPlaneRadiiAt(x, y);
                if (disk.RadiiAt(x, y) > 1 + margin && rho >= rings.InnerRadii && rho <= rings.OuterRadii - margin && float.IsFinite(reference[i]))
                {
                    result.Add(i);
                }
            }
        }
        return [.. result];
    }

    private static ReferencePlacement Refine(SearchContext context, ReferencePlacement best, Func<ReferencePlacement, double> get,
        Func<ReferencePlacement, double, ReferencePlacement> set, double reach, double step)
    {
        var centre = get(best);
        for (var v = centre - reach; v <= centre + reach + (step / 2); v += step)
        {
            var candidate = set(best, v);
            var c = context.Correlation(candidate);
            if (c > best.Correlation)
            {
                best = candidate with { Correlation = c };
            }
        }
        return best;
    }

    // Bilinear, NaN past the edge.
    private static float Sample(ReadOnlySpan<float> plane, int width, int height, double x, double y)
    {
        if (!(x >= 0 && y >= 0 && x <= width - 1 && y <= height - 1))
        {
            return float.NaN;
        }
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(x0 + 1, width - 1), Math.Min(y0 + 1, height - 1));
        var (fx, fy) = (x - x0, y - y0);
        var top = plane[(y0 * width) + x0] + (fx * (plane[(y0 * width) + x1] - plane[(y0 * width) + x0]));
        var bottom = plane[(y1 * width) + x0] + (fx * (plane[(y1 * width) + x1] - plane[(y1 * width) + x0]));
        return (float)(top + (fy * (bottom - top)));
    }

    // A value's rank among sorted values as a fraction, 0 to 1: each distinct value at its mid-rank (an 8-bit picture puts thousands of pixels
    // on one), a value between two read between their ranks, one past either end at that end.
    private sealed class Quantiles
    {
        private readonly List<float> _values = [];
        private readonly List<double> _ranks = [];

        public Quantiles(List<float> sorted)
        {
            for (var lo = 0; lo < sorted.Count;)
            {
                var hi = lo;
                while (hi + 1 < sorted.Count && sorted[hi + 1] == sorted[lo])
                {
                    hi++;
                }
                _values.Add(sorted[lo]);
                _ranks.Add((lo + hi) / 2.0 / Math.Max(1, sorted.Count - 1));
                lo = hi + 1;
            }
        }

        public double Of(float v)
        {
            var at = _values.BinarySearch(v);
            if (at >= 0)
            {
                return _ranks[at];
            }
            var above = ~at;
            if (above == 0)
            {
                return _ranks[0];
            }
            if (above >= _values.Count)
            {
                return _ranks[^1];
            }
            var (a, b) = (_values[above - 1], _values[above]);
            return _ranks[above - 1] + ((v - a) / (b - a) * (_ranks[above] - _ranks[above - 1]));
        }
    }

    // A master pixel's place in the reference.
    private readonly struct Mapping(MetricDisk masterDisk, MetricDisk referenceDisk, ReferencePlacement placement)
    {
        private readonly double _cos = Math.Cos(placement.RotationDeg * Math.PI / 180);
        private readonly double _sin = Math.Sin(placement.RotationDeg * Math.PI / 180);

        public (double X, double Y) At(double x, double y)
        {
            var (u, v) = ((x - masterDisk.X) / placement.Scale, (y - masterDisk.Y) / placement.Scale);
            if (placement.Mirrored)
            {
                u = -u;
            }
            return (referenceDisk.X + placement.ShiftX + (u * _cos) - (v * _sin), referenceDisk.Y + placement.ShiftY + (u * _sin) + (v * _cos));
        }
    }

    // The two detail planes and the master's planet, read at any placement.
    private sealed class SearchContext(float[] master, float[] reference, int width, int referenceWidth, int referenceHeight, int[] region,
        MetricDisk masterDisk, MetricDisk referenceDisk)
    {
        public double Correlation(in ReferencePlacement placement)
        {
            var map = new Mapping(masterDisk, referenceDisk, placement);
            double sm = 0, sr = 0, smm = 0, srr = 0, smr = 0;
            var n = 0;
            foreach (var i in region)
            {
                var (u, v) = map.At(i % width, i / width);
                var rv = Sample(reference, referenceWidth, referenceHeight, u, v);
                if (!float.IsFinite(rv))
                {
                    continue;
                }
                double a = master[i], b = rv;
                sm += a;
                sr += b;
                smm += a * a;
                srr += b * b;
                smr += a * b;
                n++;
            }
            if (n < region.Length / 2)
            {
                return double.NegativeInfinity;
            }
            var cov = smr - (sm * sr / n);
            var vm = smm - (sm * sm / n);
            var vr = srr - (sr * sr / n);
            return vm > 0 && vr > 0 ? cov / Math.Sqrt(vm * vr) : double.NegativeInfinity;
        }
    }
}
