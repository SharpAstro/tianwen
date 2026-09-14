using System;
using System.Collections.Generic;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Sources;

/// <summary>
/// Options for <see cref="EdgeSpreadProfile.Measure"/>.
/// </summary>
/// <param name="Sectors">Angular sectors round the segment's centroid; each gets its own radial profile
/// and its own reading, and the medians over the sectors are reported. A segment that is neither round
/// nor evenly bright reads its shape as width if the profile is taken all round at once.</param>
/// <param name="Reach">How far past the segment's own boundary the profile extends, in pixels, for the
/// outside dip and the far level.</param>
/// <param name="MinContrastSigma">A sector is read only when its rim or edge stands this many exterior
/// sigmas over the base it is measured from.</param>
/// <param name="StarMaskMargin">Stars from the segmentation (compact segments) are masked out of the
/// profile with this margin, so a star on the rim does not become the rim's peak.</param>
public sealed record EdgeSpreadOptions(int Sectors = 36, int Reach = 60, float MinContrastSigma = 3f, int StarMaskMargin = 3)
{
    public static EdgeSpreadOptions Default { get; } = new EdgeSpreadOptions();

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Sectors, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(Reach, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(MinContrastSigma);
        ArgumentOutOfRangeException.ThrowIfNegative(StarMaskMargin);
    }
}

/// <summary>
/// The extended-object measurement: an extended segment's boundary read as a line-spread function.
/// </summary>
/// <param name="Label">The segment measured.</param>
/// <param name="SectorsRead">Sectors that had a readable rim or edge (of <see cref="EdgeSpreadOptions.Sectors"/>).</param>
/// <param name="RimWidth">Median over sectors of the FWHM in radius of a rim (a profile that peaks near the
/// boundary, a shell); NaN when no sector has a rim.</param>
/// <param name="RimContrast">Median rim height over the higher of the interior and the far level, in the plane's units.</param>
/// <param name="EdgeWidth">Median over sectors of the 10 to 90 percent width, in pixels, of the profile's fall
/// from the interior level to the far level across the boundary (a filled object's edge).</param>
/// <param name="OutsideDipSigma">Median over sectors of the profile's minimum just outside the boundary
/// against the far level, in exterior-noise sigmas. Under about -2 is ringing.</param>
/// <param name="NoiseOutside">MAD-derived sigma of the 7 px high-passed plane in the reach beyond the boundary.</param>
/// <param name="NoiseInside">The same inside the segment.</param>
public readonly record struct EdgeSpreadReading(
    int Label,
    int SectorsRead,
    float RimWidth,
    float RimContrast,
    float EdgeWidth,
    float OutsideDipSigma,
    float NoiseOutside,
    float NoiseInside);

/// <summary>
/// A no-reference sharpness reading for extended sources. A nebula has no sharper twin the way a star
/// field's other half is, so its own boundary serves: a shell (the Bubble Nebula's rim) is a line spread
/// function whose FWHM in radius reads the blur, and a filled object's edge is an edge spread function
/// whose 10 to 90 percent width reads it; the dip just outside reads ringing, and the high-pass noise
/// on either side reads amplification where there is no edge.
/// </summary>
/// <remarks>
/// Ported from the Python readout that measured the deconvolver on the Bubble
/// (<c>training/denoise/n2n_rim_readout.py</c>), generalised from a fitted circle to the segment's own
/// boundary: each sector's ray runs from the centroid outward, the boundary is the last pixel carrying
/// the segment's label along it, and the profile is the median over the sector's pixels per 1 px annulus.
/// The first reading on the Bubble (a whole-ring azimuthal median) read the shell's ellipticity as 48
/// px of width; per sector it read 22 px, which is the shell.
/// </remarks>
public static class EdgeSpreadProfile
{
    public static EdgeSpreadReading Measure(ReadOnlySpan<float> plane, int width, int height, SegmentationMap segmentation, int label, EdgeSpreadOptions? options = null)
    {
        options ??= EdgeSpreadOptions.Default;
        options.Validate();
        if (plane.Length != width * height)
        {
            throw new ArgumentException($"plane has {plane.Length} samples for {width}x{height}", nameof(plane));
        }

        if (segmentation.Width != width || segmentation.Height != height)
        {
            throw new ArgumentException("the segmentation map is not the plane's size", nameof(segmentation));
        }

        if (label < 1 || label > segmentation.Segments.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(label), label, "no such segment");
        }

