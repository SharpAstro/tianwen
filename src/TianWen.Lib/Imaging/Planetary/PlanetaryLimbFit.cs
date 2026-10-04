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
/// <param name="Rings">Saturn's rings, in the model with the globe (S2 of docs/plans/planetary-restoration.md, #1232): their edges and
/// optical depths are these, their tilt the observer's latitude, and each one's brightness is fitted. Null for a planet without rings.</param>
public sealed record LimbFitOptions(double AxisRatio, double PhaseAngleDeg = 0, double AnnulusInner = 0.8, double AnnulusOuter = 1.2,
    int? SunSide = null, double SunTiltDeg = 0, double SubObserverLatitudeDeg = 0, SaturnRings? Rings = null);

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
/// <param name="RingLevels">Each of <see cref="LimbFitOptions.Rings"/>' brightness over the globe's (<paramref name="Brightness"/>), in
/// their order, at the ring's middle; null without rings.</param>
/// <param name="RingSlopes">Each ring's brightness across its width, in [-1, 1]: its level at its outer edge is the middle's times one plus
/// this, at its inner edge one minus (S4, #1184); null without rings. A B ring brightens toward the Cassini division and an A ring dims
/// outward; with one flat level each, the fit stretched the globe to follow that light, 1.1 % large under a 6 px seeing.</param>
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
    double HaloWidth = 0,
    double[]? RingLevels = null,
    double[]? RingSlopes = null);

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
    /// Why what reads the fit cannot stand for <paramref name="planet"/> yet, or null where it can. Without its rings in the model a fit
    /// swallowed Saturn's: on 2021-12-16 it put the globe's radius at 28.1 px, half again the 18.6 px the ephemeris and the plate scale
    /// give, with a 9 px blur. The rings are in the model now (S2, #1232, <see cref="OptionsFor"/>), and the derived sharpening and the
    /// metrics read around them (S4, #1184, <see cref="MetricDisk.Rings"/>); a de-rotation and the colour alignment still work on the globe
    /// alone (S5, #1234), and decline Saturn until then.
    /// </summary>
    public static string? Unmodelled(CatalogIndex planet) => planet is CatalogIndex.Saturn
        ? "Saturn's rings are not yet carried through a de-rotation or the colour alignment (#1234)"
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

    /// <summary>
    /// The options the ephemeris gives for <paramref name="aspect"/>. Saturn's carry its rings, and the fitted pixels reach past the A
    /// ring's outer edge by the same fifth of a radius Jupiter's annulus reaches past its limb.
    /// </summary>
    public static LimbFitOptions OptionsFor(in PlanetAspect aspect)
    {
        var (west, north, _) = PhysicalEphemeris.SunOnTheDisk(aspect);
        var options = new LimbFitOptions(ApparentAxisRatio(aspect.Flattening, aspect.SubObserverLatitudeCentric), aspect.PhaseAngle,
            SunTiltDeg: Math.Atan2(north, Math.Abs(west)) * 180 / Math.PI, SubObserverLatitudeDeg: aspect.SubObserverLatitudeCentric);
        return aspect.Planet == CatalogIndex.Saturn
            ? options with { Rings = SaturnRings.Main, AnnulusOuter = SaturnRings.Main.OuterRadii + 0.2 }
            : options;
    }

    /// <summary>
    /// Fits the disk in <paramref name="image"/>: its luminance (the mean of its channels), started from <see cref="Start"/>.
    /// Null when no disk stands out of the sky, or it leaves too few pixels around the limb inside the frame.
    /// </summary>
    public static LimbFit? Fit(Image image, LimbFitOptions options)
    {
        var plane = Luminance(image);
        var start = options.Rings is { } rings ? StartRinged(plane, image.Width, image.Height, rings) : Start(plane, image.Width, image.Height, options.AxisRatio);
        return start is { } at ? Fit(plane, image.Width, image.Height, at.X, at.Y, at.Radius, options) : null;
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
    /// Where a fit of a ringed planet starts (S2, #1232): the centroid of the pixels above a quarter of the way from the sky to the
    /// brightest, as <see cref="Start"/> takes it, and the globe's radius from how far those pixels reach along their long axis, which is
    /// the rings'. The area <see cref="Start"/> reads a radius from is the rings' as much as the globe's. The reach is the 99.5th
    /// percentile of the pixels' distances along the long axis, the outer ring's edge less a little; the fit takes it from there. Only
    /// the largest connected group of those pixels is read: the 2022-10-09 colour stack carries a line at full brightness across its
    /// top rows, which took the long axis and put the start at 70 px for a globe of 19. Null when nothing stands out of the sky.
    /// </summary>
    public static (double X, double Y, double Radius)? StartRinged(ReadOnlySpan<float> plane, int width, int height, SaturnRings rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var copy = plane.ToArray();
        var sky = StatisticsHelper.NthSmallest(copy, (int)(0.05 * (copy.Length - 1)));
        var disk = StatisticsHelper.NthSmallest(copy, (int)(0.99 * (copy.Length - 1)));
        if (!(disk > sky))
        {
            return null;
        }
        var level = sky + (0.25 * (disk - sky));
        var blob = LargestBlob(plane, width, height, level);
        if (blob.Count < 50)
        {
            return null;
        }
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        foreach (var index in blob)
        {
            var (x, y) = (index % width, index / width);
            sx += x;
            sy += y;
            sxx += (double)x * x;
            syy += (double)y * y;
            sxy += (double)x * y;
        }
        var n = (double)blob.Count;
        var (mx, my) = (sx / n, sy / n);
        var major = 0.5 * Math.Atan2(2 * ((sxy / n) - (mx * my)), ((sxx / n) - (mx * mx)) - ((syy / n) - (my * my)));
        var (cos, sin) = (Math.Cos(major), Math.Sin(major));
        var reach = new double[blob.Count];
        for (var i = 0; i < reach.Length; i++)
        {
            var (x, y) = (blob[i] % width, blob[i] / width);
            reach[i] = Math.Abs(((x - mx) * cos) + ((y - my) * sin));
        }
        var extent = StatisticsHelper.NthSmallest(reach, (int)(0.995 * (reach.Length - 1)));
        return (mx, my, extent / rings.OuterRadii);
    }

    // The largest group of pixels above `level` joined by their edges, as indices into the plane.
    private static List<int> LargestBlob(ReadOnlySpan<float> plane, int width, int height, double level)
    {
        var seen = new bool[plane.Length];
        var best = new List<int>();
        var queue = new Queue<int>();
        for (var start = 0; start < plane.Length; start++)
        {
            if (seen[start] || !(plane[start] > level))
            {
                continue;
            }
            var group = new List<int>();
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out var index))
            {
                group.Add(index);
                var (x, y) = (index % width, index / width);
                Visit(plane, x > 0 ? index - 1 : -1);
                Visit(plane, x < width - 1 ? index + 1 : -1);
                Visit(plane, y > 0 ? index - width : -1);
                Visit(plane, y < height - 1 ? index + width : -1);
            }
            if (group.Count > best.Count)
            {
                best = group;
            }
        }
        return best;

        void Visit(ReadOnlySpan<float> values, int index)
        {
            if (index >= 0 && !seen[index] && values[index] > level)
            {
                seen[index] = true;
                queue.Enqueue(index);
            }
        }
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
                var seed = SeedFrom(coarse, options, ((coarse.CenterX + 0.5) * bin) - 0.5, ((coarse.CenterY + 0.5) * bin) - 0.5, coarse.EquatorialRadius * bin,
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
        var seed = SeedFrom(like, options, startX, startY, like.EquatorialRadius, like.PsfSigma);
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
        // Every pixel the disk (and its rings) reach, inside the model's own grid (which reaches 12 px past its annulus).
        var outer = options.Rings is { } rings ? rings.OuterRadii + 0.05 : 1.05;
        var cover = options with { AnnulusOuter = outer };
        var reach = (int)Math.Ceiling((outer * fit.EquatorialRadius) + 2);
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
        var p = SeedFrom(fit, options, fit.CenterX, fit.CenterY, fit.EquatorialRadius, 0);
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
    private static double[] SeedFrom(in LimbFit fit, LimbFitOptions options, double centerX, double centerY, double radius, double sigma)
    {
        var sloped = DiskModel.Sloped(options.Rings?.Rings ?? []);
        var slopes = new double[sloped.Length];
        for (var j = 0; j < sloped.Length; j++)
        {
            slopes[j] = fit.RingSlopes is { } fitted && sloped[j] < fitted.Length ? fitted[sloped[j]] : 0;
        }
        return [centerX, centerY, radius, fit.NorthAngleDeg * Math.PI / 180, fit.LimbDarkening, sigma, fit.Brightness, fit.Sky,
            fit.ZonalAlbedo2, fit.ZonalAlbedo4, fit.ZonalAlbedo1, 2 * fit.HaloFraction, fit.PsfSigma > 0 ? fit.HaloWidth / fit.PsfSigma : 3,
            .. fit.RingLevels ?? [], .. slopes];
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
        // Rings tell the ends apart too: their near half crosses the globe on the pole turned away, the far half goes behind it.
        var ends = seed is not null || ((options.PhaseAngleDeg <= 0 || options.SunTiltDeg == 0) && options.Rings is null) ? [0.0] : new[] { 0.0, Math.PI };
        var rings = options.Rings?.Rings ?? [];

        // The searches are independent, each with its own model, so they run at once; the best is chosen in their order.
        (int Side, double End)[] combinations = [.. Combinations(sides, ends)];
        var candidates = new LimbFit[combinations.Length];
        ParallelFor.Run(combinations.Length, c =>
        {
            var (side, end) = combinations[c];
            var model = new DiskModel(pixels, width, height, options, side, startX, startY, startRadius);
            var start = seed is { } s ? s.Parameters
                : [startX, startY, startRadius, (startAxisAngleDeg * Math.PI / 180) + end, 0.9, 1.5, peak - sky, sky, 0, 0, 0, 0.05, 3, .. RingLevels(rings), .. new double[DiskModel.Sloped(rings).Length]];
            double[] step = [1e-3, 1e-3, 1e-3, 1e-5, 1e-4, 1e-4, 1e-4 * Math.Max(peak - sky, 1e-6), 1e-4 * Math.Max(peak - sky, 1e-6), 1e-4, 1e-4, 1e-4, 1e-4, 1e-3,
                .. RingSteps(rings.Length), .. RingSteps(DiskModel.Sloped(rings).Length)];
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
                DiskModel.HaloFraction(q[11]), DiskModel.HaloRatio(q[12]) * Math.Abs(q[5]),
                rings.Length > 0 ? q[DiskModel.GlobeParameters..(DiskModel.GlobeParameters + rings.Length)] : null,
                rings.Length > 0 ? DiskModel.SlopesOf(rings, q[(DiskModel.GlobeParameters + rings.Length)..]) : null);
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

    // The rings' levels a search starts from: the nominal ones.
    private static double[] RingLevels(System.Collections.Immutable.ImmutableArray<SaturnRing> rings)
    {
        var levels = new double[rings.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            levels[i] = rings[i].Level;
        }
        return levels;
    }

    private static double[] RingSteps(int count)
    {
        var steps = new double[count];
        Array.Fill(steps, 1e-4);
        return steps;
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
    /// blur's wing: its share and its width over the core's; then, with rings, each ring's brightness over the globe's at its middle, and
    /// each one's slope across its width (<see cref="LimbFit.RingSlopes"/>).
    /// <para>
    /// The rings (S2, #1232) are annuli in the equatorial plane, seen at the observer's latitude: a cell at (u, v) on the sky, in
    /// equatorial radii with v toward the model's north, crosses the plane at radius <c>sqrt(u^2 + v^2 / sin^2 D)</c>. A cell is lit by the
    /// fraction of it inside each edge, as the limb's cells are, so the model moves smoothly as an edge crosses a cell. The half of the
    /// rings nearer the observer (on the pole turned away) lies over the globe and lets through <see cref="SaturnRing.Transmission"/> of
    /// it; the globe hides the far half. A face the Sun does not light is dark. Neither body's shadow on the other is drawn.
    /// </para>
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
        /// <summary>The globe's parameters, which the rings' levels follow.</summary>
        public const int GlobeParameters = 13;

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
        // The rings' edges (equatorial radii) and how much of the globe behind each one shows.
        private readonly double[] _ringInner;
        private readonly double[] _ringOuter;
        private readonly double[] _ringTransmission;
        private readonly double[] _ringSunTransmission;
        private readonly double[] _ringLevels;
        private readonly double[] _ringSlopes;
        private readonly int[] _sloped;
        // The Sun in the body frame (e1 along the equator, e2 the equator's direction nearest the observer, e3 the model's north
        // pole), the spheroid's polar over equatorial radius, and the Sun's direction in the frame where the spheroid is a unit sphere.
        private readonly double _sun1, _sun2, _sun3, _q;
        private readonly double _hat1, _hat2;

        // The wing is blurred on a grid coarser by up to this: it is smooth by construction, at least twice the core.
        private const int MaxWingBin = 16;

        // The wing's share, kept in [0, 0.5), and its width over the core's, kept in [1, 64], whatever the search tries: an
        // unbounded width once asked for a kernel wider than the stack could hold. A bound of 8 was too tight: a Moffat of
        // beta 2 (T1's heaviest seeing) held the wing at both bounds and moved the radius 0.6 %.
        public static double HaloFraction(double raw) => 0.5 * Math.Clamp(raw, 0, 0.999);

        public static double HaloRatio(double raw) => 1 + Math.Min(Math.Abs(raw - 1), 63);

        // A ring's slope across its width, held in [-1, 1] so neither edge is brighter than twice the middle nor below nothing.
        public static double RingSlope(double raw) => Math.Clamp(raw, -1, 1);

        // The rings whose slope is fitted: B and A, bright and wide enough for their structure to move the fit (a nominal level of 0.3 or
        // more). C and the Cassini division stay flat: faint and a few pixels wide, their slopes ran away (the C ring's level to -4e5 under
        // a 6 px seeing) and took the radius with them.
        public static int[] Sloped(System.Collections.Immutable.ImmutableArray<SaturnRing> rings)
        {
            var sloped = new List<int>();
            for (var i = 0; i < rings.Length; i++)
            {
                if (rings[i].Level >= 0.3)
                {
                    sloped.Add(i);
                }
            }
            return [.. sloped];
        }

        // Every ring's slope from the fitted ones (`fitted`, Sloped's order), zero for a ring held flat.
        public static double[] SlopesOf(System.Collections.Immutable.ImmutableArray<SaturnRing> rings, ReadOnlySpan<double> fitted)
        {
            var (slopes, sloped) = (new double[rings.Length], Sloped(rings));
            for (var j = 0; j < sloped.Length && j < fitted.Length; j++)
            {
                slopes[sloped[j]] = RingSlope(fitted[j]);
            }
            return slopes;
        }

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

            var rings = options.Rings is { } ringed && Math.Abs(Math.Sin(options.SubObserverLatitudeDeg * Math.PI / 180)) > 0.01 ? ringed.Rings : [];
            var equatorial = PhysicalEphemeris.Radii(CatalogIndex.Saturn).Equatorial;
            var sinB = Math.Sin(options.SubObserverLatitudeDeg * Math.PI / 180);
            _ringInner = new double[rings.Length];
            _ringOuter = new double[rings.Length];
            _ringTransmission = new double[rings.Length];
            _ringSunTransmission = new double[rings.Length];
            _ringLevels = new double[rings.Length];
            _ringSlopes = new double[rings.Length];
            _sloped = Sloped(rings);

            var phase = sunSide == 0 ? 0 : options.PhaseAngleDeg * Math.PI / 180;
            var tilt = options.SunTiltDeg * Math.PI / 180;
            var cosB = Math.Cos(options.SubObserverLatitudeDeg * Math.PI / 180);
            var (su, sv, sw) = (sunSide * Math.Sin(phase) * Math.Cos(tilt), Math.Sin(phase) * Math.Sin(tilt), Math.Cos(phase));
            (_sun1, _sun2, _sun3) = (su, (sw * cosB) - (sv * sinB), (sw * sinB) + (sv * cosB));
            // The apparent axis ratio is sqrt(sin^2 B + q^2 cos^2 B).
            _q = Math.Sqrt(Math.Max((options.AxisRatio * options.AxisRatio) - (sinB * sinB), 1e-6)) / Math.Max(Math.Abs(cosB), 1e-6);
            var hatNorm = Math.Sqrt((_sun1 * _sun1) + (_sun2 * _sun2) + (_sun3 * _sun3 / (_q * _q)));
            (_hat1, _hat2) = (_sun1 / hatNorm, _sun2 / hatNorm);
            for (var i = 0; i < rings.Length; i++)
            {
                (_ringInner[i], _ringOuter[i], _ringTransmission[i], _ringSunTransmission[i]) =
                    (rings[i].InnerKm / equatorial, rings[i].OuterKm / equatorial, rings[i].Transmission(sinB), rings[i].Transmission(_sun3));
            }
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
            // The rings' face toward the observer is lit when the Sun stands on the same side of their plane.
            var ringsLit = Math.Sign((sw * sinD) + (sv * cosD)) == Math.Sign(sinD);
            var axisRatio = _options.AxisRatio;
            var ringCount = _ringInner.Length;
            // The levels where the parallel bands can read them: a span cannot be captured.
            p.Slice(GlobeParameters, ringCount).CopyTo(_ringLevels);
            for (var j = 0; j < _sloped.Length; j++)
            {
                _ringSlopes[_sloped[j]] = p.Length > GlobeParameters + ringCount + j ? RingSlope(p[GlobeParameters + ringCount + j]) : 0;
            }

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
                        if (ringCount > 0)
                        {
                            value = Ringed(value, coverage, u, v * axisRatio, a, sinD, cosD, cell, ringsLit);
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

        // A cell's brightness with the rings: the globe's (`globe`, unshadowed, the cell `coverage` of it on the disk) and each ring's
        // share of the cell. (u, v) is the cell on the sky in equatorial radii, v toward the model's north; the line of sight crosses the
        // plane at r^2 = u^2 + v^2 / sin^2 D, whose gradient, in pixels, is 2 / a sqrt(u^2 + (v / sin^2 D)^2). The near half (v sin D < 0)
        // lies over the globe, passing exp(-tau / sin B) of it; the globe hides the far half and shades the rings; the rings shade the
        // globe, passing exp(-tau / sin B') of the Sun's light.
        // A ring's band seen against the globe and its shadow on the globe share their edges when the Sun is near the observer, and lie
        // nearly parallel otherwise, so within a cell the two shares of one ring are taken as nested: the cell's part both covered and
        // shaded is the smaller share. Their product, as if independent, darkened every such edge a second time (+0.3 % on the radius,
        // lit from the observer). The same holds behind the globe for its outline and its shadow on the far half.
        private double Ringed(double globe, double coverage, double u, double v, double a, double sinD, double cosD, double cell, bool lit)
        {
            var sin2 = sinD * sinD;
            var r2 = (u * u) + (v * v / sin2);
            var gradient = 2 / a * Math.Sqrt((u * u) + (v * v / (sin2 * sin2)));

            // Where the ray toward the Sun from the globe's point under the cell crosses the plane, and r^2's change across a cell there.
            var sun2 = globe > 0 ? SunwardRadius2(u, v, sinD, cosD) : -1;
            var step = 0.0;
            if (sun2 >= 0)
            {
                var (du, dv) = (SunwardRadius2(u + (cell / a), v, sinD, cosD), SunwardRadius2(u, v + (cell / a), sinD, cosD));
                step = Math.Sqrt(((du < 0 ? 0 : du - sun2) * (du < 0 ? 0 : du - sun2)) + ((dv < 0 ? 0 : dv - sun2) * (dv < 0 ? 0 : dv - sun2)));
            }

            var near = v * sinD < 0;
            var radius = Math.Sqrt(r2);
            double light = 0, through = 1;
            for (var i = 0; i < _ringInner.Length; i++)
            {
                var seen = Inside(r2, gradient, cell, _ringOuter[i]) - Inside(r2, gradient, cell, _ringInner[i]);
                var shaded = sun2 >= 0 ? Crossing(sun2, step, _ringOuter[i]) - Crossing(sun2, step, _ringInner[i]) : 0;
                if (seen > 0)
                {
                    // The ring's level at the cell's radius in its plane: its middle's, ramped across its width.
                    var across = Math.Clamp(((2 * radius) - _ringInner[i] - _ringOuter[i]) / (_ringOuter[i] - _ringInner[i]), -1, 1);
                    light += seen * (lit ? _ringLevels[i] * (1 + (_ringSlopes[i] * across)) : 0);
                }
                if (near)
                {
                    var both = Math.Min(seen, shaded);
                    var (tv, ts) = (_ringTransmission[i], _ringSunTransmission[i]);
                    through *= 1 - seen - shaded + both + ((seen - both) * tv) + ((shaded - both) * ts) + (both * tv * ts);
                }
                else
                {
                    through *= 1 - (shaded * (1 - _ringSunTransmission[i]));
                }
            }
            if (light <= 0)
            {
                return globe * through;
            }
            var sunlit = 1 - GlobesShadow(u, -v / sinD, a, sinD, cell);
            return near
                ? (light * sunlit) + (globe * through)
                : (globe * through) + (Math.Min(1 - coverage, sunlit) * light);
        }

        // The share of a cell inside the ring edge at `edge` equatorial radii, from the line of sight's r^2 and its gradient in pixels.
        private static double Inside(double r2, double gradient, double cell, double edge)
            => gradient > 0 ? Math.Clamp((((edge * edge) - r2) / gradient / cell) + 0.5, 0, 1) : (r2 < edge * edge ? 1 : 0);

        // The share of a cell whose sunward rays cross the plane inside the ring edge at `edge`, from r^2 and its change across a cell.
        private static double Crossing(double r2, double step, double edge)
            => step > 0 ? Math.Clamp((((edge * edge) - r2) / step) + 0.5, 0, 1) : (r2 < edge * edge ? 1 : 0);

        // The share of the cell at (x1, x2, 0) on the ring plane that the globe shades: the ray from it toward the Sun passes within d of
        // the centre, in the frame where the spheroid is the unit sphere, and the cell is shaded where d < 1 on the Sun's side. Its edge is
        // smoothed over the cell as the limb's is, by d's gradient in pixels (x1 = u, x2 = -v / sin B, each in equatorial radii).
        private double GlobesShadow(double x1, double x2, double a, double sinD, double cell)
        {
            var along = (x1 * _hat1) + (x2 * _hat2);
            if (along >= 0)
            {
                return 0;
            }
            var (p1, p2) = (x1 - (along * _hat1), x2 - (along * _hat2));
            var d = Math.Sqrt((p1 * p1) + (p2 * p2) + (along * along * (1 - (_hat1 * _hat1) - (_hat2 * _hat2))));
            if (d <= 0)
            {
                return 1;
            }
            var gradient = Math.Sqrt(((p1 / d / a) * (p1 / d / a)) + ((p2 / d / (a * sinD)) * (p2 / d / (a * sinD))));
            return gradient > 0 ? Math.Clamp(((1 - d) / gradient / cell) + 0.5, 0, 1) : (d < 1 ? 1 : 0);
        }

        // The squared radius, equatorial radii, at which the ray toward the Sun from where the line of sight through (u, v) meets the
        // spheroid crosses the ring plane; negative off the globe, or where the ray never crosses the plane (the Sun on the point's side).
        // The line of sight (u, v, t) meets X1^2 + X2^2 + X3^2 / q^2 = 1 at X2 = t cos B - v sin B, X3 = t sin B + v cos B, nearer root.
        private double SunwardRadius2(double u, double v, double sinD, double cosD)
        {
            if (_sun3 == 0)
            {
                return -1;
            }
            var q2Inverse = 1 / (_q * _q);
            var qa = (cosD * cosD) + (sinD * sinD * q2Inverse);
            var qb = 2 * v * sinD * cosD * (q2Inverse - 1);
            var qc = (u * u) + (v * v * sinD * sinD) + (v * v * cosD * cosD * q2Inverse) - 1;
            var discriminant = (qb * qb) - (4 * qa * qc);
            if (discriminant < 0)
            {
                return -1;
            }
            var t = (-qb + Math.Sqrt(discriminant)) / (2 * qa);
            var (x2, x3) = ((t * cosD) - (v * sinD), (t * sinD) + (v * cosD));
            var s = -x3 / _sun3;
            if (s <= 0)
            {
                return -1;
            }
            var (c1, c2) = (u + (s * _sun1), x2 + (s * _sun2));
            return (c1 * c1) + (c2 * c2);
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
