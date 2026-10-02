using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The field's mean departure from its Moffat, as a fraction of a star's amplitude by distance from its centre: the
/// residual table of an analytic-plus-empirical PSF (DAOPHOT's design, radial here). A real star is not one Moffat: a core
/// and a halo that one beta cannot both follow left every bright star of the first ten masters with an over-subtracted
/// core (a hole) inside an under-subtracted halo (a ring). Measured on bright, isolated, unsaturated stars against a sky
/// taken beyond the table's reach, so a halo the fit's own sky would have absorbed is in it. A star of another width reads
/// it at its distance scaled by the width.
/// </summary>
internal sealed class RadialCorrection
{
    private readonly float[] _table;

    /// <summary>The table's bin width, pixels at the field's width.</summary>
    public const double BinWidth = 0.25;

    /// <summary>The alpha the table was measured at; a star of alpha a reads it at distance times this over a.</summary>
    public double ReferenceAlpha { get; }

    /// <summary>The table's reach, pixels at the field's width; beyond it the correction is zero.</summary>
    public double Reach => _table.Length * BinWidth;

    /// <summary>Stars the table was measured on.</summary>
    public int Stars { get; }

    private RadialCorrection(float[] table, double referenceAlpha, int stars)
    {
        _table = table;
        ReferenceAlpha = referenceAlpha;
        Stars = stars;
    }

    /// <summary>The correction at <paramref name="distance"/> pixels from a star of alpha <paramref name="alpha"/>.</summary>
    public double At(double distance, double alpha)
    {
        var scaled = distance * ReferenceAlpha / alpha;
        var bin = (int)(scaled / BinWidth);
        if (bin >= _table.Length)
        {
            return 0.0;
        }
        // Linear between bin centres.
        var position = scaled / BinWidth - 0.5;
        var i0 = Math.Clamp((int)Math.Floor(position), 0, _table.Length - 1);
        var i1 = Math.Min(i0 + 1, _table.Length - 1);
        var t = Math.Clamp(position - i0, 0.0, 1.0);
        return (1 - t) * _table[i0] + t * _table[i1];
    }

    /// <summary>A peak of 1 with the table added: what <paramref name="psf"/> times an amplitude becomes at a pixel.</summary>
    public double Model(MoffatPsf psf, int px, int py, double cx, double cy)
    {
        var dx = px - cx;
        var dy = py - cy;
        return psf.PixelMean(px, py, cx, cy) + At(Math.Sqrt(dx * dx + dy * dy), psf.Alpha);
    }

    /// <summary>
    /// Builds the table from pooled samples (distance at the field's width, residual over amplitude): the median per bin,
    /// a three-bin running median over it, zero where a bin has under <paramref name="minSamples"/>, tapered to zero over
    /// the last fifth of the reach so the model has no step where the table ends. Null when too few stars.
    /// </summary>
    public static RadialCorrection? Build(List<(float Distance, float Residual)> samples, double reach, double referenceAlpha, int stars, int minStars = 20, int minSamples = 30)
    {
        if (stars < minStars || samples.Count == 0)
        {
            return null;
        }
        var bins = (int)Math.Ceiling(reach / BinWidth);
        var buckets = new List<float>[bins];
        foreach (var (d, r) in samples)
        {
            var b = (int)(d / BinWidth);
            if (b < bins && float.IsFinite(r))
            {
                (buckets[b] ??= new List<float>()).Add(r);
            }
        }
        // A bin is kept only where its median is significant, three times its own standard error (1.2533 times the
        // MAD-sigma over root n): a star many times brighter than the ones the table was measured on multiplies whatever
        // the table holds, so its noise must not be in it (a 20,000-sigma star on a field that IS a Moffat was left a
        // 7-sigma ring by a table of noise alone).
        var medians = new float[bins];
        for (var b = 0; b < bins; b++)
        {
            if (buckets[b] is { Count: var count } list && count >= minSamples)
            {
                list.Sort();
                var median = list[count / 2];
                var deviations = new float[count];
                for (var k = 0; k < count; k++)
                {
                    deviations[k] = Math.Abs(list[k] - median);
                }
                Array.Sort(deviations);
                var standardError = 1.2533 * 1.4826 * deviations[count / 2] / Math.Sqrt(count);
                medians[b] = Math.Abs(median) > 3.0 * standardError ? median : 0f;
            }
        }
        var table = new float[bins];
        for (var b = 0; b < bins; b++)
        {
            var window = new List<float>(3);
            for (var k = Math.Max(0, b - 1); k <= Math.Min(bins - 1, b + 1); k++)
            {
                window.Add(medians[k]);
            }
            window.Sort();
            var taper = Math.Clamp((bins - b) / (0.2 * bins), 0.0, 1.0);
            table[b] = (float)(window[window.Count / 2] * taper);
        }
        return new RadialCorrection(table, referenceAlpha, stars);
    }
}
