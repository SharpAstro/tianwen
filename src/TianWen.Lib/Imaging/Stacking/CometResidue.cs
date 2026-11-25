using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using nom.tam.fits;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// What a comet run leaves around the body that is not round, read off its star layer and its composite (#2489): the
/// coloured ring and the half-ring, each in units of each channel's own sky noise.
/// </summary>
/// <remarks>
/// <para><b>The coloured ring</b> is trail residue. A star remover handed a raw Bayer mosaic leaves a channel-lopsided
/// residue on every star it takes (red up, green down: magenta), and on the comet grid that residue of each bright star
/// lies along the track in a few frames of many. Where the comet layer's rejection does not take it out, it stays in
/// the model, is subtracted from the star layer at every frame's body position (a green streak along the track beside
/// each bright star) and added back once at the reference (a magenta one): a green and magenta pair at every bright
/// star inside the coma. On 10P on 2026-11-25 a Bayer-drizzled comet layer showed it plainly where the August
/// composites, stacked debayered with a sigma clip, did not. <b>The dark streak</b> is the same residue in luminance: a
/// model built from the comet master less its own star removal holds trail flux, and subtracted at every frame's body
/// position it digs a streak beside each bright star (the comet plan's "third row is the trap").</para>
/// <para><b>Both are read along the track.</b> Each plane is detrended by its own annular median round the body (so the
/// coma and the sky are gone and only what is not round is left), the star cores are masked (a star is not residue),
/// and chroma (green less the mean of red and blue) and luminance are averaged over a line along the drift: every trail
/// on the comet grid lies along it, so a trail keeps its level while the noise falls by the line's root. The extremes of
/// that line (0.01th and 99.99th percentiles) are the reading. On 10P (sky sigma units): the dark streak read
/// <b>-3.69</b> in the star layer's luminance where the clean run read -0.92, and the ring read <b>+4.88</b> in the
/// star layer's chroma (its green trails) and <b>-7.94</b> in the model's (the magenta added back) where the run without
/// it read +1.32 and -1.82. A broad percentile over the disc did not separate them: a trail is a few hundred pixels of a
/// third of a million.</para>
/// <para><b>The half-ring</b> is a slope in the model: the field's gradient carried into the crop, a dipole cut hard at
/// the reach, which subtracted along the track and added back once paints its bright half up to an edge (SWAN, August
/// 2026; <c>CometModel.RemoveBackgroundPlane</c> took it out). An annular profile is zero at the reach by construction
/// and cannot see it; the first harmonic of the model's sector medians at its edge can, which is the statistic the
/// comet plan settled on (docs/plans/comet-integration.md, "The 'reflection halo' round SWAN was the field's slope
/// inside the model").</para>
/// <para>Ported from the session script that measured both on 10P (<c>ring_probe.py</c>, 2026-11-25), so the reading
/// that judged the result is the one that guards it.</para>
/// </remarks>
public static class CometResidue
{
    /// <summary>Pixels nearer the body than this are left out: the nucleus and the raw core spliced into the model.</summary>
    public const float InnerRadiusPx = 25f;

    /// <summary>A pixel of the star layer this far above its detrended level, in sky sigma, is a star's core.</summary>
    public const float StarCoreSigma = 8f;

    /// <summary>How far a star core is grown, in pixels (city-block), before it is masked.</summary>
    public const int StarCoreGrowPx = 5;

    /// <summary>The sectors the model's edge is read in.</summary>
    public const int EdgeSectors = 24;

    /// <summary>How wide the model's edge annulus is, inward from the reach, in pixels.</summary>
    public const float EdgeWidthPx = 20f;

    /// <summary>Half the length of the line along the drift the residue is averaged over, in pixels.</summary>
    public const int TrackHalfLengthPx = 10;

