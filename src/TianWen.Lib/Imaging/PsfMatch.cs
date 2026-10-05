using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Masters brought to one star width before they are combined: each sharper one blurred by the Gaussian that takes its
/// stars to the widest one's. Channels that differ in width colour every star (the sharpest channel's peakier core tints
/// it, the others' wider wings fringe it: LDN 1622's green, 1.97 px against blue's 2.20, left green star cores), and a
/// fixed-aperture star flux, which the continuum scale and the synthetic luminance both read, falls as a star widens.
/// </summary>
/// <remarks>
/// Gaussian widths add in quadrature, so the kernel is <c>sqrt(wide^2 - sharp^2)</c> wide. A real star is not Gaussian,
/// which is why the widths are measured again after (<see cref="ToWidestAsync"/> reports both).
/// </remarks>
public static class PsfMatch
{
    /// <summary>FWHM over sigma for a Gaussian.</summary>
    public const double FwhmPerSigma = 2.354820045;

    /// <summary>The Gaussian sigma, in pixels, taking a star <paramref name="fromFwhm"/> wide to <paramref name="toFwhm"/>;
    /// zero when it is already as wide or wider.</summary>
    public static float SigmaToWiden(double fromFwhm, double toFwhm)
        => toFwhm > fromFwhm ? (float)(Math.Sqrt((toFwhm * toFwhm) - (fromFwhm * fromFwhm)) / FwhmPerSigma) : 0f;

    /// <summary>How close to the target a width must come: a percent of it.</summary>
    public const double Tolerance = 0.01;

    /// <summary>At most this many blurs a master: the first by the quadrature difference, then what is left of it.</summary>
    public const int MaxPasses = 3;

    /// <summary>One master's match: its stars' median FWHM before and after, and the total sigma it was blurred by (the
    /// passes' sigmas in quadrature).</summary>
    public readonly record struct Matched(double FwhmBefore, double FwhmAfter, float Sigma);

    /// <summary>
    /// Every master blurred to the widest one's median star FWHM, to within <see cref="Tolerance"/> in at most
    /// <see cref="MaxPasses"/> blurs; absent pixels are filled with the master's median for the blur and absent again after,
    /// so an alignment border does not spread. Masters already at the widest come back as they were.
    /// </summary>
    public static async Task<(Image[] Images, Matched[] Report, double TargetFwhm)> ToWidestAsync(
        IReadOnlyList<Image> masters, CancellationToken cancellationToken = default)
    {
        var before = new double[masters.Count];
        var target = 0.0;
        for (var i = 0; i < masters.Count; i++)
        {
            before[i] = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(masters[i], cancellationToken));
            if (double.IsFinite(before[i]) && before[i] > target)
            {
                target = before[i];
            }
        }

        var images = new Image[masters.Count];
        var report = new Matched[masters.Count];
        for (var i = 0; i < masters.Count; i++)
        {
            var image = masters[i];
            var width = before[i];
            double applied = 0;
            // A real star is not Gaussian, so one quadrature step undershoots (LDN 1622's red: 2.06 px to 2.16 for a 2.26
            // target). The remaining difference is taken again, at most twice more, the widths adding as the kernels do.
            for (var pass = 0; pass < MaxPasses && double.IsFinite(width) && target - width > Tolerance * target; pass++)
            {
                var sigma = SigmaToWiden(width, target);
                image = BlurKeepingAbsent(image, sigma);
                applied += (double)sigma * sigma;
                width = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(image, cancellationToken));
            }
            images[i] = image;
            report[i] = new Matched(before[i], width, (float)Math.Sqrt(applied));
        }
        return (images, report, target);
    }

    /// <summary>A Gaussian blur that keeps a master's absent pixels absent, filled with its median for the blur so the
    /// border does not spread.</summary>
    internal static Image BlurKeepingAbsent(Image source, float sigma)
    {
        var (channels, width, height) = source.Shape;
        var filled = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var src = source.GetChannelSpan(c);
            var finite = new float[src.Length];
            var n = 0;
            foreach (var v in src)
            {
                if (float.IsFinite(v))
                {
                    finite[n++] = v;
                }
            }
            var median = n > 0 ? StatisticsHelper.MedianFast(finite.AsSpan(0, n)) : 0f;
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var v = src[(y * width) + x];
                    plane[y, x] = float.IsFinite(v) ? v : median;
                }
            }
            filled[c] = plane;
        }
        var blurred = new Image(filled, BitDepth.Float32, source.MaxValue, source.MinValue, source.Pedestal, source.ImageMeta).GaussianBlur(sigma);

        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var src = source.GetChannelSpan(c);
            var soft = blurred.GetChannelSpan(c);
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var at = (y * width) + x;
                    plane[y, x] = float.IsFinite(src[at]) ? soft[at] : float.NaN;
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, BitDepth.Float32, source.MaxValue, source.MinValue, source.Pedestal, source.ImageMeta);
    }
}
