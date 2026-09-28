using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
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
public sealed record LimbFitOptions(double AxisRatio, double PhaseAngleDeg = 0, double AnnulusInner = 0.8, double AnnulusOuter = 1.2,
    int? SunSide = null);

/// <summary>
/// A disk fitted at its limb: where the planet is, how big, and how it lies in the image. Angles in degrees, image
/// convention (from +x toward +y).
/// </summary>
/// <param name="EquatorialRadius">Where the unblurred disk's brightness reaches zero along the equator, in pixels: the
/// planet's own edge, which WinJUPOS's outline marks, never the blurred image's steepest point, which lies inside it.</param>
/// <param name="AxisAngleDeg">The direction of the planet's axis in the image (modulo 180).</param>
/// <param name="LimbDarkening">The Minnaert exponent k (brightness mu0^k mu^(k-1)).</param>
/// <param name="PsfSigma">The Gaussian blur the edge shows, in pixels.</param>
/// <param name="SunSide">+1 or -1: which end of the equator the sun lights (0 when no phase was modelled).</param>
/// <param name="StandardErrors">The fit's standard errors, in the order centre x, centre y, radius, axis angle (degrees),
/// limb darkening, PSF sigma, brightness, sky.</param>
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
    bool Converged);

/// <summary>
/// Fits a planet's disk at its limb with a forward model (docs/plans/planetary-restoration.md, R1): an oblate disk of the
/// ephemeris' axis ratio, limb-darkened by Minnaert's law and lit at the ephemeris' phase, blurred by a Gaussian PSF,
/// over the pixels around the limb. Never the centre of mass, which a bright belt or the Great Red Spot moves, and never
/// an edge detector: Jupiter darkens to its limb, so the blurred image's steepest point lies a couple of pixels inside it
/// (measured on the 2022 stacks WinJUPOS measured).
/// </summary>
public static class PlanetaryLimbFit
{
    private const double Supersample = 2;

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
        => new LimbFitOptions(ApparentAxisRatio(aspect.Flattening, aspect.SubObserverLatitudeCentric), aspect.PhaseAngle);

    /// <summary>
    /// Fits the disk in <paramref name="image"/>: its luminance (the mean of its channels), started from <see cref="Start"/>.
    /// Null when no disk stands out of the sky, or it leaves too few pixels around the limb inside the frame.
    /// </summary>
    public static LimbFit? Fit(Image image, LimbFitOptions options)
    {
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
        return Start(plane, image.Width, image.Height, options.AxisRatio) is { } start
            ? Fit(plane, image.Width, image.Height, start.X, start.Y, start.Radius, options)
            : null;
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
                var seed = new double[] { ((coarse.CenterX + 0.5) * bin) - 0.5, ((coarse.CenterY + 0.5) * bin) - 0.5, coarse.EquatorialRadius * bin,
                    coarse.AxisAngleDeg * Math.PI / 180, coarse.LimbDarkening, Math.Sqrt(Math.Max((coarse.PsfSigma * coarse.PsfSigma * bin * bin) - ((bin * bin) - 1) / 12.0, 0.25)),
                    coarse.Brightness, coarse.Sky };
                return FitAt(plane, width, height, seed[0], seed[1], seed[2], options, (seed, coarse.SunSide), 30);
            }
        }
        return FitAt(plane, width, height, startX, startY, startRadius, options, null, 200);
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
        // fitted and the better kept (a seed carries the side its coarse fit chose); with no phase there is one fit.
        var sides = seed is { } given ? [given.SunSide]
            : options.PhaseAngleDeg <= 0 ? [0]
            : options.SunSide is { } known ? [known]
            : new[] { 1, -1 };
        var startAxisAngleDeg = seed is null ? AxisFromShape(plane, width, height, startX, startY, startRadius, (sky + peak) / 2) : 0;

        LimbFit? best = null;
        foreach (var side in sides)
        {
            var model = new DiskModel(pixels, width, height, options, side, startX, startY, startRadius);
            Span<double> start = seed is { } s ? s.Parameters : [startX, startY, startRadius, startAxisAngleDeg * Math.PI / 180, 0.9, 1.5, peak - sky, sky];
            Span<double> step = [1e-3, 1e-3, 1e-3, 1e-5, 1e-4, 1e-4, 1e-4 * Math.Max(peak - sky, 1e-6), 1e-4 * Math.Max(peak - sky, 1e-6)];
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
            var candidate = new LimbFit(q[0], q[1], Math.Abs(q[2]), Normalise(q[3] * 180 / Math.PI), q[4], Math.Abs(q[5]), q[6], q[7],
                side, Math.Sqrt(2 * fit.Cost / observed.Length), observed.Length, errors, fit.Iterations, fit.Converged);
            if (best is not { } current || candidate.RmsResidual < current.RmsResidual)
            {
                best = candidate;
            }
        }
        return best;
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
    /// brightness, sky.
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
        }

        public void Evaluate(ReadOnlySpan<double> p, Span<double> destination)
        {
            var (x0, y0, a, theta, k, sigma, brightness, sky) = (p[0], p[1], Math.Abs(p[2]), p[3], p[4], Math.Abs(p[5]), p[6], p[7]);
            var b = a * _options.AxisRatio;
            var (cos, sin) = (Math.Cos(theta), Math.Sin(theta));
            var phase = _sunSide == 0 ? 0 : _options.PhaseAngleDeg * Math.PI / 180;
            // The sun in the disk frame (u along the equator, v along the axis, w toward the observer), near the equator.
            var (su, sw) = (_sunSide * Math.Sin(phase), Math.Cos(phase));
            var cell = 1 / Supersample;

            for (var gy = 0; gy < _gridHeight; gy++)
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
                    double value = 0;
                    if (rho2 < 1)
                    {
                        var mu = Math.Sqrt(1 - rho2);
                        var mu0 = (u * su) + (mu * sw);
                        if (mu0 > 0)
                        {
                            value = Math.Pow(mu0, k) * Math.Pow(Math.Max(mu, 1e-3), k - 1);
                        }
                    }
                    _grid[(gy * _gridWidth) + gx] = value;
                }
            }

            Blur(sigma * Supersample);

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

        // A separable Gaussian of `sigma` cells, in place on the grid.
        private void Blur(double sigma)
        {
            if (sigma < 0.05)
            {
                return;
            }
            var radius = (int)Math.Ceiling(3.5 * sigma);
            Span<double> kernel = stackalloc double[(2 * radius) + 1];
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
            for (var y = 0; y < _gridHeight; y++)
            {
                var row = y * _gridWidth;
                for (var x = 0; x < _gridWidth; x++)
                {
                    double s = 0;
                    for (var t = -radius; t <= radius; t++)
                    {
                        var xx = Math.Clamp(x + t, 0, _gridWidth - 1);
                        s += kernel[t + radius] * _grid[row + xx];
                    }
                    _scratch[row + x] = s;
                }
            }
            for (var y = 0; y < _gridHeight; y++)
            {
                for (var x = 0; x < _gridWidth; x++)
                {
                    double s = 0;
                    for (var t = -radius; t <= radius; t++)
                    {
                        var yy = Math.Clamp(y + t, 0, _gridHeight - 1);
                        s += kernel[t + radius] * _scratch[(yy * _gridWidth) + x];
                    }
                    _grid[(y * _gridWidth) + x] = s;
                }
            }
        }
    }
}
