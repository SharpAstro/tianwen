using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What a limb fit knows of the planet before it looks: the shape and lighting the ephemeris gives.</summary>
/// <param name="AxisRatio">The disk's apparent polar over equatorial radius (<see cref="PlanetaryLimbFit.ApparentAxisRatio"/>).</param>
/// <param name="PhaseAngleDeg">The phase angle, always modelled when above zero: its brightness asymmetry is FIRST order in
/// the angle (a 10 degree phase ignored moved a fitted centre 2.6 px), where the terminator's bite is second order.</param>
/// <param name="AnnulusInner">The fitted pixels' inner bound, in equatorial radii: the limb's surroundings, not the disk's
/// detail (belts and spots are not in the model).</param>
/// <param name="AnnulusOuter">The fitted pixels' outer bound, in equatorial radii.</param>
/// <param name="SunSide">Which end of the equator the sun lights (+1 or -1), when it is known; null fits both and keeps the
/// better.</param>
/// <param name="SunTiltDeg">How far the lit side lies off the equator, toward the north pole
/// (<see cref="PhysicalEphemeris.SunOnTheDisk"/>): 5.2 degrees on 2022-09-03. Which end of the fitted axis is north is not
/// known from the image, so both are fitted and the better kept.</param>
/// <param name="SubObserverLatitudeDeg">The planetocentric latitude the observer sees the disk from, which the zonal albedo's
/// latitude counts in: without it one pole's dark cap reads larger than the other's, and the centre moves along the axis
/// (0.15 px with Jupiter's poles alone, #1050).</param>
public sealed record LimbFitOptions(double AxisRatio, double PhaseAngleDeg = 0, double AnnulusInner = 0.8, double AnnulusOuter = 1.2,
    int? SunSide = null, double SunTiltDeg = 0, double SubObserverLatitudeDeg = 0);

/// <summary>
/// A disk fitted at its limb: where the planet is, how big, and how it lies in the image. Angles in degrees, image
/// convention (from +x toward +y).
/// </summary>
/// <param name="EquatorialRadius">Where the unblurred disk's brightness reaches zero along the equator, in pixels: the
/// planet's own edge, which WinJUPOS's outline marks, never the blurred image's steepest point, which lies inside it.</param>
/// <param name="AxisAngleDeg">The direction of the planet's axis in the image (modulo 180).</param>
/// <param name="LimbDarkening">The Minnaert exponent k (brightness mu0^k mu^(k-1)).</param>
/// <param name="PsfSigma">The core of the blur the edge shows, a Gaussian's sigma in pixels.</param>
/// <param name="SunSide">+1 or -1: which end of the equator the sun lights (0 when no phase was modelled).</param>
/// <param name="StandardErrors">The fit's standard errors, in the order centre x, centre y, radius, axis angle (degrees),
/// limb darkening, PSF sigma, brightness, sky, and the zonal albedo's two terms.</param>
/// <param name="ZonalAlbedo2">The albedo's term in the square of the sine of latitude z: <c>A = 1 + c1 z + c2 z^2 + c4 z^4</c>.</param>
/// <param name="ZonalAlbedo4">The albedo's term in its fourth power. At the poles the albedo is <c>1 +- c1 + c2 + c4</c> of the
/// equator's.</param>
/// <param name="ZonalAlbedo1">The albedo's odd term, one hemisphere against the other, z counted toward
/// <paramref name="NorthAngleDeg"/>.</param>
/// <param name="NorthAngleDeg">The axis's end the model took as north (from +x toward +y): the one the sun's tilt off the
/// equator points to. Meaningful only when <see cref="LimbFitOptions.SunTiltDeg"/> was not zero; otherwise one of the axis's
/// two ends.</param>
/// <param name="HaloFraction">The share of the blur in its wide wing, a second Gaussian: seeing's Moffat wings and the
/// telescope's diffraction rings, which a single Gaussian cannot follow (#1050).</param>
/// <param name="HaloWidth">The wing's sigma, in pixels.</param>
public readonly record struct LimbFit(
    double CenterX,
    double CenterY,
    double EquatorialRadius,
    double AxisAngleDeg,
    double LimbDarkening,
    double PsfSigma,
    double Brightness,
    double Sky,
    int SunSide,
    double RmsResidual,
    int Pixels,
    double[] StandardErrors,
    int Iterations,
    bool Converged,
    double ZonalAlbedo2 = 0,
    double ZonalAlbedo4 = 0,
    double ZonalAlbedo1 = 0,
    double NorthAngleDeg = 0,
    double HaloFraction = 0,
    double HaloWidth = 0);

