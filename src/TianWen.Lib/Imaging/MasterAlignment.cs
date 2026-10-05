using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Masters of one target, from other nights or through other filters, brought onto ONE grid, the reference master's, so
/// they can be combined pixel by pixel: a colour image from mono filters, a narrowband line against its continuum
/// (<see cref="ContinuumSubtractor"/>, #874), two nights of one object (<c>DatasetCrossNightExporter</c>).
/// </summary>
/// <remarks>
/// <para><b>By the stars, as the stacker registers a sub.</b> Each master's stars (<see cref="FindStarsAsync"/>) are
/// matched to the reference's by <see cref="FrameRegistration.TryMatchAsync"/>, and the moving master is resampled once
/// onto the reference's grid; the reference is not resampled. A master's uncovered canvas ring is exact zero, so it is
/// masked to NaN first (<see cref="MaskAbsent"/>), and the warp keeps it absent.</para>
/// <para><b>The star budget is widened until one answers</b> (<see cref="MatchAsync"/>): the matcher takes the brightest
/// N stars of each side, and two masters that share half a field can hold no quad in common among a hundred stars
/// apiece, so a partial overlap starves the matcher rather than defeating it.</para>
/// <para><b>Resampling is a blur</b>, so a pair meant to be subtracted (a line and its continuum) is best aligned onto a
/// THIRD master's grid, where both are resampled alike, rather than one onto the other.</para>
/// </remarks>
public static class MasterAlignment
{
    /// <summary>The signal-to-noise a star must reach to count, as the cross-night exporter takes them.</summary>
    public const float StarSnrMin = 20f;

    /// <summary>At most this many stars a master: the brightest, which are what the quad matcher reads.</summary>
    public const int StarMax = 500;

    /// <summary>The star budgets tried in order: the matcher's default, then twice and four times it.</summary>
    public static IReadOnlyList<int> DefaultQuadBudgets { get; } =
        [FrameRegistration.DefaultQuadStars, FrameRegistration.DefaultQuadStars * 2, FrameRegistration.DefaultQuadStars * 4];

    /// <summary>One master on the reference's grid.</summary>
    /// <param name="Image">The master resampled onto the reference's grid, NaN where it does not reach.</param>
    /// <param name="ToReference">The fitted transform from the master's pixels to the reference's.</param>
    /// <param name="Stars">Stars the master gave the match.</param>
    /// <param name="QuadStars">The star budget that answered.</param>
    /// <param name="RmsPx">The fit's residual over its matched stars, in pixels.</param>
    /// <param name="Scale">The transform's scale (one for the same optics).</param>
    /// <param name="RotationDeg">The transform's rotation, in degrees.</param>
    /// <param name="MedianFwhm">The master's own stars' median FWHM, in its own pixels: two masters to be subtracted
    /// should agree on it.</param>
    public sealed record Aligned(
        Image Image, Matrix3x2 ToReference, int Stars, int QuadStars, float QuadTolerance, float RmsPx, double Scale,
        double RotationDeg, double MedianFwhm);

    /// <summary>The channel a master's stars are read in: green for colour, the one plane of a mono master.</summary>
    public static int StarChannel(Image master) => master.ChannelCount > 1 ? 1 : 0;

    /// <summary>A master's stars, on its absent ring masked to NaN.</summary>
    public static Task<StarList> FindStarsAsync(Image master, CancellationToken cancellationToken = default)
        => MaskAbsent(master).FindStarsAsync(StarChannel(master), StarSnrMin, StarMax, FrameRegistration.MinStarsForMatch,
            cancellationToken: cancellationToken);

    /// <summary>Exact zeros and non-finite pixels become NaN: a retained master's uncovered canvas ring.</summary>
    public static Image MaskAbsent(Image source)
    {
        var (channels, width, height) = source.Shape;
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var src = source.GetChannelSpan(c);
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var v = src[row + x];
                    plane[y, x] = v == 0f || !float.IsFinite(v) ? float.NaN : v;
                }
            }
            planes[c] = plane;
        }
        return new Image(planes, source.BitDepth, source.MaxValue, source.MinValue, source.Pedestal, source.ImageMeta);
    }

    /// <summary>
    /// The transform taking <paramref name="moving"/>'s stars onto <paramref name="reference"/>'s, at the first of
    /// <paramref name="budgets"/> (each capped by the shorter list) that answers; a budget of 0 when none did.
    /// </summary>
    public static async Task<(Matrix3x2 Transform, float QuadTolerance, float RmsPx, int QuadStars)> MatchAsync(
        StarList reference, StarList moving, IReadOnlyList<int> budgets)
    {
        using var sortedMoving = new SortedStarList(moving);
        using var sortedReference = new SortedStarList(reference);
        var cap = Math.Min(reference.Count, moving.Count);
        var tried = 0;
        foreach (var wanted in budgets)
        {
            var budget = Math.Min(wanted, cap);
            if (budget <= tried)
            {
                continue;
            }
            tried = budget;
            var (solution, tolerance, rms) = await FrameRegistration.TryMatchAsync(sortedMoving, sortedReference, budget);
            if (solution is { } transform)
            {
                return (transform, tolerance, rms, budget);
            }
        }
        return (Matrix3x2.Identity, 0f, 0f, 0);
    }

    /// <summary>
    /// <paramref name="moving"/> on <paramref name="reference"/>'s grid, or null and why not (too few stars, no quad in
    /// common, a transform with no inverse). <paramref name="referenceStars"/> may be passed to align several masters to
    /// one reference without reading its stars each time.
    /// </summary>
    public static async Task<(Aligned? Result, string Reason)> AlignAsync(
        Image reference, Image moving, StarList? referenceStars = null, CancellationToken cancellationToken = default)
    {
        var refStars = referenceStars ?? await FindStarsAsync(reference, cancellationToken);
        var stars = await FindStarsAsync(moving, cancellationToken);
        if (refStars.Count < FrameRegistration.MinStarsForMatch || stars.Count < FrameRegistration.MinStarsForMatch)
        {
            return (null, $"too few stars ({stars.Count} against the reference's {refStars.Count}, need {FrameRegistration.MinStarsForMatch})");
        }
        var (transform, tolerance, rms, quadStars) = await MatchAsync(refStars, stars, DefaultQuadBudgets);
        if (quadStars == 0)
        {
            return (null, $"no quad in common at up to {Math.Min(DefaultQuadBudgets[^1], Math.Min(refStars.Count, stars.Count))} stars a side");
        }
        var determinant = (transform.M11 * transform.M22) - (transform.M12 * transform.M21);
        if (Math.Abs(determinant) < 1e-9f)
        {
            return (null, "the fitted transform has no inverse");
        }
        var warped = await MaskAbsent(moving).WarpToReferenceGridAsync(transform, reference.Width, reference.Height, cancellationToken);
        return (new Aligned(
            warped, transform, stars.Count, quadStars, tolerance, rms,
            Math.Sqrt(Math.Abs(determinant)),
            Math.Atan2(transform.M12, transform.M11) * 180.0 / Math.PI,
            MedianFwhm(stars)), "");
    }

    /// <summary>The median FWHM of a star list, in its image's pixels; NaN for an empty list.</summary>
    public static double MedianFwhm(StarList stars)
    {
        if (stars.Count == 0)
        {
            return double.NaN;
        }
        var fwhm = new float[stars.Count];
        var n = 0;
        foreach (var star in stars)
        {
            fwhm[n++] = star.StarFWHM;
        }
        return Stat.StatisticsHelper.MedianFast(fwhm.AsSpan(0, n));
    }
}
