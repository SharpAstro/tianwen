using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Synthetic training pairs for R9's learned refiner (#1056, docs/plans/planetary-restoration.md, "R9 revisited"): a global map rendered
/// at a random geometry; the truth that render through a twin's own diffraction (the sharpening's target); a stack, the render through
/// the mean PSF of a kept subset of the twin's frames (<see cref="SyntheticPsfFile"/>) broadened by a Gaussian, on a sky with white noise
/// at a chosen signal to noise in the finest band; and the stack sharpened by <see cref="PlanetarySharpening"/> as a real master is.
/// </summary>
public static class PlanetaryTrainingPairs
{
    /// <summary>A twin's frames' PSFs, held to draw kept subsets from: each frame's PSF binned to the detector, best first.</summary>
    /// <param name="Name">Where it came from.</param>
    /// <param name="Header">What every frame shares.</param>
    /// <param name="Frames">Each frame's PSF on the detector grid (<paramref name="Size"/> squared), unit-sum, sharpest first.</param>
    /// <param name="Size">The detector grid's side.</param>
    /// <param name="Diffraction">The perfect telescope's PSF on the detector grid, unit-sum.</param>
    public sealed record PsfSource(string Name, SyntheticPsfHeader Header, float[][] Frames, int Size, double[] Diffraction);

    /// <summary>One pair's draw: what was chosen, for the pair's record.</summary>
    public readonly record struct PairDraw(DateTimeOffset When, double RadiusPx, double NorthDeg, double KeepFraction, double BroadenPx, double FinestBandSnr,
        double NoiseSigma, int Size);

    /// <summary>
    /// One pair: the stack, the stack sharpened as shipped, the truth (over the sky, as <see cref="PlanetaryTruth"/> reads one), the disk it
    /// was rendered at, the wavelength its PSFs are at, and its draw.
    /// </summary>
    public sealed record Pair(Image Stack, Image Sharpened, Image Truth, DiskPlacement Placement, double WavelengthNm, PairDraw Draw);

    /// <summary>The level of the disk and the sky a pair is drawn at, in the units a master's [0, 1] scale gives.</summary>
    public const double Level = 0.5;

    /// <summary>The sky under every pair.</summary>
    public const double Sky = 0.02;

    /// <summary>Reads a twin's PSF file and bins every frame to its detector grid, sharpest first (by the sum of the squared PSF).</summary>
    public static PsfSource? Load(string path)
    {
        using var reader = SyntheticPsfFile.Reader.Open(path);
        if (reader is null)
        {
            return null;
        }
        var header = reader.Header;
        var fine = new double[header.PsfGrid * header.PsfGrid];
        var frames = new List<(double Sharpness, float[] Psf)>();
        while (reader.TryRead(fine, out _, out _, out _))
        {
            var binned = Bin(fine, header.PsfGrid, header.Oversample);
            double sharpness = 0;
            foreach (var v in binned)
            {
                sharpness += v * v;
            }
            frames.Add((sharpness, binned));
        }
        var size = header.PsfGrid / header.Oversample;
        var diffraction = Bin(header.Diffraction, header.PsfGrid, header.Oversample).Select(v => (double)v).ToArray();
        return new PsfSource(System.IO.Path.GetFileNameWithoutExtension(path), header, [.. frames.OrderByDescending(f => f.Sharpness).Select(f => f.Psf)], size, diffraction);
    }

    /// <summary>
    /// One pair from <paramref name="map"/> (Minnaert's <paramref name="minnaertK"/>) through <paramref name="psf"/>, drawn by
    /// <paramref name="random"/>: a radius from <paramref name="radiusRange"/>, a keep fraction log-uniform in
    /// <paramref name="keepRange"/>, a broadening uniform in [0, <paramref name="broadenMaxPx"/>] and the finest band's signal to noise
    /// log-uniform in <paramref name="snrRange"/>. Null where the sharpening did not derive its gains.
    /// </summary>
    public static Pair? Make(PlanetMap map, double minnaertK, PsfSource psf, Random random, (double Least, double Most) radiusRange, (double Least, double Most) keepRange,
        double broadenMaxPx, (double Least, double Most) snrRange)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(psf);
        ArgumentNullException.ThrowIfNull(random);
        var when = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(random.NextDouble() * 4 * 365 * 24 * 60);
        var radius = radiusRange.Least + (random.NextDouble() * (radiusRange.Most - radiusRange.Least));
        var north = random.NextDouble() * 360;
        var keep = Math.Exp(Math.Log(keepRange.Least) + (random.NextDouble() * (Math.Log(keepRange.Most) - Math.Log(keepRange.Least))));
        var broaden = random.NextDouble() * broadenMaxPx;
        var snr = Math.Exp(Math.Log(snrRange.Least) + (random.NextDouble() * (Math.Log(snrRange.Most) - Math.Log(snrRange.Least))));

