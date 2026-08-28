using System;
using System.Runtime.CompilerServices;

namespace TianWen.Lib.Imaging;

/// <summary>
/// What <see cref="Image.AnalyseStar(int, int, int, int, out ImagedStar)"/> measured on the way to a
/// star and then discarded: the local sky, its spread, and the aperture the measurement was summed
/// over.
/// </summary>
/// <remarks>
/// It exists so the deblender does not recompute an annulus median that has already been paid for.
/// Every field is in the channel's own value space (NOT pedestal-subtracted), matching
/// <see cref="ImagedStar.LocalBackground"/>.
/// </remarks>
internal readonly record struct StarMeasurementContext(float Background, float NoiseSd, int ApertureRadius);

public partial class Image
{
    /// <summary>Elongation at or above which a measurement is offered to the deblender.</summary>
    internal const float DeblendMinEllipticity = 0.45f;

    /// <summary>
    /// How far HFD may exceed FWHM before a measurement is offered to the deblender, whatever its
    /// elongation says.
    /// </summary>
    /// <remarks>
    /// <para>The two are the same number on a single star -- 2.5066*sigma against 2.3548*sigma for a
    /// Gaussian, a ratio of 1.06 -- and they stay together however LARGE the star is, because both
    /// scale with it: the frame's widest genuine star measures HFD 13.13 against FWHM 13.66.</para>
    /// <para>They come apart on a blend, and only on a blend, because they are driven by different
    /// things. HFD is a flux-weighted RADIUS, so a companion several pixels out pulls it outward in
    /// proportion to its distance; FWHM is where the azimuthally averaged profile crosses half of the
    /// peak, and the peak belongs to the brighter core, so it keeps reporting that core's width. A
    /// 4.6:1 pair 5.6 px apart on this frame reads HFD 7.29 against FWHM 3.42.</para>
    /// <para>It is in the gate because ELONGATION MISSES EXACTLY THAT CASE. Second moments are
    /// dominated by the bright core's own halo, so that pair measured e = 0.42 and sat under the
    /// elongation gate while being, visibly, two stars. The two terms fail in opposite directions --
    /// elongation sees a comparable pair, the ratio sees a lopsided one -- which is why both are
    /// here.</para>
    /// </remarks>
    internal const float DeblendMaxHfdToFwhmRatio = 1.5f;

    /// <summary>
    /// How far above the local noise a secondary maximum must sit before it is a candidate component.
    /// </summary>
    internal const float DeblendPeakNoiseSigma = 5f;

    /// <summary>
    /// How deep the dip between two maxima must be, as a fraction of the FAINTER maximum, for them to
    /// be two objects rather than one.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole decision, and it is a shape test rather than a distance test, which is
    /// what makes it immune to the failure that sank the radius-splitting attempt (7ff7a4bc): a
    /// radius asserts where a companion may be, a saddle asks whether one is there.</para>
    /// <para>For two equal Gaussians of width sigma at separation d, two maxima exist at all only
    /// beyond d = 2*sigma, and the dip between them deepens fast: saddle/peak is 0.88 at 2.5*sigma,
    /// 0.64 at 3*sigma and 0.27 at 4*sigma. 0.85 therefore resolves from about 2.6*sigma, i.e. ~2.9 px
    /// on a FWHM 2.6 px frame, and that is what a peak-based deblender can honestly claim -- below
    /// 2*sigma there is no second maximum to find, and no radius or threshold conjures one.</para>
    /// <para>It also disqualifies a SATURATED core for free. A flat top has many pixels at the
    /// identical clipped value, so its "maxima" have a saddle equal to the peak, i.e. a ratio of 1.0.
    /// No plateau test is needed.</para>
    /// </remarks>
    internal const float DeblendSaddleFraction = 0.85f;

    /// <summary>Fitted components closer than this are one star, whatever the peak map said.</summary>
    internal const float DeblendMinSeparation = 1.8f;

    /// <summary>Minimum share of the blend's flux for a component to be reported.</summary>
    internal const float DeblendMinFluxFraction = 0.03f;

    /// <summary>
    /// Most components one blend may be split into. Three covers the crowded-field triple; beyond
    /// that the aperture holds a cluster, and a per-object measurement is not what the caller wants.
    /// </summary>
    internal const int MaxDeblendComponents = 3;

    /// <summary>Fixed iteration count, so a deblend costs the same on every candidate.</summary>
    private const int DeblendIterations = 24;

