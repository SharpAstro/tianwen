using System;
using System.Collections.Generic;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>A star's shape fitted back off an image (<see cref="InjectionMeasure.FitMoffat"/>).</summary>
/// <param name="Amplitude">The peak, in the plane's units.</param>
/// <param name="X">The centre.</param>
/// <param name="Y">The centre.</param>
/// <param name="FwhmPx">The FWHM, the geometric mean of the two axes' (<see cref="StarProfile"/>'s convention).</param>
/// <param name="Beta">The Moffat exponent.</param>
/// <param name="AxisRatio">Minor over major.</param>
/// <param name="PositionAngleRad">The major axis's angle from +x.</param>
/// <param name="Converged">Whether the fit converged inside its budget.</param>
public readonly record struct StarShapeFit(
    double Amplitude, double X, double Y, double FwhmPx, double Beta, double AxisRatio, double PositionAngleRad, bool Converged);

/// <summary>A saturated star's plateau and edge (<see cref="InjectionMeasure.SaturatedShape"/>).</summary>
/// <param name="PlateauPx">Pixels within 2 percent of the core's maximum above the sky, inside 30 px.</param>
/// <param name="EdgePx">The radial distance from the 90 to the 50 percent level of the core above the sky, in the
/// azimuthal median profile at half-pixel steps: a hard clip of a PSF has a sharp edge, a stack of clipped subs a softer
/// one.</param>
public readonly record struct SaturatedStarShape(int PlateauPx, double EdgePx);

/// <summary>
/// The measures R1's predictions are read with (docs/plans/star-remover-training.md, "R1: the injector"): an injected star's
/// shape fitted back off the draw minus the plate, and a saturated star's plateau and edge, read the same way off a
/// master's real saturated stars and off the injected ones, so the two are one definition.
/// </summary>
public static class InjectionMeasure
{
    /// <summary>The window a saturated star is read in, and how far from the frame's edge it must be.</summary>
    public const int SaturatedWindowPx = 40;

    private const double SaturatedSkyInner = 30.0;
    private const double MaxBeta = 50.0;