        // The frame: the disk and the PSF's reach, an even side.
        var kernelSize = psf.Size;
        var side = (2 * (int)Math.Ceiling((radius * 1.3) + (kernelSize / 2.0))) + 2;
        var placement = new DiskPlacement((side / 2.0) + (random.NextDouble() - 0.5), (side / 2.0) + (random.NextDouble() - 0.5), radius, north);
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, when);
        var sharp = PlanetaryRender.Render(map, aspect, placement, side, side, minnaertK, supersample: 4);

        // The stack's PSF: the kept frames' mean, broadened; the truth's the telescope's own diffraction.
        var kept = Math.Max(1, (int)Math.Round(keep * psf.Frames.Length));
        var stackPsf = new double[kernelSize * kernelSize];
        for (var f = 0; f < kept; f++)
        {
            var frame = psf.Frames[f];
            for (var i = 0; i < stackPsf.Length; i++)
            {
                stackPsf[i] += frame[i];
            }
        }
        stackPsf = Normalised(Broadened(stackPsf, kernelSize, broaden));
        var blurred = PlanetaryRender.ConvolveCentred(sharp, side, side, stackPsf, kernelSize);
        var truthPlane = PlanetaryRender.ConvolveCentred(sharp, side, side, Normalised(psf.Diffraction), kernelSize);

        // White noise at the finest band's signal to noise inside 0.9 radii.
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, radius);
        var signal = FinestBandRms(blurred, side, disk);
        var unit = new float[side * side];
        for (var i = 0; i < unit.Length; i++)
        {
            unit[i] = (float)PhaseScreen.Gaussian(random);
        }
        var noiseScale = FinestBandRms(unit, side, disk);
        var sigma = noiseScale > 0 ? Level * signal / (snr * noiseScale) : 0;
        var stack = new float[side, side];
        var truth = new float[side, side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var i = (y * side) + x;
                stack[y, x] = (float)((Level * blurred[i]) + Sky + (sigma * unit[i]));
                truth[y, x] = (float)(Level * truthPlane[i]);
            }
        }
        var meta = default(ImageMeta) with { ObjectName = "Jupiter", ExposureStartTime = when };
        var stackImage = new Image([stack], BitDepth.Float32, 1, 0, 0, meta);
        var options = new PlanetarySharpenOptions(CatalogIndex.Jupiter, when, psf.Header.Pupil ?? DefaultPupil)
        {
            WavelengthsNm = [psf.Header.WavelengthM * 1e9],
        };
        if (PlanetarySharpening.Sharpen(stackImage, options) is not { Derived: true } result)
        {
            return null;
        }
        return new Pair(stackImage, result.Sharpened, new Image([truth], BitDepth.Float32, 1, 0, 0, meta), placement, psf.Header.WavelengthM * 1e9,
            new PairDraw(when, radius, north, keep, broaden, snr, sigma, side));
    }

    // The 254 mm Newtonian the twins of 2022-09-03 were made through, where a PSF file names no pupil.
    private static readonly Pupil DefaultPupil = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001);

    // A fine PSF summed over oversample by oversample cells: the detector's PSF, its centre where the fine centre falls.
    private static float[] Bin(double[] fine, int grid, int oversample)
    {
        var size = grid / oversample;
        var binned = new float[size * size];
        for (var y = 0; y < size * oversample; y++)
        {
            for (var x = 0; x < size * oversample; x++)
            {
                binned[((y / oversample) * size) + (x / oversample)] += (float)fine[(y * grid) + x];
            }
        }
        return binned;
    }

    private static double[] Normalised(double[] kernel)
    {
        var sum = kernel.Sum();
        return sum > 0 ? [.. kernel.Select(v => v / sum)] : kernel;
    }

    // The kernel convolved with a Gaussian of sigma pixels, on its own grid (the edges held at zero).
    private static double[] Broadened(double[] kernel, int size, double sigma)
    {
        if (sigma < 0.05)
        {
            return kernel;
        }
        var reach = (int)Math.Ceiling(3 * sigma);
        var taps = Enumerable.Range(-reach, (2 * reach) + 1).Select(k => Math.Exp(-k * k / (2 * sigma * sigma))).ToArray();
        var total = taps.Sum();
        var rows = new double[kernel.Length];
        var result = new double[kernel.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double v = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    var xx = x + k;
                    if (xx >= 0 && xx < size)
                    {
                        v += taps[k + reach] * kernel[(y * size) + xx];
                    }
                }
                rows[(y * size) + x] = v / total;
            }
        }
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double v = 0;
                for (var k = -reach; k <= reach; k++)
                {
                    var yy = y + k;
                    if (yy >= 0 && yy < size)
                    {
                        v += taps[k + reach] * rows[(yy * size) + x];
                    }
                }
                result[(y * size) + x] = v / total;
            }
        }
        return result;
    }

    // The RMS of the finest a trous band (the plane less its B3-spline smoothing at step one) inside 0.9 radii of the disk.
    private static double FinestBandRms(float[] plane, int side, in MetricDisk disk)
    {
        ReadOnlySpan<double> b3 = [1.0 / 16, 4.0 / 16, 6.0 / 16, 4.0 / 16, 1.0 / 16];
        var rows = new double[plane.Length];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                double v = 0;
                for (var k = -2; k <= 2; k++)
                {
                    v += b3[k + 2] * plane[(y * side) + Math.Clamp(x + k, 0, side - 1)];
                }
                rows[(y * side) + x] = v;
            }
        }
        var (sum, count) = (0.0, 0);
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                if (disk.RadiiAt(x, y) >= 0.9)
                {
                    continue;
                }
                double smooth = 0;
                for (var k = -2; k <= 2; k++)
                {
                    smooth += b3[k + 2] * rows[(Math.Clamp(y + k, 0, side - 1) * side) + x];
                }
                var d = plane[(y * side) + x] - smooth;
                (sum, count) = (sum + (d * d), count + 1);
            }
        }
        return count > 0 ? Math.Sqrt(sum / count) : 0;
    }
}