    /// <summary>Peaks considered before the saddle test thins them.</summary>
    private const int DeblendMaxPeaks = 8;

    private readonly record struct DeblendPeak(int X, int Y, float Value);

    /// <summary>
    /// Cheap pre-gate deciding whether a measurement is worth attempting to split. It costs only the
    /// elongation the measurement already carries; the DECISION is the saddle test.
    /// </summary>
    /// <remarks>
    /// <para>Two stars inside one aperture displace the second moment along the line joining them by
    /// <c>m1*m2/(m1+m2)^2 * d^2</c>, so even a 4:1 pair 2 px apart on a FWHM 2.6 px frame reads
    /// e = 0.59 against ~0.2 for a round star. Elongation is therefore a sufficient net; it is a poor
    /// verdict, because tracking drift elongates every star on a frame and a diffraction spike
    /// elongates the bright ones. Those cost a peak scan and are then refused, which is the intended
    /// division of labour.</para>
    /// <para><c>FWHM == 0</c> is in the gate for the opposite reason. It is not elongation: it is the
    /// measurement reporting that the brightest pixel in its own box is at least twice its central
    /// value, i.e. a much brighter neighbour is inside the aperture. Detection refuses those outright
    /// (they are positions where no star is), so without this term the most contaminated measurements
    /// on the frame would be the only ones never offered to the deblender.</para>
    /// </remarks>
    internal static bool LooksBlended(in ImagedStar star)
        => star.StarFWHM <= 0f
        || star.Ellipticity >= DeblendMinEllipticity
        || star.HFD > DeblendMaxHfdToFwhmRatio * star.StarFWHM;