/// <summary>
/// Fits a planet's disk at its limb with a forward model (docs/plans/planetary-restoration.md, R1): an oblate disk of the
/// ephemeris' axis ratio, limb-darkened by Minnaert's law and lit at the ephemeris' phase, its albedo a smooth function of
/// latitude, blurred by a Gaussian PSF, over the pixels around the limb. Never the centre of mass, which a bright belt or the Great Red Spot moves, and never
/// an edge detector: Jupiter darkens to its limb, so the blurred image's steepest point lies a couple of pixels inside it
/// (measured on the 2022 stacks WinJUPOS measured).
/// </summary>
public static class PlanetaryLimbFit
{
    private const double Supersample = 2;

    /// <summary>
    /// Why the fit cannot stand for <paramref name="planet"/>'s outline, or null where it can. Saturn's rings lie outside its globe and
    /// are not in the model, so a fit swallows them: on 2021-12-16's Saturn it put the globe's radius at 28.1 px, half again the
    /// 18.6 px the ephemeris and the plate scale give, with a 9 px blur. Everything read off the outline goes with it (the derived
    /// sharpening's edge, its bound and moons, a de-rotation's spheroid), so those decline such a planet until the rings are modelled
    /// (#1184).
    /// </summary>
    public static string? Unmodelled(CatalogIndex planet) => planet is CatalogIndex.Saturn
        ? "Saturn's rings are not in the limb fit's model yet (#1184)"
        : null;

    /// <summary>
    /// The apparent polar over equatorial radius of an oblate planet seen from a planetocentric latitude
    /// <paramref name="latitudeCentricDeg"/>: <c>sqrt(sin^2 D + q^2 cos^2 D)</c> for the true ratio q = 1 - flattening.
    /// </summary>
    public static double ApparentAxisRatio(double flattening, double latitudeCentricDeg)
    {
        var q = 1 - flattening;
        var d = latitudeCentricDeg * Math.PI / 180;
        return Math.Sqrt((Math.Sin(d) * Math.Sin(d)) + (q * q * Math.Cos(d) * Math.Cos(d)));
    }

    /// <summary>The options the ephemeris gives for <paramref name="aspect"/>.</summary>
    public static LimbFitOptions OptionsFor(in PlanetAspect aspect)
    {
        var (west, north, _) = PhysicalEphemeris.SunOnTheDisk(aspect);
        return new LimbFitOptions(ApparentAxisRatio(aspect.Flattening, aspect.SubObserverLatitudeCentric), aspect.PhaseAngle,
            SunTiltDeg: Math.Atan2(north, Math.Abs(west)) * 180 / Math.PI, SubObserverLatitudeDeg: aspect.SubObserverLatitudeCentric);
    }

    /// <summary>
    /// Fits the disk in <paramref name="image"/>: its luminance (the mean of its channels), started from <see cref="Start"/>.
    /// Null when no disk stands out of the sky, or it leaves too few pixels around the limb inside the frame.
    /// </summary>
    public static LimbFit? Fit(Image image, LimbFitOptions options)
    {
        var plane = Luminance(image);
        return Start(plane, image.Width, image.Height, options.AxisRatio) is { } start
            ? Fit(plane, image.Width, image.Height, start.X, start.Y, start.Radius, options)
            : null;
    }