        var segment = segmentation.Segments[label - 1];
        var cx = segment.XCentroid;
        var cy = segment.YCentroid;
        var reach = options.Reach;
        var x0 = Math.Max(0, segment.X0 - reach);
        var y0 = Math.Max(0, segment.Y0 - reach);
        var x1 = Math.Min(width - 1, segment.X1 + reach);
        var y1 = Math.Min(height - 1, segment.Y1 + reach);
        var w = x1 - x0 + 1;
        var h = y1 - y0 + 1;

        // The window, its star mask (compact segments with a margin), its labels, and a 7 px high-pass.
        var win = new float[w * h];
        var masked = new bool[w * h];
        var isSegment = new bool[w * h];
        var labels = segmentation.Labels;
        var compact = new bool[segmentation.Segments.Length + 1];
        foreach (var s in segmentation.Segments)
        {
            compact[s.Label] = s.IsCompact;
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var gi = (y + y0) * width + (x + x0);
                var v = plane[gi];
                win[y * w + x] = float.IsFinite(v) ? v : float.NaN;
                masked[y * w + x] = !float.IsFinite(v) || compact[labels[gi]];
                isSegment[y * w + x] = labels[gi] == label;
            }
        }

        if (options.StarMaskMargin > 0)
        {
            MaskOps.DilateSquare(masked, w, h, options.StarMaskMargin);
        }

        var hp = HighPass7(win, w, h);
        var noiseOut = NoiseOf(hp, isSegment, masked, inside: false);
        var noiseIn = NoiseOf(hp, isSegment, masked, inside: true);

        var lx = cx - x0;
        var ly = cy - y0;
        var rims = new List<float>();
        var rimContrasts = new List<float>();
        var edges = new List<float>();
        var dips = new List<float>();
        var maxR = MathF.Sqrt(w * (float)w + h * (float)h);
        var profile = new float[(int)maxR + 2];
        var counts = new List<float>?[(int)maxR + 2];
        for (var k = 0; k < options.Sectors; k++)
        {
            var t0 = -MathF.PI + k * 2f * MathF.PI / options.Sectors;
            var t1 = t0 + 2f * MathF.PI / options.Sectors;
            var boundary = -1f;
            Array.Clear(counts);

            // Gather the sector's pixels per 1 px annulus; the boundary is the outermost annulus with segment pixels.
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var dx = x - lx;
                    var dy = y - ly;
                    var theta = MathF.Atan2(dy, dx);
                    if (theta < t0 || theta >= t1)
                    {
                        continue;
                    }

                    var i = y * w + x;
                    var r = MathF.Sqrt(dx * dx + dy * dy);
                    var bin = (int)r;
                    if (isSegment[i] && r > boundary)
                    {
                        boundary = r;
                    }

                    if (masked[i] || bin >= profile.Length)
                    {
                        continue;
                    }

                    (counts[bin] ??= new List<float>()).Add(win[i]);
                }
            }

            if (boundary < 3f)
            {
                continue;
            }

            for (var i = 0; i < profile.Length; i++)
            {
                profile[i] = counts[i] is { Count: > 2 } list ? StatisticsHelper.MedianFast(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)) : float.NaN;
            }

            var rb = (int)boundary;
            var far = MedianRange(profile, rb + reach / 2, rb + reach);
            var inner = MedianRange(profile, (int)(rb * 0.4f), (int)(rb * 0.7f));
            if (float.IsNaN(far))
            {
                continue;
            }

            // Rim: the profile's peak within 25 px inside the boundary, over the higher base.
            var ip = -1;
            var peak = float.NegativeInfinity;
            for (var i = Math.Max(1, rb - 25); i <= rb + 2 && i < profile.Length; i++)
            {
                if (profile[i] > peak)
                {
                    peak = profile[i];
                    ip = i;
                }
            }

            var basis = float.IsNaN(inner) ? far : MathF.Max(far, inner);
            if (ip > 0 && peak - basis >= options.MinContrastSigma * noiseOut && (float.IsNaN(inner) || peak > inner + 2f * noiseOut))
            {
                var half = basis + 0.5f * (peak - basis);
                var lo = ip;
                while (lo > 0 && profile[lo] > half)
                {
                    lo--;
                }

                var hi = ip;
                while (hi < profile.Length - 1 && profile[hi] > half)
                {
                    hi++;
                }

                rims.Add(hi - lo);
                rimContrasts.Add(peak - basis);
            }

            // Edge: the 10 to 90 percent fall from the interior level to the far level across the boundary.
            if (!float.IsNaN(inner) && inner - far >= options.MinContrastSigma * noiseOut)
            {
                var p90 = far + 0.9f * (inner - far);
                var p10 = far + 0.1f * (inner - far);
                var r90 = -1;
                var r10 = -1;
                for (var i = (int)(rb * 0.7f); i < Math.Min(profile.Length, rb + reach); i++)
                {
                    if (float.IsNaN(profile[i]))
                    {
                        continue;
                    }

                    if (r90 < 0 && profile[i] <= p90)
                    {
                        r90 = i;
                    }

                    if (r90 >= 0 && profile[i] <= p10)
                    {
                        r10 = i;
                        break;
                    }
                }

                if (r90 >= 0 && r10 > r90)
                {
                    edges.Add(r10 - r90);
                }
            }

            // Outside dip against the far level, in exterior sigmas.
            var dipMin = float.PositiveInfinity;
            for (var i = rb + 3; i < Math.Min(profile.Length, rb + reach / 2) ; i++)
            {
                if (!float.IsNaN(profile[i]) && profile[i] < dipMin)
                {
                    dipMin = profile[i];
                }
            }

            if (float.IsFinite(dipMin) && noiseOut > 0f)
            {
                dips.Add((dipMin - far) / noiseOut);
            }
        }

        return new EdgeSpreadReading(
            label,
            Math.Max(rims.Count, edges.Count),
            Median(rims),
            Median(rimContrasts),
            Median(edges),
            Median(dips),
            noiseOut,
            noiseIn);
    }

    private static float MedianRange(float[] profile, int from, int to)
    {
        var buf = new List<float>();
        for (var i = Math.Max(0, from); i < Math.Min(profile.Length, to); i++)
        {
            if (!float.IsNaN(profile[i]))
            {
                buf.Add(profile[i]);
            }
        }

        return Median(buf);
    }

    private static float Median(List<float> values)
        => values.Count == 0 ? float.NaN : StatisticsHelper.MedianFast(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));

    private static float[] HighPass7(float[] win, int w, int h)
    {
        var hp = new float[win.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0.0;
                var n = 0;
                for (var dy = -3; dy <= 3; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= h)
                    {
                        continue;
                    }

                    for (var dx = -3; dx <= 3; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= w)
                        {
                            continue;
                        }

                        var v = win[yy * w + xx];
                        if (!float.IsNaN(v))
                        {
                            sum += v;
                            n++;
                        }
                    }
                }

                var c = win[y * w + x];
                hp[y * w + x] = n > 0 && !float.IsNaN(c) ? c - (float)(sum / n) : float.NaN;
            }
        }

        return hp;
    }

    private static float NoiseOf(float[] hp, bool[] isSegment, bool[] masked, bool inside)
    {
        var buf = new List<float>();
        for (var i = 0; i < hp.Length; i++)
        {
            if (masked[i] || float.IsNaN(hp[i]) || isSegment[i] != inside)
            {
                continue;
            }

            buf.Add(hp[i]);
        }

        if (buf.Count < 16)
        {
            return float.NaN;
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf);
        var (_, mad) = StatisticsHelper.MedianAndMad(span);
        return StatisticsHelper.MAD_TO_SD * mad;
    }
}