    /// <summary>
    /// Splits one merged measurement into the stars actually inside its aperture, by fitting a
    /// several-component model of a COMMON point-spread function to the aperture's pixels.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a fit and not a radius.</b> The suppression radius was attacked twice from the
    /// geometry side and it does not work (see TODO.md): one radius has to stop a star re-triggering
    /// on itself AND decide where another star may stand, and no single number serves both.
    /// Deblending inside the merge distance is a different question -- which stars explain these
    /// pixels -- and only a model answers it. The flux-weighted centre of gravity the single-star path
    /// takes is the centre of MASS of everything in the aperture, so on a pair it lands between the
    /// two stars by construction, which is exactly where no star is.</para>
    /// <para><b>The fit is expectation-maximisation over a shared-width Gaussian mixture</b>, not
    /// Levenberg-Marquardt. It needs no Jacobian, no matrix inverse and no line search, cannot
    /// diverge, and costs a fixed number of passes over a ~13x13 aperture. Each pixel's flux is split
    /// between the components in proportion to what each predicts there (its RESPONSIBILITY), each
    /// component's centre is the responsibility-weighted centroid of its share, and the width is
    /// pooled. A Voronoi split -- assign each pixel to the nearer peak -- is the same idea with hard
    /// weights, and it is worse in the direction that matters: the brighter star's wings fall entirely
    /// inside the fainter star's half-plane and drag its centre outward, which is the very bias being
    /// removed.</para>
    /// <para><b>The width is shared deliberately.</b> Two stars a few pixels apart are the same
    /// point-spread function; giving each its own width lets a bright component swell until it absorbs
    /// its neighbour, which is the classic way a mixture fit collapses to one component.</para>
    /// <para><b>Coordinates are LOCAL to the aperture</b> throughout. A second moment accumulated at
    /// absolute frame coordinates squares a number up to ~3000 and then subtracts almost all of it
    /// back, which spends most of a float's mantissa on cancellation; relative to the aperture centre
    /// no offset exceeds <see cref="BoxRadius"/>.</para>
    /// <para><b>Pixels, not sub-pixel samples.</b> The single-star path interpolates because its grid
    /// is centred on a sub-pixel centroid; a fit carries no such constraint, and interpolating would
    /// correlate neighbouring residuals for nothing.</para>
    /// </remarks>
    /// <param name="plane">
    /// The channel plane the measurement was triggered on. Passed in rather than re-resolved from
    /// <c>Planes</c> per candidate, so the deblender is guaranteed to be reading the same array the
    /// detection scan is -- residency is a property of the operation, not of a repeated lookup.
    /// </param>
    /// <param name="width">Frame width, so the aperture can be bounds-checked.</param>
    /// <param name="height">Frame height, so the aperture can be bounds-checked.</param>
    /// <param name="merged">The single measurement covering the whole blend.</param>
    /// <param name="context">Local sky and aperture, from that same measurement.</param>
    /// <param name="components">Receives the components; must hold <see cref="MaxDeblendComponents"/>.</param>
    /// <returns>
    /// How many components were written. <b>0 means "not a blend"</b>, and the caller keeps its own
    /// measurement -- it never means the star should be dropped. 1 is never returned: a fit that
    /// resolves to one component IS the single measurement, and re-reporting it through a different
    /// estimator would make an isolated star's numbers depend on whether the pre-gate happened to
    /// fire.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal int TryDeblend(
        float[,] plane, int width, int height, in ImagedStar merged, in StarMeasurementContext context,
        Span<ImagedStar> components)
    {
        var apertureRadius = context.ApertureRadius;
        if (apertureRadius < 2 || components.Length < MaxDeblendComponents || context.NoiseSd <= 0f)
        {
            return 0;
        }

        var bg = context.Background;

        // Aperture centre on the integer grid; every offset below is relative to it.
        var ox = (int)MathF.Round(merged.XCentroid);
        var oy = (int)MathF.Round(merged.YCentroid);
        if (ox - apertureRadius - 1 < 0 || ox + apertureRadius + 1 >= width ||
            oy - apertureRadius - 1 < 0 || oy + apertureRadius + 1 >= height)
        {
            return 0;
        }

        Span<DeblendPeak> peaks = stackalloc DeblendPeak[DeblendMaxPeaks];
        var peakCount = FindPeaks(plane, ox, oy, apertureRadius, bg, DeblendPeakNoiseSigma * context.NoiseSd, peaks);
        if (peakCount < 2)
        {
            return 0;
        }

        // Brightest first, so a candidate is only ever tested against peaks that already survived.
        SortPeaksDescending(peaks[..peakCount]);

        Span<float> cx = stackalloc float[MaxDeblendComponents];
        Span<float> cy = stackalloc float[MaxDeblendComponents];
        Span<float> amp = stackalloc float[MaxDeblendComponents];
        var count = 0;

        for (var i = 0; i < peakCount && count < MaxDeblendComponents; i++)
        {
            var candidate = peaks[i];
            var localX = candidate.X - ox;
            var localY = candidate.Y - oy;

            var separate = true;
            for (var k = 0; k < count && separate; k++)
            {
                separate = IsSaddleSeparated(plane, bg, ox, oy, cx[k], cy[k], amp[k], localX, localY, candidate.Value);
            }

            if (separate)
            {
                cx[count] = localX;
                cy[count] = localY;
                amp[count] = candidate.Value;
                count++;
            }
        }

        if (count < 2)
        {
            return 0;
        }

        // Start narrow. A mixture initialised too wide gives every pixel a responsibility near 1/n and
        // the components walk together into a single answer, which is the failure this whole routine
        // exists to avoid. Started narrow, each holds its own core and the shared width grows to fit.
        var sigma = Math.Clamp(Separation(cx[0], cy[0], cx[1], cy[1]) / 3.5f, 0.6f, 2f);
        var maxSigma = MathF.Max(1f, 0.6f * apertureRadius);

        Fit(plane, ox, oy, apertureRadius, bg, count, cx, cy, amp, ref sigma, maxSigma);

        // The fit is free to move the components, so a pair that walked together during it is one
        // star whatever the peak map said beforehand.
        for (var i = 0; i < count; i++)
        {
            if (MathF.Sqrt(cx[i] * cx[i] + cy[i] * cy[i]) > apertureRadius)
            {
                return 0;
            }

            for (var j = i + 1; j < count; j++)
            {
                if (Separation(cx[i], cy[i], cx[j], cy[j]) < DeblendMinSeparation)
                {
                    return 0;
                }
            }
        }

        return Measure(plane, ox, oy, apertureRadius, context, count, cx, cy, amp, sigma, components);
    }

    /// <summary>
    /// Strict local maxima above <paramref name="floor"/> inside the aperture, over a 3x3
    /// neighbourhood.
    /// </summary>
    /// <remarks>
    /// 3x3, and not the 5x5 the mask probe uses: a 5x5 window centred on the fainter half of a 3 px
    /// pair reaches into the brighter core, so the companion is never a maximum and the population
    /// this method exists to find is precisely the one it would miss. Requiring every neighbour to be
    /// STRICTLY lower still disqualifies a saturated plateau, and the saddle test disqualifies it
    /// again.
    /// </remarks>
    private static int FindPeaks(float[,] plane, int ox, int oy, int radius, float bg, float floor, Span<DeblendPeak> peaks)
    {
        var count = 0;
        var rSquared = radius * radius;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > rSquared)
                {
                    continue;
                }

                var x = ox + dx;
                var y = oy + dy;
                var centre = plane[y, x];
                var value = centre - bg;
                if (float.IsNaN(value) || value <= floor)
                {
                    continue;
                }

                var isMax = true;
                for (var ny = -1; ny <= 1 && isMax; ny++)
                {
                    for (var nx = -1; nx <= 1; nx++)
                    {
                        if ((nx | ny) != 0 && plane[y + ny, x + nx] >= centre)
                        {
                            isMax = false;
                            break;
                        }
                    }
                }

                if (!isMax)
                {
                    continue;
                }

                if (count < peaks.Length)
                {
                    peaks[count++] = new DeblendPeak(x, y, value);
                }
                else
                {
                    // Full: keep the brightest by displacing the current weakest.
                    var weakest = 0;
                    for (var k = 1; k < count; k++)
                    {
                        if (peaks[k].Value < peaks[weakest].Value)
                        {
                            weakest = k;
                        }
                    }
                    if (value > peaks[weakest].Value)
                    {
                        peaks[weakest] = new DeblendPeak(x, y, value);
                    }
                }
            }
        }

        return count;
    }

    /// <summary>Insertion sort, brightest first: at most <see cref="DeblendMaxPeaks"/> entries, and it allocates nothing.</summary>
    private static void SortPeaksDescending(Span<DeblendPeak> peaks)
    {
        for (var i = 1; i < peaks.Length; i++)
        {
            var key = peaks[i];
            var j = i - 1;
            while (j >= 0 && peaks[j].Value < key.Value)
            {
                peaks[j + 1] = peaks[j];
                j--;
            }
            peaks[j + 1] = key;
        }
    }

    /// <summary>
    /// True when the dip along the straight line between two maxima falls far enough below the fainter
    /// of them for the two to be separate objects.
    /// </summary>
    private static bool IsSaddleSeparated(
        float[,] plane, float bg, int ox, int oy,
        float ax, float ay, float aValue, float bx, float by, float bValue)
    {
        var fainter = MathF.Min(aValue, bValue);
        if (fainter <= 0f)
        {
            return false;
        }

        var dx = bx - ax;
        var dy = by - ay;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < DeblendMinSeparation)
        {
            return false;
        }

        // Quarter-pixel steps: the dip between two maxima 2 px apart is one pixel wide, so a coarser
        // walk steps over it and reports a blend as a single star.
        var steps = (int)MathF.Ceiling(length * 4f);
        var lowest = float.MaxValue;
        for (var s = 1; s < steps; s++)
        {
            var t = (float)s / steps;
            var value = plane[
                (int)MathF.Round(oy + ay + dy * t),
                (int)MathF.Round(ox + ax + dx * t)] - bg;
            if (!float.IsNaN(value) && value < lowest)
            {
                lowest = value;
            }
        }

        return lowest < DeblendSaddleFraction * fainter;
    }

    /// <summary>
    /// Expectation-maximisation over the aperture: responsibilities from the current model, then
    /// centres, amplitudes and the shared width from those responsibilities.
    /// </summary>
    /// <remarks>
    /// The width is re-estimated in a second sweep, about the UPDATED centres, so it cannot absorb the
    /// centring error of the iteration that produced it -- the single-sweep form
    /// (<c>E[r^2] - |c|^2</c> about the old centre) is the same quantity computed by cancellation, and
    /// it is the term that decides whether the components hold apart or collapse together.
    /// </remarks>
    private static void Fit(
        float[,] plane, int ox, int oy, int radius, float bg, int count,
        Span<float> cx, Span<float> cy, Span<float> amp, ref float sigma, float maxSigma)
    {
        Span<double> sumW = stackalloc double[MaxDeblendComponents];
        Span<double> sumWx = stackalloc double[MaxDeblendComponents];
        Span<double> sumWy = stackalloc double[MaxDeblendComponents];
        Span<float> weight = stackalloc float[MaxDeblendComponents];
        var rSquared = radius * radius;

        for (var iteration = 0; iteration < DeblendIterations; iteration++)
        {
            sumW[..count].Clear();
            sumWx[..count].Clear();
            sumWy[..count].Clear();

            var twoSigmaSq = 2f * sigma * sigma;
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > rSquared)
                    {
                        continue;
                    }

                    var flux = plane[oy + dy, ox + dx] - bg;
                    if (float.IsNaN(flux) || flux <= 0f)
                    {
                        continue;
                    }

                    var total = Responsibilities(count, cx, cy, amp, dx, dy, twoSigmaSq, weight);
                    if (total <= 0f)
                    {
                        continue;
                    }

                    for (var k = 0; k < count; k++)
                    {
                        double share = weight[k] / total * flux;
                        sumW[k] += share;
                        sumWx[k] += share * dx;
                        sumWy[k] += share * dy;
                    }
                }
            }

            var moved = 0f;
            for (var k = 0; k < count; k++)
            {
                if (sumW[k] <= 0)
                {
                    continue;
                }

                var nx = (float)(sumWx[k] / sumW[k]);
                var ny = (float)(sumWy[k] / sumW[k]);
                moved = MathF.Max(moved, MathF.Abs(nx - cx[k]) + MathF.Abs(ny - cy[k]));
                cx[k] = nx;
                cy[k] = ny;
                amp[k] = (float)sumW[k];
            }

            double sumWd2 = 0, sumWall = 0;
            var twoSigmaSqNow = 2f * sigma * sigma;
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > rSquared)
                    {
                        continue;
                    }

                    var flux = plane[oy + dy, ox + dx] - bg;
                    if (float.IsNaN(flux) || flux <= 0f)
                    {
                        continue;
                    }

                    var total = Responsibilities(count, cx, cy, amp, dx, dy, twoSigmaSqNow, weight);
                    if (total <= 0f)
                    {
                        continue;
                    }

                    for (var k = 0; k < count; k++)
                    {
                        var ex = dx - cx[k];
                        var ey = dy - cy[k];
                        double share = weight[k] / total * flux;
                        sumWd2 += share * (ex * ex + ey * ey);
                        sumWall += share;
                    }
                }
            }

            if (sumWall > 0)
            {
                // For an isotropic 2D Gaussian, E[r^2] = 2 * sigma^2.
                sigma = Math.Clamp(MathF.Sqrt((float)(sumWd2 / sumWall) * 0.5f), 0.5f, maxSigma);
            }

            if (moved < 1e-3f)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Each component's unnormalised claim on one pixel, and their total. A zero total means no
    /// component predicts anything here, and the pixel carries no information about any of them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Responsibilities(
        int count, ReadOnlySpan<float> cx, ReadOnlySpan<float> cy, ReadOnlySpan<float> amp,
        int dx, int dy, float twoSigmaSq, Span<float> weight)
    {
        var total = 0f;
        for (var k = 0; k < count; k++)
        {
            var ex = dx - cx[k];
            var ey = dy - cy[k];
            var w = amp[k] * MathF.Exp(-(ex * ex + ey * ey) / twoSigmaSq);
            weight[k] = w;
            total += w;
        }

        return total;
    }

    /// <summary>
    /// Turns a converged fit into <see cref="ImagedStar"/>s, measuring each component over the same
    /// aperture with the model's responsibilities as weights.
    /// </summary>
    /// <remarks>
    /// <para>HFD, flux and ellipticity use the SAME formulae as the single-star path, weighted by
    /// responsibility -- so a component's numbers mean what they mean everywhere else, and a blend
    /// that resolves into two well-separated stars reports two ordinary stars.</para>
    /// <para>FWHM is the fitted width (<c>2*sqrt(2*ln 2)*sigma</c>) rather than an interpolated
    /// half-maximum crossing of a radial profile: a profile drawn around one component still runs
    /// through its neighbour, which is what made the merged measurement wrong in the first place. The
    /// two estimators agree by construction on a Gaussian.</para>
    /// <para>SNR keeps the blend's aperture radius in the background term, so a component's noise is
    /// charged over the whole aperture its flux was summed in. That understates SNR slightly, which is
    /// the safe direction: it can drop a marginal component, never invent one.</para>
    /// </remarks>
    private int Measure(
        float[,] plane, int ox, int oy, int radius, in StarMeasurementContext context,
        int count, ReadOnlySpan<float> cx, ReadOnlySpan<float> cy, ReadOnlySpan<float> amp, float sigma,
        Span<ImagedStar> components)
    {
        Span<float> flux = stackalloc float[MaxDeblendComponents];
        Span<float> fluxR = stackalloc float[MaxDeblendComponents];
        Span<double> posFlux = stackalloc double[MaxDeblendComponents];
        Span<double> mxx = stackalloc double[MaxDeblendComponents];
        Span<double> myy = stackalloc double[MaxDeblendComponents];
        Span<double> mxy = stackalloc double[MaxDeblendComponents];
        Span<float> weight = stackalloc float[MaxDeblendComponents];

        flux[..count].Clear();
        fluxR[..count].Clear();
        posFlux[..count].Clear();
        mxx[..count].Clear();
        myy[..count].Clear();
        mxy[..count].Clear();

        var bg = context.Background;
        var rSquared = radius * radius;
        var twoSigmaSq = 2f * sigma * sigma;

        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > rSquared)
                {
                    continue;
                }

                var value = plane[oy + dy, ox + dx] - bg;
                if (float.IsNaN(value))
                {
                    continue;
                }

                // Responsibilities come from the MODEL, so they are defined where the pixel sits below
                // sky too -- which is what lets flux keep the single-star path's convention of summing
                // the aperture including its negative samples.
                var total = Responsibilities(count, cx, cy, amp, dx, dy, twoSigmaSq, weight);
                if (total <= 0f)
                {
                    continue;
                }

                for (var k = 0; k < count; k++)
                {
                    var share = weight[k] / total;
                    var ex = dx - cx[k];
                    var ey = dy - cy[k];
                    flux[k] += share * value;
                    fluxR[k] += share * value * MathF.Sqrt(ex * ex + ey * ey);

                    if (value > 0f)
                    {
                        var shared = (double)share * value;
                        posFlux[k] += shared;
                        mxx[k] += shared * ex * ex;
                        myy[k] += shared * ey * ey;
                        mxy[k] += shared * ex * ey;
                    }
                }
            }
        }

        var totalFlux = 0f;
        for (var k = 0; k < count; k++)
        {
            totalFlux += MathF.Max(flux[k], 0f);
        }

        if (totalFlux <= 0f)
        {
            return 0;
        }

        var aduScale = HasUnitScalePeak ? ushort.MaxValue : 1.0f;
        var aduSdBg = context.NoiseSd * aduScale;
        var backgroundVariance = radius * radius * MathF.PI * aduSdBg * aduSdBg;
        var fwhm = 2f * MathF.Sqrt(2f * MathF.Log(2f)) * sigma;

        for (var k = 0; k < count; k++)
        {
            // One unusable component abandons the WHOLE split, rather than reporting the others: they
            // were fitted together, so the survivors' flux and centres were shaped by a component the
            // caller is not being told about. Abandoning falls back to the merged measurement, which is
            // at least an honest description of the same pixels.
            if (flux[k] <= 0f || flux[k] < DeblendMinFluxFraction * totalFlux)
            {
                return 0;
            }

            var componentFlux = MathF.Max(flux[k], 0.00001f);
            var hfd = MathF.Max(0.7f, 2f * fluxR[k] / componentFlux);
            if (hfd is <= 0.8f or > BoxRadius * 2)
            {
                return 0;
            }

            var ellipticity = 0f;
            if (posFlux[k] > 0)
            {
                var nxx = mxx[k] / posFlux[k];
                var nyy = myy[k] / posFlux[k];
                var nxy = mxy[k] / posFlux[k];
                var halfTrace = (nxx + nyy) * 0.5;
                var halfDiff = (nxx - nyy) * 0.5;
                var disc = Math.Sqrt(halfDiff * halfDiff + nxy * nxy);
                var a2 = halfTrace + disc;
                var b2 = halfTrace - disc;
                if (a2 > 1e-10)
                {
                    ellipticity = (float)Math.Sqrt(Math.Max(0.0, 1.0 - Math.Max(0.0, b2 / a2)));
                }
            }

            var aduFlux = componentFlux * aduScale;
            var snr = aduFlux / MathF.Sqrt(aduFlux + backgroundVariance);

            components[k] = new ImagedStar(
                hfd, fwhm, snr, componentFlux, ox + cx[k], oy + cy[k], ellipticity, bg);
        }

        return count;
    }

    private static float Separation(float ax, float ay, float bx, float by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