    /// <summary>
    /// Fits an elliptical Moffat, pixel-integrated as <see cref="StarProfile.PixelMean"/> renders one, to the star near
    /// (<paramref name="cx"/>, <paramref name="cy"/>) on a plane whose background is zero (a draw minus its plate), over the
    /// pixels within max(4, 2.5 x <paramref name="fwhmStart"/>). The fit starts round, at <paramref name="fwhmStart"/> and
    /// beta 2.5 from the brightest pixel near the centre, so it does not begin at a drawn star's own values. Null where the
    /// window holds too few finite pixels or no positive light.
    /// </summary>
    public static StarShapeFit? FitMoffat(float[] plane, int width, int height, double cx, double cy, double fwhmStart)
    {
        var radius = Math.Max(4.0, 2.5 * fwhmStart);
        var r = (int)Math.Ceiling(radius);
        var x0 = (int)Math.Round(cx);
        var y0 = (int)Math.Round(cy);
        var xs = new List<int>();
        var ys = new List<int>();
        var values = new List<double>();
        var peak = 0.0;
        for (var y = Math.Max(0, y0 - r); y <= Math.Min(height - 1, y0 + r); y++)
        {
            for (var x = Math.Max(0, x0 - r); x <= Math.Min(width - 1, x0 + r); x++)
            {
                var v = plane[(y * width) + x];
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius || !float.IsFinite(v))
                {
                    continue;
                }
                xs.Add(x);
                ys.Add(y);
                values.Add(v);
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= 2.25 && v > peak)
                {
                    peak = v;
                }
            }
        }
        if (values.Count < 12 || !(peak > 0))
        {
            return null;
        }
        var px = xs.ToArray();
        var py = ys.ToArray();
        var data = values.ToArray();

        // Parameters: amplitude, centre, ln FWHM, ln(beta - 1), and the reduced shear (g1, g2), |g| = (1 - q) / (1 + q),
        // which has no angle to wrap and is smooth through round.
        ReadOnlySpan<double> initial = [peak, cx, cy, Math.Log(fwhmStart), Math.Log(1.5), 0.0, 0.0];
        ReadOnlySpan<double> step = [Math.Max(peak * 1e-4, 1e-12), 1e-3, 1e-3, 1e-4, 1e-3, 1e-4, 1e-4];
        var fit = LevenbergMarquardt.Fit(initial, data.Length, (p, residuals) =>
        {
            var profile = ProfileOf(p);
            for (var i = 0; i < data.Length; i++)
            {
                residuals[i] = data[i] - (p[0] * profile.PixelMean(px[i], py[i], p[1], p[2]));
            }
        }, step, maxIterations: 60, relativeTolerance: 1e-9);
        var shape = ProfileOf(fit.Parameters);
        return new StarShapeFit(fit.Parameters[0], fit.Parameters[1], fit.Parameters[2], shape.FwhmPx, shape.Beta, shape.AxisRatio,
            shape.PositionAngleRad, fit.Converged);
    }

    private static StarProfile ProfileOf(ReadOnlySpan<double> p)
    {
        var g1 = p[5];
        var g2 = p[6];
        var g = Math.Min(0.95, Math.Sqrt((g1 * g1) + (g2 * g2)));
        var q = (1.0 - g) / (1.0 + g);
        var angle = 0.5 * Math.Atan2(g2, g1);
        var beta = 1.0 + Math.Min(Math.Exp(p[4]), MaxBeta - 1.0);
        return new StarProfile(StarProfileFamily.Moffat, Math.Exp(p[3]), beta, q, angle);
    }

    /// <summary>
    /// A saturated star's plateau and edge on a single plane (a luminance), about (<paramref name="cx"/>,
    /// <paramref name="cy"/>): the core's maximum within 4 px, the sky the median from 30 to 40 px, the plateau the pixels
    /// within 2 percent of the core above that sky inside 30 px, and the edge from the 90 to the 50 percent crossing of the
    /// azimuthal median profile. The definition R0's survey of the masters used (plateaus of 1 to 4 px at the median, edges
    /// of 0.5 to 1 px). Null within <see cref="SaturatedWindowPx"/> of the edge, or where the core does not stand above
    /// its sky.
    /// </summary>
    public static SaturatedStarShape? SaturatedShape(float[] plane, int width, int height, double cx, double cy)
    {
        if (!(cx > SaturatedWindowPx && cy > SaturatedWindowPx && cx < width - SaturatedWindowPx - 1 && cy < height - SaturatedWindowPx - 1))
        {
            return null;
        }
        var x0 = (int)cx;
        var y0 = (int)cy;
        var max = double.NegativeInfinity;
        var sky = new List<float>();
        const int bins = 60;
        var rings = new List<float>[bins];
        for (var b = 0; b < bins; b++)
        {
            rings[b] = new List<float>();
        }
        for (var y = y0 - SaturatedWindowPx; y <= y0 + SaturatedWindowPx; y++)
        {
            for (var x = x0 - SaturatedWindowPx; x <= x0 + SaturatedWindowPx; x++)
            {
                var v = plane[(y * width) + x];
                if (!float.IsFinite(v))
                {
                    continue;
                }
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d < 4.0 && v > max)
                {
                    max = v;
                }
                if (d > SaturatedSkyInner && d < SaturatedWindowPx)
                {
                    sky.Add(v);
                }
                var bin = (int)(d / 0.5);
                if (bin < bins)
                {
                    rings[bin].Add(v);
                }
            }
        }
        if (sky.Count == 0 || !double.IsFinite(max))
        {
            return null;
        }
        var skyArray = sky.ToArray();
        var skyLevel = (double)StatisticsHelper.NthSmallest(skyArray, skyArray.Length / 2);
        var span = max - skyLevel;
        if (!(span > 0))
        {
            return null;
        }
        var plateau = 0;
        for (var y = y0 - SaturatedWindowPx; y <= y0 + SaturatedWindowPx; y++)
        {
            for (var x = x0 - SaturatedWindowPx; x <= x0 + SaturatedWindowPx; x++)
            {
                var v = plane[(y * width) + x];
                if (float.IsFinite(v) && v >= max - (0.02 * span) && (x - cx) * (x - cx) + (y - cy) * (y - cy) < SaturatedSkyInner * SaturatedSkyInner)
                {
                    plateau++;
                }
            }
        }
        double r90 = double.NaN;
        double r50 = double.NaN;
        for (var b = 0; b < bins && double.IsNaN(r50); b++)
        {
            if (rings[b].Count == 0)
            {
                continue;
            }
            var ring = rings[b].ToArray();
            var fraction = (StatisticsHelper.NthSmallest(ring, ring.Length / 2) - skyLevel) / span;
            if (double.IsNaN(r90) && fraction < 0.9)
            {
                r90 = b * 0.5;
            }
            if (fraction < 0.5)
            {
                r50 = b * 0.5;
            }
        }
        return new SaturatedStarShape(plateau, double.IsNaN(r50) || double.IsNaN(r90) ? double.NaN : r50 - r90);
    }
}