    /// <summary>One plate's residue along the track round the body, in sky sigma: the extremes (0.01th and 99.99th
    /// percentiles) of chroma (green less the mean of red and blue) and of luminance, each averaged along the drift.</summary>
    public readonly record struct Track(float ChromaLow, float ChromaHigh, float LuminanceLow, float LuminanceHigh);

    /// <summary>One reading: each channel's sky noise in the star layer's units, how many pixels were read and how many
    /// were masked as star cores, the residue along the track of the star layer and of the model (the composite less the
    /// star layer, which is exactly what was added back), and the first harmonic of the model's sector medians at its
    /// edge per channel, in sky sigma.</summary>
    public sealed record Reading(ImmutableArray<float> SkySigma, int PixelsRead, int StarCorePixels, Track StarLayer, Track Model, ImmutableArray<float> EdgeHarmonic);

    /// <summary>
    /// Reads the residue of a comet run round <paramref name="body"/>, out to <paramref name="reachPx"/>.
    /// </summary>
    /// <param name="starLayer">The star layer, colour.</param>
    /// <param name="composite">The composite, on the star layer's canvas.</param>
    /// <param name="body">The body on the star canvas, 0-based pixels (<see cref="BodyOf"/>, or the run log's "body placed at").</param>
    /// <param name="drift">The body's drift on the canvas (the composite's <c>DRIFTX</c>, <c>DRIFTY</c>); only its direction is used.</param>
    /// <param name="reachPx">How far out to read; the model's reach, or the coma's.</param>
    public static Reading Measure(Image starLayer, Image composite, Vector2 body, Vector2 drift, float reachPx)
    {
        ArgumentNullException.ThrowIfNull(starLayer);
        ArgumentNullException.ThrowIfNull(composite);
        if (starLayer.ChannelCount < 3 || composite.ChannelCount != starLayer.ChannelCount
            || composite.Width != starLayer.Width || composite.Height != starLayer.Height)
        {
            throw new ArgumentException("a colour star layer and a composite on its canvas are read", nameof(composite));
        }

        if (!(reachPx > InnerRadiusPx + EdgeWidthPx))
        {
            throw new ArgumentOutOfRangeException(nameof(reachPx), reachPx, $"the reach is past {InnerRadiusPx + EdgeWidthPx} px");
        }

        if (!(drift.LengthSquared() > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(drift), drift, "the drift has a direction: a comet run states it as DRIFTX, DRIFTY");
        }

        var half = (int)reachPx + 40;
        var x0 = Math.Max(0, (int)MathF.Round(body.X) - half);
        var y0 = Math.Max(0, (int)MathF.Round(body.Y) - half);
        var x1 = Math.Min(starLayer.Width, (int)MathF.Round(body.X) + half);
        var y1 = Math.Min(starLayer.Height, (int)MathF.Round(body.Y) + half);
        var (w, h) = (x1 - x0, y1 - y0);
        var n = w * h;

        var radius = new float[n];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                radius[y * w + x] = Vector2.Distance(new Vector2(x0 + x, y0 + y), body);
            }
        }

        // Each channel's sky noise from the window's outer ring, in the star layer.
        var sigma = new float[3];
        var star = new float[3][];
        var model = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            var starSpan = starLayer.GetChannelSpan(c);
            var compSpan = composite.GetChannelSpan(c);
            star[c] = new float[n];
            model[c] = new float[n];
            for (var y = 0; y < h; y++)
            {
                var row = (y0 + y) * starLayer.Width + x0;
                for (var x = 0; x < w; x++)
                {
                    var s = starSpan[row + x];
                    star[c][y * w + x] = s;
                    model[c][y * w + x] = compSpan[row + x] - s;
                }
            }

            var far = Select(star[c], radius, reachPx - EdgeWidthPx, float.MaxValue);
            var (_, mad) = StatisticsHelper.MedianAndMad(far);
            sigma[c] = 1.4826f * mad;
            if (!(sigma[c] > 0))
            {
                throw new ArgumentException($"channel {c} has no sky noise round the body to read in", nameof(starLayer));
            }
        }

        var bins = new int[n];
        for (var i = 0; i < n; i++)
        {
            bins[i] = (int)(radius[i] / 2f);
        }

        var binCount = (int)(MathF.Sqrt(2f) * half / 2f) + 2;
        var starResidue = new float[3][];
        var modelResidue = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            starResidue[c] = Detrend(star[c], bins, binCount, sigma[c]);
            modelResidue[c] = Detrend(model[c], bins, binCount, sigma[c]);
        }

        // Star cores: the star layer's detrended luminance above StarCoreSigma, grown by StarCoreGrowPx.
        var core = new bool[n];
        for (var i = 0; i < n; i++)
        {
            core[i] = (starResidue[0][i] + starResidue[1][i] + starResidue[2][i]) / 3f > StarCoreSigma;
        }

        var masked = Grow(core, w, h, StarCoreGrowPx);
        var keep = new bool[n];
        var kept = 0;
        var cores = 0;
        for (var i = 0; i < n; i++)
        {
            var inside = radius[i] >= InnerRadiusPx && radius[i] <= reachPx;
            keep[i] = inside && !masked[i];
            kept += keep[i] ? 1 : 0;
            cores += inside && masked[i] ? 1 : 0;
        }

        var edge = ImmutableArray.CreateBuilder<float>(3);
        for (var c = 0; c < 3; c++)
        {
            edge.Add(EdgeHarmonic(modelResidue[c], radius, masked, x0, y0, w, body, reachPx));
        }

        var direction = Vector2.Normalize(drift);
        return new Reading(
            [.. sigma], kept, cores,
            TrackOf(starResidue, masked, keep, w, h, direction), TrackOf(modelResidue, masked, keep, w, h, direction),
            edge.MoveToImmutable());
    }

    /// <summary>
    /// Reads a finished run from its files: the star layer and the composite written beside it, the body found in them
    /// (<see cref="BodyOf"/>) and the drift taken from the composite's own header (<c>DRIFTX</c>, <c>DRIFTY</c>).
    /// </summary>
    /// <exception cref="IOException">A file cannot be read as a FITS image.</exception>
    /// <exception cref="InvalidDataException">The composite states no drift: it is no comet composite.</exception>
    public static Reading MeasureFiles(string starLayerPath, string compositePath, float reachPx)
    {
        if (!Image.TryReadFitsFile(starLayerPath, out var star))
        {
            throw new IOException($"{starLayerPath} cannot be read as a FITS image");
        }

        if (!Image.TryReadFitsFile(compositePath, out var composite))
        {
            throw new IOException($"{compositePath} cannot be read as a FITS image");
        }

        Vector2? drift;
        using (var fits = Image.OpenFitsHeader(compositePath))
        {
            drift = fits.ReadFirstImageHduHeaderOnly()?.Header is { } header ? AlignmentProvenance.FromHeader(header)?.DriftPxPerHour : null;
        }

        if (drift is not { } direction)
        {
            throw new InvalidDataException($"{compositePath} states no drift (DRIFTX, DRIFTY): it is no comet composite");
        }

        return Measure(star, composite, BodyOf(star, composite), direction, reachPx);
    }

    /// <summary>
    /// Where the body is on the star canvas, read off the two files: the model (the composite less the star layer) is
    /// the body added back once, and it peaks at the nucleus. The peak of the channels' sum over a 5x5 box, refined by
    /// the centroid of that box. A composite states its drift but not where it put the body, so a reading of a finished
    /// run finds it here.
    /// </summary>
    public static Vector2 BodyOf(Image starLayer, Image composite)
    {
        ArgumentNullException.ThrowIfNull(starLayer);
        ArgumentNullException.ThrowIfNull(composite);
        var (w, h) = (starLayer.Width, starLayer.Height);
        var sum = new float[w * h];
        for (var c = 0; c < Math.Min(3, starLayer.ChannelCount); c++)
        {
            var s = starLayer.GetChannelSpan(c);
            var m = composite.GetChannelSpan(c);
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += m[i] - s[i];
            }
        }

        var (bestX, bestY, best) = (-1, -1, float.MinValue);
        for (var y = 2; y < h - 2; y++)
        {
            for (var x = 2; x < w - 2; x++)
            {
                var box = 0f;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        box += sum[(y + dy) * w + x + dx];
                    }
                }

                if (box > best)
                {
                    (bestX, bestY, best) = (x, y, box);
                }
            }
        }

        if (bestX < 0)
        {
            throw new ArgumentException("the composite adds nothing to its star layer: there is no body to find", nameof(composite));
        }

        double weight = 0, sx = 0, sy = 0;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var v = Math.Max(0f, sum[(bestY + dy) * w + bestX + dx]);
                weight += v;
                sx += v * (bestX + dx);
                sy += v * (bestY + dy);
            }
        }

        return weight > 0 ? new Vector2((float)(sx / weight), (float)(sy / weight)) : new Vector2(bestX, bestY);
    }

    // Each value less the median of its 2 px annulus round the body, in sky sigma.
    private static float[] Detrend(float[] plane, int[] bins, int binCount, float sigma)
    {
        var counts = new int[binCount];
        foreach (var b in bins)
        {
            counts[Math.Min(b, binCount - 1)]++;
        }

        var members = new float[binCount][];
        for (var b = 0; b < binCount; b++)
        {
            members[b] = new float[counts[b]];
        }

        var fill = new int[binCount];
        for (var i = 0; i < plane.Length; i++)
        {
            var b = Math.Min(bins[i], binCount - 1);
            members[b][fill[b]++] = plane[i];
        }

        var medians = new float[binCount];
        for (var b = 0; b < binCount; b++)
        {
            medians[b] = members[b].Length == 0 ? 0f : StatisticsHelper.MedianFast(members[b].AsSpan());
        }

        var residue = new float[plane.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            residue[i] = (plane[i] - medians[Math.Min(bins[i], binCount - 1)]) / sigma;
        }

        return residue;
    }

    // City-block growth by passes of the four neighbours, as a cross-shaped dilation repeated.
    private static bool[] Grow(bool[] seed, int w, int h, int passes)
    {
        var current = seed.AsSpan().ToArray();
        for (var p = 0; p < passes; p++)
        {
            var next = current.AsSpan().ToArray();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (!current[y * w + x])
                    {
                        continue;
                    }

                    if (x > 0)
                    {
                        next[y * w + x - 1] = true;
                    }

                    if (x < w - 1)
                    {
                        next[y * w + x + 1] = true;
                    }

                    if (y > 0)
                    {
                        next[(y - 1) * w + x] = true;
                    }

                    if (y < h - 1)
                    {
                        next[(y + 1) * w + x] = true;
                    }
                }
            }

            current = next;
        }

        return current;
    }

    // Chroma and luminance averaged over the line of 2 * TrackHalfLengthPx + 1 px along the drift through each pixel,
    // star cores left out of every line, and the extremes of both over the pixels read.
    private static Track TrackOf(float[][] residue, bool[] masked, bool[] keep, int w, int h, Vector2 direction)
    {
        var n = w * h;
        var chroma = new float[n];
        var luminance = new float[n];
        for (var i = 0; i < n; i++)
        {
            if (masked[i])
            {
                continue;
            }

            chroma[i] = residue[1][i] - 0.5f * (residue[0][i] + residue[2][i]);
            luminance[i] = (residue[0][i] + residue[1][i] + residue[2][i]) / 3f;
        }

        var steps = new (int Dx, int Dy)[2 * TrackHalfLengthPx + 1];
        for (var k = -TrackHalfLengthPx; k <= TrackHalfLengthPx; k++)
        {
            steps[k + TrackHalfLengthPx] = ((int)MathF.Round(k * direction.X), (int)MathF.Round(k * direction.Y));
        }

        var count = 0;
        foreach (var k in keep)
        {
            count += k ? 1 : 0;
        }

        var chromaLine = new float[count];
        var luminanceLine = new float[count];
        var j = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!keep[y * w + x])
                {
                    continue;
                }

                float sumChroma = 0f, sumLuminance = 0f;
                var used = 0;
                foreach (var (dx, dy) in steps)
                {
                    var (sx, sy) = (x + dx, y + dy);
                    if (sx < 0 || sx >= w || sy < 0 || sy >= h || masked[sy * w + sx])
                    {
                        continue;
                    }

                    sumChroma += chroma[sy * w + sx];
                    sumLuminance += luminance[sy * w + sx];
                    used++;
                }

                chromaLine[j] = used == 0 ? 0f : sumChroma / used;
                luminanceLine[j] = used == 0 ? 0f : sumLuminance / used;
                j++;
            }
        }

        Array.Sort(chromaLine);
        Array.Sort(luminanceLine);
        return new Track(
            Percentile(chromaLine, 0.01), Percentile(chromaLine, 99.99),
            Percentile(luminanceLine, 0.01), Percentile(luminanceLine, 99.99));
    }

    // The first harmonic of the sector medians over the edge annulus [reach - EdgeWidthPx, reach], star cores out:
    // (2 / N) |sum m_k exp(-i theta_k)|, in the plane's units (sky sigma here). A round model reads near zero; a slope
    // cut at the reach reads its dipole.
    private static float EdgeHarmonic(float[] plane, float[] radius, bool[] masked, int x0, int y0, int w, Vector2 body, float reachPx)
    {
        var sectors = new List<float>[EdgeSectors];
        for (var s = 0; s < EdgeSectors; s++)
        {
            sectors[s] = [];
        }

        for (var i = 0; i < plane.Length; i++)
        {
            if (masked[i] || radius[i] < reachPx - EdgeWidthPx || radius[i] > reachPx)
            {
                continue;
            }

            var (x, y) = (x0 + i % w, y0 + i / w);
            var angle = MathF.Atan2(y - body.Y, x - body.X);
            var s = (int)((angle + MathF.PI) / (2f * MathF.PI) * EdgeSectors) % EdgeSectors;
            sectors[s].Add(plane[i]);
        }

        double re = 0, im = 0;
        var used = 0;
        for (var s = 0; s < EdgeSectors; s++)
        {
            if (sectors[s].Count == 0)
            {
                continue;
            }

            var values = sectors[s].ToArray();
            var median = StatisticsHelper.MedianFast(values.AsSpan());
            var theta = -Math.PI + (s + 0.5) * 2 * Math.PI / EdgeSectors;
            re += median * Math.Cos(theta);
            im -= median * Math.Sin(theta);
            used++;
        }

        return used == 0 ? 0f : (float)(2.0 / used * Math.Sqrt(re * re + im * im));
    }

    // Linear interpolation between the order statistics, as numpy's default percentile does.
    private static float Percentile(float[] sorted, double percent)
    {
        if (sorted.Length == 0)
        {
            return float.NaN;
        }

        var position = percent / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var t = (float)(position - lower);
        return sorted[lower] + t * (sorted[upper] - sorted[lower]);
    }

    // The plane's values whose radius is in (from, to].
    private static Span<float> Select(float[] plane, float[] radius, float from, float to)
    {
        var count = 0;
        foreach (var r in radius)
        {
            count += r > from && r <= to ? 1 : 0;
        }

        var values = new float[count];
        var j = 0;
        for (var i = 0; i < plane.Length; i++)
        {
            if (radius[i] > from && radius[i] <= to)
            {
                values[j++] = plane[i];
            }
        }

        return values;
    }
}