    /// <summary>What a disk is fitted on: the mean of <paramref name="image"/>'s channels, row-major.</summary>
    public static float[] Luminance(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var plane = new float[image.Width * image.Height];
        var channels = image.ChannelCount;
        for (var c = 0; c < channels; c++)
        {
            var source = image.GetChannelSpan(c);
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] += source[i] / channels;
            }
        }
        return plane;
    }

    /// <summary>
    /// A start for the fit, within a pixel or two for a disk of any size: the region brighter than a quarter of the way
    /// from the sky to the disk (their 5th and 99th percentiles), its centroid counted pixel by pixel (a belt cannot pull a
    /// binary centroid) and the equatorial radius its area gives at <paramref name="axisRatio"/>. Not
    /// <see cref="PlanetaryDisk.BoundingBox"/>: its mean-plus-three-sigma threshold passes only a disk's bright middle once
    /// the disk fills a fifth of the frame, as a planetary stack's does, and started the fit at half the radius. Null when
    /// nothing stands out of the sky.
    /// </summary>
    public static (double X, double Y, double Radius)? Start(ReadOnlySpan<float> plane, int width, int height, double axisRatio)
    {
        var copy = plane.ToArray();
        var sky = StatisticsHelper.NthSmallest(copy, (int)(0.05 * (copy.Length - 1)));
        var disk = StatisticsHelper.NthSmallest(copy, (int)(0.99 * (copy.Length - 1)));
        if (!(disk > sky))
        {
            return null;
        }
        var level = sky + (0.25 * (disk - sky));
        double sx = 0, sy = 0, n = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (plane[row + x] > level)
                {
                    sx += x;
                    sy += y;
                    n++;
                }
            }
        }
        return n < 50 ? null : (sx / n, sy / n, Math.Sqrt(n / (Math.PI * axisRatio)));
    }

    /// <summary>
    /// Fits the disk in <paramref name="plane"/> (row-major, <paramref name="width"/> x <paramref name="height"/>, any
    /// scale), starting from a centre and radius within a few pixels (<see cref="Start"/> gives one).
    /// Null when the start leaves too few pixels around the limb inside the frame.
    /// </summary>
    public static LimbFit? Fit(ReadOnlySpan<float> plane, int width, int height, double startX, double startY, double startRadius,
        LimbFitOptions options)
    {
        // Coarse to fine: a disk wider than about 40 px in radius is first fitted on a binned copy, where every model
        // evaluation costs a bin's square less (binning is linear, and its box blur folds into the fitted sigma), then refined
        // at full resolution from there, where it only has a little way to go.
        var bin = Math.Max(1, (int)Math.Round(startRadius / CoarseRadius));
        if (bin > 1)
        {
            var (binned, bw, bh) = Bin(plane, width, height, bin);
            // A pixel's centre at (x, y) lies at ((x + 0.5) / bin - 0.5) in the binned frame.
            if (FitAt(binned, bw, bh, ((startX + 0.5) / bin) - 0.5, ((startY + 0.5) / bin) - 0.5, startRadius / bin, options, null, 200) is { } coarse)
            {
                var seed = SeedFrom(coarse, ((coarse.CenterX + 0.5) * bin) - 0.5, ((coarse.CenterY + 0.5) * bin) - 0.5, coarse.EquatorialRadius * bin,
                    Math.Sqrt(Math.Max((coarse.PsfSigma * coarse.PsfSigma * bin * bin) - ((bin * bin) - 1) / 12.0, 0.25)));
                return FitAt(plane, width, height, seed[0], seed[1], seed[2], options, (seed, coarse.SunSide), 30);
            }
        }
        return FitAt(plane, width, height, startX, startY, startRadius, options, null, 200);
    }

    /// <summary>
    /// Fits the disk in <paramref name="plane"/> from a fit of a like image, a capture's aligned mean for one of its frames: every
    /// parameter starts at <paramref name="like"/>'s but the centre, which starts at (<paramref name="startX"/>,
    /// <paramref name="startY"/>), so the search begins beside its answer. A cold start on a single frame of 2022-09-03 took most
    /// of a minute of processor time (#1050). Null when too few pixels around the limb lie in the frame.
    /// </summary>
    public static LimbFit? Fit(ReadOnlySpan<float> plane, int width, int height, double startX, double startY, in LimbFit like, LimbFitOptions options)
    {
        var seed = SeedFrom(like, startX, startY, like.EquatorialRadius, like.PsfSigma);
        return FitAt(plane, width, height, startX, startY, like.EquatorialRadius, options, (seed, like.SunSide), 30);
    }

    /// <summary>
    /// <paramref name="fit"/>'s disk as its own model renders it before any blur (docs/plans/planetary-restoration.md, R7 part 3): the
    /// limb-darkened disk lit at the fit's phase, with its zonal albedo, each pixel the mean of its supersampled cells, of unit brightness
    /// on a sky of zero, over <paramref name="width"/> by <paramref name="height"/>; zero off the disk and on its night side. What a
    /// kernel other than the fit's own is fitted around, the geometry kept.
    /// </summary>
    public static float[] SharpModel(in LimbFit fit, LimbFitOptions options, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Every pixel the disk reaches, inside the model's own grid (which reaches 12 px past its annulus).
        var cover = options with { AnnulusOuter = 1.05 };
        var reach = (int)Math.Ceiling((1.05 * fit.EquatorialRadius) + 2);
        var (cx, cy) = ((int)Math.Round(fit.CenterX), (int)Math.Round(fit.CenterY));
        var pixels = new List<int>();
        for (var y = Math.Max(0, cy - reach); y <= Math.Min(height - 1, cy + reach); y++)
        {
            for (var x = Math.Max(0, cx - reach); x <= Math.Min(width - 1, cx + reach); x++)
            {
                pixels.Add((y * width) + x);
            }
        }
        var model = new DiskModel(pixels, width, height, cover, fit.SunSide, fit.CenterX, fit.CenterY, fit.EquatorialRadius);
        // The fit's own parameters with no blur (a core under 0.05 cells is none), no halo, unit brightness and no sky.
        var p = SeedFrom(fit, fit.CenterX, fit.CenterY, fit.EquatorialRadius, 0);
        (p[6], p[7], p[11]) = (1, 0, 0);
        var values = new double[pixels.Count];
        model.Evaluate(p, values);
        var result = new float[width * height];
        for (var i = 0; i < pixels.Count; i++)
        {
            result[pixels[i]] = (float)values[i];
        }
        return result;
    }

    // A fit's parameters as the search holds them, with the centre, radius and core sigma given: how one fit seeds another. The
    // halo's share and width are held through their bounds (DiskModel.HaloFraction, HaloRatio), so they go back through them:
    // passing the share as it reads started the next search at a quarter of it.
    private static double[] SeedFrom(in LimbFit fit, double centerX, double centerY, double radius, double sigma)
    {
        return [centerX, centerY, radius, fit.NorthAngleDeg * Math.PI / 180, fit.LimbDarkening, sigma, fit.Brightness, fit.Sky,
            fit.ZonalAlbedo2, fit.ZonalAlbedo4, fit.ZonalAlbedo1, 2 * fit.HaloFraction, fit.PsfSigma > 0 ? fit.HaloWidth / fit.PsfSigma : 3];
    }

    // The radius a coarse fit works at: enough pixels around the limb for the model's eight parameters, few enough to be quick.
    private const double CoarseRadius = 40;

    // A plane binned by `bin` in each direction, each cell the mean of its pixels (a partial cell at the edge is dropped).
    private static (float[] Plane, int Width, int Height) Bin(ReadOnlySpan<float> plane, int width, int height, int bin)
    {
        var (bw, bh) = (width / bin, height / bin);
        var binned = new float[bw * bh];
        for (var y = 0; y < bh; y++)
        {
            for (var x = 0; x < bw; x++)
            {
                double sum = 0;
                for (var dy = 0; dy < bin; dy++)
                {
                    var row = ((y * bin) + dy) * width;
                    for (var dx = 0; dx < bin; dx++)
                    {
                        sum += plane[row + (x * bin) + dx];
                    }
                }
                binned[(y * bw) + x] = (float)(sum / (bin * bin));
            }
        }
        return (binned, bw, bh);
    }

    // One fit, from a centre and radius, or from a full parameter vector and the sun's side a coarser fit settled on.
    private static LimbFit? FitAt(ReadOnlySpan<float> plane, int width, int height, double startX, double startY, double startRadius,
        LimbFitOptions options, (double[] Parameters, int SunSide)? seed, int maxIterations)
    {
        var pixels = AnnulusPixels(width, height, startX, startY, startRadius, options);
        if (pixels.Count < 200)
        {
            return null;
        }
        var observed = new double[pixels.Count];
        for (var i = 0; i < pixels.Count; i++)
        {
            observed[i] = plane[pixels[i]];
        }
        var (sky, peak) = SkyAndPeak(observed);

        // Which end of the equator the sun lights is not known without the image's orientation on the sky, so both are
        // fitted and the better kept (a seed carries the side its coarse fit chose); with no phase there is one fit. Nor is
        // which end of the axis is north, which matters once the lit side is tilted off the equator: the model counts the
        // tilt toward the end the axis angle points to, so both ends are started.
        var sides = seed is { } given ? [given.SunSide]
            : options.PhaseAngleDeg <= 0 ? [0]
            : options.SunSide is { } known ? [known]
            : new[] { 1, -1 };
        var startAxisAngleDeg = seed is null ? AxisFromShape(plane, width, height, startX, startY, startRadius, (sky + peak) / 2) : 0;
        var ends = seed is not null || options.PhaseAngleDeg <= 0 || options.SunTiltDeg == 0 ? [0.0] : new[] { 0.0, Math.PI };

        // The searches are independent, each with its own model, so they run at once; the best is chosen in their order.
        (int Side, double End)[] combinations = [.. Combinations(sides, ends)];
        var candidates = new LimbFit[combinations.Length];
        ParallelFor.Run(combinations.Length, c =>
        {
            var (side, end) = combinations[c];
            var model = new DiskModel(pixels, width, height, options, side, startX, startY, startRadius);
            Span<double> start = seed is { } s ? s.Parameters
                : [startX, startY, startRadius, (startAxisAngleDeg * Math.PI / 180) + end, 0.9, 1.5, peak - sky, sky, 0, 0, 0, 0.05, 3];
            Span<double> step = [1e-3, 1e-3, 1e-3, 1e-5, 1e-4, 1e-4, 1e-4 * Math.Max(peak - sky, 1e-6), 1e-4 * Math.Max(peak - sky, 1e-6), 1e-4, 1e-4, 1e-4, 1e-4, 1e-3];
            var fit = LevenbergMarquardt.Fit(start, observed.Length, (p, r) =>
            {
                model.Evaluate(p, r);
                for (var i = 0; i < r.Length; i++)
                {
                    r[i] -= observed[i];
                }
            }, step, maxIterations: maxIterations);

            var q = fit.Parameters;
            var errors = fit.StandardErrors;
            errors[3] *= 180 / Math.PI;
            var north = ((q[3] * 180 / Math.PI % 360) + 360) % 360;
            var candidate = new LimbFit(q[0], q[1], Math.Abs(q[2]), Normalise(north), q[4], Math.Abs(q[5]), q[6], q[7],
                side, Math.Sqrt(2 * fit.Cost / observed.Length), observed.Length, errors, fit.Iterations, fit.Converged, q[8], q[9], q[10], north,
                DiskModel.HaloFraction(q[11]), DiskModel.HaloRatio(q[12]) * Math.Abs(q[5]));
            candidates[c] = candidate;
        });
        LimbFit? best = null;
        foreach (var candidate in candidates)
        {
            if (best is not { } current || candidate.RmsResidual < current.RmsResidual)
            {
                best = candidate;
            }
        }
        return best;
    }

    private static IEnumerable<(int Side, double End)> Combinations(int[] sides, double[] ends)
    {
        foreach (var side in sides)
        {
            foreach (var end in ends)
            {
                yield return (side, end);
            }
        }
    }

    // The axis's direction from the disk's SHAPE: the second moments of the pixels above half the limb's level, within 1.3
    // radii, weighted equally (brightness would let a belt pull it). For an ellipse the smaller-variance direction is the
    // axis. A start inside the right basin: at 90 degrees out the fit settles on the swapped axes instead.
    private static double AxisFromShape(ReadOnlySpan<float> plane, int width, int height, double x0, double y0, double radius, double level)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, n = 0;
        var reach = 1.3 * radius;
        var y1 = Math.Max(0, (int)(y0 - reach));
        var y2 = Math.Min(height - 1, (int)(y0 + reach));
        var x1 = Math.Max(0, (int)(x0 - reach));
        var x2 = Math.Min(width - 1, (int)(x0 + reach));
        for (var y = y1; y <= y2; y++)
        {
            for (var x = x1; x <= x2; x++)
            {
                if (plane[(y * width) + x] > level)
                {
                    sx += x;
                    sy += y;
                    sxx += (double)x * x;
                    syy += (double)y * y;
                    sxy += (double)x * y;
                    n++;
                }
            }
        }
        if (n < 10)
        {
            return 0;
        }
        var (mx, my) = (sx / n, sy / n);
        var (cxx, cyy, cxy) = ((sxx / n) - (mx * mx), (syy / n) - (my * my), (sxy / n) - (mx * my));
        // The major axis lies at half the angle of (cxx - cyy, 2 cxy); the planet's axis is the minor one, 90 degrees on.
        var major = 0.5 * Math.Atan2(2 * cxy, cxx - cyy) * 180 / Math.PI;
        return Normalise(major + 90);
    }

    // An axis direction modulo 180, in [0, 180).
    private static double Normalise(double degrees)
    {
        var d = degrees % 180;
        return d < 0 ? d + 180 : d;
    }

    // The pixels whose centres lie in the annulus around the starting ellipse (as a circle of the starting radius: the
    // annulus is generous enough for the flattening), inside the frame.
    private static List<int> AnnulusPixels(int width, int height, double x0, double y0, double radius, LimbFitOptions options)
    {
        var pixels = new List<int>();
        var inner = options.AnnulusInner * options.AxisRatio * radius;
        var outer = options.AnnulusOuter * radius;
        var y1 = Math.Max(0, (int)Math.Floor(y0 - outer));
        var y2 = Math.Min(height - 1, (int)Math.Ceiling(y0 + outer));
        var x1 = Math.Max(0, (int)Math.Floor(x0 - outer));
        var x2 = Math.Min(width - 1, (int)Math.Ceiling(x0 + outer));
        for (var y = y1; y <= y2; y++)
        {
            for (var x = x1; x <= x2; x++)
            {
                var r = Math.Sqrt(((x - x0) * (x - x0)) + ((y - y0) * (y - y0)));
                if (r >= inner && r <= outer)
                {
                    pixels.Add((y * width) + x);
                }
            }
        }
        return pixels;
    }

    // The sky (a low percentile of the annulus, which is mostly off the disk outside) and the disk's level near the limb's
    // inside (a high percentile).
    private static (double Sky, double Peak) SkyAndPeak(double[] values)
    {
        var copy = (double[])values.Clone();
        return (StatisticsHelper.NthSmallest(copy, (int)(0.1 * (copy.Length - 1))), StatisticsHelper.NthSmallest(copy, (int)(0.98 * (copy.Length - 1))));
    }

    /// <summary>
    /// The model, rendered supersampled on a grid covering the annulus, blurred by a separable Gaussian, and read at the
    /// fitted pixels. Parameters: centre x, centre y, equatorial radius, axis angle (radians), Minnaert k, PSF sigma,
    /// brightness, sky, the zonal albedo's terms in the squared, fourth and first powers of the sine of latitude, and the
    /// blur's wing: its share and its width over the core's.
    /// <para>
    /// Measured against a rendered Jupiter (T1, #1050), two things the model lacked moved the fit. A cell the limb crosses
    /// was lit or dark by its centre alone, so the model jumped as the limb crossed a cell centre; on the sunlit limb of a
    /// phased disk, whose brightness does not fall to zero at the edge, those jumps stalled the search in a worse minimum
    /// (2.4 % small on a uniform disk the truth fitted to 0.2 %). A cell is now lit by the fraction of it inside the limb.
    /// And a uniform albedo read Jupiter's darker polar regions as a smaller disk (2.5 % small even started from the
    /// truth), which the zonal albedo absorbs, its odd term the difference between the hemispheres. The sun is where the
    /// ephemeris puts it, off the equator (<see cref="LimbFitOptions.SunTiltDeg"/>), with its latitude counted from the
    /// observer's (<see cref="LimbFitOptions.SubObserverLatitudeDeg"/>). And a single Gaussian blur, against seeing's Moffat
    /// wings and the telescope's diffraction rings, left the radius 0.4 to 0.7 % large; the blur has a second, wider
    /// Gaussian for its wing.
    /// </para>
    /// <para>
    /// An evaluation is the fit's whole cost, about a thousand of them a cold fit, so it renders, blurs and reads back its rows in
    /// parallel bands (#1106). Every output gathers into its own cell, its taps summed first to last, so the fit is bit for bit one
    /// walk's; a reduction across bands would not be. A cold fit of the 2022-09-03 Red stack went from 9.3 to 1.8 s (Release).
    /// </para>
    /// </summary>
    private sealed class DiskModel
    {
        private readonly List<int> _pixels;
        private readonly int _width;
        private readonly LimbFitOptions _options;
        private readonly int _sunSide;
        private readonly int _gridX0;
        private readonly int _gridY0;
        private readonly int _gridWidth;
        private readonly int _gridHeight;
        private readonly double[] _grid;
        private readonly double[] _scratch;
        private readonly double[] _sharp;
        private readonly double[] _wing;
        private readonly double[] _coarse;
        private readonly double[] _coarseScratch;

        // The wing is blurred on a grid coarser by up to this: it is smooth by construction, at least twice the core.
        private const int MaxWingBin = 16;

        // The wing's share, kept in [0, 0.5), and its width over the core's, kept in [1, 64], whatever the search tries: an
        // unbounded width once asked for a kernel wider than the stack could hold. A bound of 8 was too tight: a Moffat of
        // beta 2 (T1's heaviest seeing) held the wing at both bounds and moved the radius 0.6 %.
        public static double HaloFraction(double raw) => 0.5 * Math.Clamp(raw, 0, 0.999);

        public static double HaloRatio(double raw) => 1 + Math.Min(Math.Abs(raw - 1), 63);

        public DiskModel(List<int> pixels, int width, int height, LimbFitOptions options, int sunSide, double x0, double y0, double radius)
        {
            _pixels = pixels;
            _width = width;
            _options = options;
            _sunSide = sunSide;
            // The grid: the annulus's box plus a margin for the blur, in supersampled cells.
            var reach = (options.AnnulusOuter * radius) + 12;
            _gridX0 = (int)Math.Floor(x0 - reach);
            _gridY0 = (int)Math.Floor(y0 - reach);
            _gridWidth = (int)(Math.Ceiling(2 * reach) * Supersample);
            _gridHeight = (int)(Math.Ceiling(2 * reach) * Supersample);
            _grid = new double[_gridWidth * _gridHeight];
            _scratch = new double[_gridWidth * _gridHeight];
            _sharp = new double[_gridWidth * _gridHeight];
            _wing = new double[_gridWidth * _gridHeight];
            // Sized for the finest coarse grid, a bin of two.
            _coarse = new double[((_gridWidth + 1) / 2) * ((_gridHeight + 1) / 2)];
            _coarseScratch = new double[_coarse.Length];
        }

        public void Evaluate(ReadOnlySpan<double> p, Span<double> destination)
        {
            var (x0, y0, a, theta, k, sigma, brightness, sky) = (p[0], p[1], Math.Abs(p[2]), p[3], p[4], Math.Abs(p[5]), p[6], p[7]);
            var (c2, c4, c1) = (p[8], p[9], p[10]);
            var (halo, ratio) = (HaloFraction(p[11]), HaloRatio(p[12]));
            var b = a * _options.AxisRatio;
            var (cos, sin) = (Math.Cos(theta), Math.Sin(theta));
            var phase = _sunSide == 0 ? 0 : _options.PhaseAngleDeg * Math.PI / 180;
            var tilt = _options.SunTiltDeg * Math.PI / 180;
            var (sinD, cosD) = Math.SinCos(_options.SubObserverLatitudeDeg * Math.PI / 180);
            // The sun in the disk frame (u along the equator, v along the axis toward the end the axis angle points to, which
            // the model takes as north, w toward the observer).
            var (su, sv, sw) = (_sunSide * Math.Sin(phase) * Math.Cos(tilt), Math.Sin(phase) * Math.Sin(tilt), Math.Cos(phase));
            var cell = 1 / Supersample;

            // Every cell is its own, so rows render in parallel bands with the same bits as one walk.
            ParallelFor.RunBands(_gridHeight, (firstRow, endRow) =>
            {
                for (var gy = firstRow; gy < endRow; gy++)
                {
                    // The cell's centre in image pixels, whose centres sit at integer coordinates.
                    var y = _gridY0 + ((gy + 0.5) * cell) - 0.5;
                    for (var gx = 0; gx < _gridWidth; gx++)
                    {
                        var x = _gridX0 + ((gx + 0.5) * cell) - 0.5;
                        var (dx, dy) = (x - x0, y - y0);
                        // Axis along theta: v is the along-axis coordinate, u the across (equatorial) one.
                        var v = ((dx * cos) + (dy * sin)) / b;
                        var u = ((-dx * sin) + (dy * cos)) / a;
                        var rho2 = (u * u) + (v * v);
                        // The fraction of the cell inside the limb, from the signed distance to it (to first order, (1 - rho^2)
                        // over the gradient of rho^2, in pixels): a cell the limb crosses is lit in part, so the model moves
                        // smoothly with the limb instead of jumping as it crosses a cell's centre.
                        var gradient = 2 * Math.Sqrt((u * u / (a * a)) + (v * v / (b * b)));
                        var coverage = gradient > 0 ? Math.Clamp(((1 - rho2) / gradient / cell) + 0.5, 0, 1) : 1;
                        double value = 0;
                        if (coverage > 0)
                        {
                            // A cell the limb crosses takes the brightness just inside it.
                            var mu = Math.Sqrt(Math.Max(1 - rho2, 0));
                            var mu0 = (u * su) + (v * sv) + (mu * sw);
                            if (mu0 > 0)
                            {
                                // The sine of the point's latitude on the unit sphere: its along-axis coordinate turned by the
                                // latitude the observer looks from.
                                var z = Math.Clamp((v * cosD) + (mu * sinD), -1, 1);
                                var z2 = z * z;
                                var albedo = 1 + (c1 * z) + (c2 * z2) + (c4 * z2 * z2);
                                value = coverage * albedo * Math.Exp((k * Math.Log(mu0)) + ((k - 1) * Math.Log(Math.Max(mu, 1e-3))));
                            }
                        }
                        _grid[(gy * _gridWidth) + gx] = value;
                    }
                }
            });

            // The core and the wing, each a separable Gaussian over the same sharp model.
            Array.Copy(_grid, _sharp, _grid.Length);
            Blur(_grid, _scratch, _gridWidth, _gridHeight, sigma * Supersample);
            if (halo > 0)
            {
                Wing(ratio * sigma * Supersample);
                for (var i = 0; i < _grid.Length; i++)
                {
                    _grid[i] = ((1 - halo) * _grid[i]) + (halo * _wing[i]);
                }
            }

            for (var i = 0; i < _pixels.Count; i++)
            {
                var px = _pixels[i] % _width;
                var py = _pixels[i] / _width;
                // The pixel's supersampled cells, averaged: the pixel spans [px - 0.5, px + 0.5).
                var gx0 = (int)((px - _gridX0) * Supersample);
                var gy0 = (int)((py - _gridY0) * Supersample);
                double sum = 0;
                for (var sy = 0; sy < Supersample; sy++)
                {
                    var row = (gy0 + sy) * _gridWidth;
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        sum += _grid[row + gx0 + sx];
                    }
                }
                destination[i] = sky + (brightness * sum / (Supersample * Supersample));
            }
        }

        // The sharp model blurred by the wing's Gaussian of `sigma` cells, into the wing grid. A wing of a few cells is blurred
        // where it is; a wider one is binned by up to MaxWingBin first, blurred there, and read back bilinearly (the bin's own
        // box adds a twelfth of its square to a variance many times larger). With the brightness taken as one Exp of two logs
        // instead of two Pows, measured together: a fit of the 2022-09-03 Red stack (a 49 px disk, Release) went from 61.5 to
        // 16.8 s, its centre moving 0.002 px and its radius 0.07 %.
        private void Wing(double sigma)
        {
            var bin = Math.Clamp((int)(sigma / 2), 1, MaxWingBin);
            if (bin == 1)
            {
                Array.Copy(_sharp, _wing, _sharp.Length);
                Blur(_wing, _scratch, _gridWidth, _gridHeight, sigma);
                return;
            }
            var (cw, ch) = ((_gridWidth + bin - 1) / bin, (_gridHeight + bin - 1) / bin);
            ParallelFor.RunBands(ch, (firstRow, endRow) =>
            {
                for (var cy = firstRow; cy < endRow; cy++)
                {
                    var y1 = Math.Min((cy + 1) * bin, _gridHeight);
                    for (var cx = 0; cx < cw; cx++)
                    {
                        var x1 = Math.Min((cx + 1) * bin, _gridWidth);
                        double sum = 0;
                        var count = 0;
                        for (var y = cy * bin; y < y1; y++)
                        {
                            for (var x = cx * bin; x < x1; x++)
                            {
                                sum += _sharp[(y * _gridWidth) + x];
                                count++;
                            }
                        }
                        _coarse[(cy * cw) + cx] = sum / count;
                    }
                }
            });
            Blur(_coarse, _coarseScratch, cw, ch, sigma / bin);
            // A cell's centre (x + 0.5) lies at (x + 0.5) / bin - 0.5 on the coarse grid.
            ParallelFor.RunBands(_gridHeight, (firstRow, endRow) =>
            {
                for (var y = firstRow; y < endRow; y++)
                {
                    var fy = Math.Clamp(((y + 0.5) / bin) - 0.5, 0, ch - 1);
                    var y0 = Math.Min((int)fy, ch - 2 < 0 ? 0 : ch - 2);
                    var ty = ch > 1 ? fy - y0 : 0;
                    for (var x = 0; x < _gridWidth; x++)
                    {
                        var fx = Math.Clamp(((x + 0.5) / bin) - 0.5, 0, cw - 1);
                        var x0 = Math.Min((int)fx, cw - 2 < 0 ? 0 : cw - 2);
                        var tx = cw > 1 ? fx - x0 : 0;
                        var x1 = Math.Min(x0 + 1, cw - 1);
                        var y1 = Math.Min(y0 + 1, ch - 1);
                        var top = (_coarse[(y0 * cw) + x0] * (1 - tx)) + (_coarse[(y0 * cw) + x1] * tx);
                        var bottom = (_coarse[(y1 * cw) + x0] * (1 - tx)) + (_coarse[(y1 * cw) + x1] * tx);
                        _wing[(y * _gridWidth) + x] = (top * (1 - ty)) + (bottom * ty);
                    }
                }
            });
        }

        // A separable Gaussian of `sigma` cells, in place on `grid` (`width` by `height`), through `scratch`. Each pass runs its
        // rows in parallel bands, a tap clamped to the edge only where it would leave the grid, and the column pass adds whole
        // rows a tap at a time: every output still sums its taps from first to last, so the bits are one plain walk's.
        private static void Blur(double[] grid, double[] scratch, int width, int height, double sigma)
        {
            if (sigma < 0.05)
            {
                return;
            }
            var radius = Math.Min((int)Math.Ceiling(3.5 * sigma), Math.Max(width, height));
            var size = (2 * radius) + 1;
            var kernel = new double[size];
            double total = 0;
            for (var t = -radius; t <= radius; t++)
            {
                kernel[t + radius] = Math.Exp(-0.5 * t * t / (sigma * sigma));
                total += kernel[t + radius];
            }
            for (var t = 0; t < kernel.Length; t++)
            {
                kernel[t] /= total;
            }
            // Rows into the scratch, then columns back.
            ParallelFor.RunBands(height, (firstRow, endRow) =>
            {
                for (var y = firstRow; y < endRow; y++)
                {
                    var source = grid.AsSpan(y * width, width);
                    var target = scratch.AsSpan(y * width, width);
                    for (var x = 0; x < width; x++)
                    {
                        double s = 0;
                        if (x >= radius && x + radius < width)
                        {
                            var window = source.Slice(x - radius, size);
                            for (var t = 0; t < size; t++)
                            {
                                s += kernel[t] * window[t];
                            }
                        }
                        else
                        {
                            for (var t = -radius; t <= radius; t++)
                            {
                                s += kernel[t + radius] * source[Math.Clamp(x + t, 0, width - 1)];
                            }
                        }
                        target[x] = s;
                    }
                }
            });
            ParallelFor.RunBands(height, (firstRow, endRow) =>
            {
                for (var y = firstRow; y < endRow; y++)
                {
                    var target = grid.AsSpan(y * width, width);
                    target.Clear();
                    for (var t = -radius; t <= radius; t++)
                    {
                        var weight = kernel[t + radius];
                        var source = scratch.AsSpan(Math.Clamp(y + t, 0, height - 1) * width, width);
                        for (var x = 0; x < width; x++)
                        {
                            target[x] += weight * source[x];
                        }
                    }
                }
            });
        }
    }
}
