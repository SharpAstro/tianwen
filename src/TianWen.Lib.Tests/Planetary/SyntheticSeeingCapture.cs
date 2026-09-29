using System;
using System.Numerics;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Tests;

/// <summary>
/// A lucky-imaging capture rendered from the physics the aperture method must see through: a banded, limb-darkened oblate
/// disk, imaged through a pupil and a fresh Kolmogorov phase screen per frame, sampled at twice the detector's resolution and
/// binned 2 by 2 onto it (the pixel's own integration), plus Gaussian noise. Frames are independent draws: an averaged power
/// spectrum does not care how they are ordered.
/// <para>
/// A STACK, which is what the spider is looked for in, is the ensemble mean of such frames, so <see cref="RenderStack"/>
/// renders it once instead: the disk through the pupil's diffraction and Fried's tilt-removed long-exposure transfer function,
/// on a grid big enough for a real disk's size and halo. The frames' own small grid wraps its halo around its edges, and a
/// disk's four periodic copies print a four-fold pattern of their own there, aligned with the grid: exactly what the spider
/// statistic looks for, which is why the stack is not made from them.
/// </para>
/// </summary>
internal sealed class SyntheticSeeingCapture
{
    /// <summary>The detector frame's side, in pixels: small, so a test can afford the thousands of frames a real capture has.</summary>
    public const int FrameSize = 64;

    private const int Grid = FrameSize * 2;

    private readonly float[] _pupil;
    private readonly Complex[] _object;
    private readonly double _spacing;
    private readonly double _r0;
    private readonly double _noise;
    private readonly Random _random;
    private readonly double[] _phase = new double[Grid * Grid];
    private readonly double[] _psf = new double[Grid * Grid];
    private readonly Complex[] _scratch = new Complex[Grid * Grid];
    private readonly Complex[] _image = new Complex[Grid * Grid];

    /// <param name="pupil">The telescope.</param>
    /// <param name="arcsecPerPixel">The detector's scale.</param>
    /// <param name="wavelengthM">The single wavelength of the render.</param>
    /// <param name="r0M">The Fried parameter at that wavelength.</param>
    /// <param name="noise">Gaussian noise per detector pixel, the disk's centre being about 1.</param>
    /// <param name="diskRadiusPx">The disk's equatorial radius on the detector.</param>
    /// <param name="seed">The draws' seed.</param>
    public SyntheticSeeingCapture(Pupil pupil, double arcsecPerPixel, double wavelengthM, double r0M, double noise, double diskRadiusPx, int seed)
    {
        _spacing = ShortExposurePsf.PupilSpacingFor(wavelengthM, arcsecPerPixel / 2, Grid);
        _pupil = pupil.Rasterise(Grid, _spacing);
        _r0 = r0M;
        _noise = noise;
        _random = new Random(seed);
        _object = RenderObject(Grid, ObjectCenter, diskRadiusPx * 2, supersample: 4);
        Fft2D.Forward(_object, Grid, Grid);
    }

    /// <summary>
    /// Renders <paramref name="frames"/> frames, row-major one after another, in 16 chunks drawn in parallel, each with its own
    /// seed from <paramref name="seed"/>, so the result depends on the seed and never on the machine's thread count.
    /// </summary>
    public static float[] Render(Pupil pupil, double arcsecPerPixel, double wavelengthM, double r0M, double noise, double diskRadiusPx, int seed, int frames)
    {
        const int chunks = 16;
        const int pixels = FrameSize * FrameSize;
        var output = new float[frames * pixels];
        Parallel.For(0, chunks, chunk =>
        {
            var capture = new SyntheticSeeingCapture(pupil, arcsecPerPixel, wavelengthM, r0M, noise, diskRadiusPx, (seed * 1000) + chunk);
            for (var f = chunk; f < frames; f += chunks)
            {
                capture.Next(output.AsSpan(f * pixels, pixels));
            }
        });
        return output;
    }

    /// <summary>The disk's centre on a <see cref="Render"/> frame.</summary>
    public static (double X, double Y) Center => ((ObjectCenter - 0.5) / 2, (ObjectCenter - 0.5) / 2);

    // Off the grid's samples by a fraction, so nothing lines up by accident.
    private const double ObjectCenter = (Grid / 2) + 0.3;

    /// <summary>
    /// A stack of the disk, <paramref name="frameSize"/> square with the disk in its middle (at <see cref="StackCenter"/>): the
    /// ensemble mean of short exposures once their tilt is taken out, which is the pupil's diffraction under Fried's
    /// tilt-removed long-exposure transfer function <c>exp(-3.44 (lambda f / r0)^(5/3) (1 - (lambda f / D)^(1/3)))</c>, rendered at
    /// twice the detector's resolution on a grid four times the frame, so no periodic copy's halo reaches it, and binned 2 by 2.
    /// Gaussian noise of <paramref name="stackNoise"/> stands for what the frames' own noise leaves after averaging.
    /// </summary>
    public static float[] RenderStack(Pupil pupil, double arcsecPerPixel, double wavelengthM, double r0M, double diskRadiusPx, int frameSize, double stackNoise, int seed)
    {
        var grid = 4 * frameSize;
        var sampleArcsec = arcsecPerPixel / 2;
        var spacing = ShortExposurePsf.PupilSpacingFor(wavelengthM, sampleArcsec, grid);
        var raster = pupil.Rasterise(grid, spacing, supersample: 2);
        var psf = new double[grid * grid];
        ShortExposurePsf.Compute(raster, ReadOnlySpan<double>.Empty, grid, psf);

        var otf = new Complex[grid * grid];
        for (var i = 0; i < psf.Length; i++)
        {
            otf[i] = psf[i];
        }
        Fft2D.Forward(otf, grid, grid);
        var radiansPerSample = sampleArcsec / ShortExposurePsf.ArcsecPerRadian;
        for (var ky = 0; ky < grid; ky++)
        {
            var fy = (ky < grid / 2 ? ky : ky - grid) / (grid * radiansPerSample);
            for (var kx = 0; kx < grid; kx++)
            {
                var fx = (kx < grid / 2 ? kx : kx - grid) / (grid * radiansPerSample);
                var lambdaF = wavelengthM * Math.Sqrt((fx * fx) + (fy * fy));
                var tiltRemoved = Math.Max(0, 1 - Math.Cbrt(lambdaF / pupil.DiameterM));
                otf[(ky * grid) + kx] *= Math.Exp(-3.44 * Math.Pow(lambdaF / r0M, 5.0 / 3.0) * tiltRemoved);
            }
        }

        var center = (grid / 2) + 0.3;
        var image = RenderObject(grid, center, diskRadiusPx * 2, supersample: 2);
        Fft2D.Forward(image, grid, grid);
        for (var i = 0; i < image.Length; i++)
        {
            image[i] *= otf[i];
        }
        Fft2D.Inverse(image, grid, grid);

        // The PSF's peak sits on sample grid/2, so the circular convolution lands grid/2 over; the frame is the grid's middle.
        var random = new Random(seed);
        var stack = new float[frameSize * frameSize];
        var origin = (grid / 2) - frameSize;
        for (var y = 0; y < frameSize; y++)
        {
            for (var x = 0; x < frameSize; x++)
            {
                var sum = 0.0;
                for (var sy = 0; sy < 2; sy++)
                {
                    var gy = (origin + (2 * y) + sy + (grid / 2)) % grid;
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var gx = (origin + (2 * x) + sx + (grid / 2)) % grid;
                        sum += image[(gy * grid) + gx].Real;
                    }
                }
                stack[(y * frameSize) + x] = (float)((sum / 4) + (stackNoise * Gaussian(random)));
            }
        }
        return stack;
    }

    /// <summary>The disk's centre on a <see cref="RenderStack"/> frame of <paramref name="frameSize"/>.</summary>
    public static (double X, double Y) StackCenter(int frameSize)
    {
        // The disk sits at grid/2 + 0.3 = 2 frameSize + 0.3 render samples, the frame starts frameSize in, and detector pixel x
        // covers render samples 2x and 2x + 1, centred on 2x + 0.5.
        var c = (frameSize + 0.3 - 0.5) / 2;
        return (c, c);
    }

    /// <summary>Renders the next frame into <paramref name="frame"/> (<see cref="FrameSize"/> squared, row-major).</summary>
    public void Next(Span<float> frame)
    {
        PhaseScreen.Kolmogorov(_phase, Grid, _spacing, _r0, _random, _scratch);
        ShortExposurePsf.Compute(_pupil, _phase, Grid, _psf, _scratch);
        for (var i = 0; i < _psf.Length; i++)
        {
            _scratch[i] = _psf[i];
        }
        Fft2D.Forward(_scratch, Grid, Grid);
        for (var i = 0; i < _image.Length; i++)
        {
            _image[i] = _object[i] * _scratch[i];
        }
        Fft2D.Inverse(_image, Grid, Grid);

        // The PSF's peak sits on sample Grid/2, so the circular convolution lands Grid/2 over in each axis.
        const int shift = Grid / 2;
        for (var y = 0; y < FrameSize; y++)
        {
            for (var x = 0; x < FrameSize; x++)
            {
                var sum = 0.0;
                for (var sy = 0; sy < 2; sy++)
                {
                    var gy = ((2 * y) + sy + shift) % Grid;
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var gx = ((2 * x) + sx + shift) % Grid;
                        sum += _image[(gy * Grid) + gx].Real;
                    }
                }
                frame[(y * FrameSize) + x] = (float)((sum / 4) + (_noise * Gaussian(_random)));
            }
        }
    }

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// A Jupiter-like disk on a <paramref name="grid"/> square render grid: axis ratio 0.935 with its pole 20 degrees from +y,
    /// Minnaert-darkened (k 0.9), with three dark belts and a dark oval. The belts' edges and the limb give it power at every
    /// frequency, so the cutoff the spectrum shows is the telescope's, never the object's.
    /// </summary>
    private static Complex[] RenderObject(int grid, double center, double radius, int supersample)
    {
        const double axisRatio = 0.935;
        var (sin, cos) = Math.SinCos(20 * Math.PI / 180);
        var values = new Complex[grid * grid];
        var reach = (int)Math.Ceiling(radius) + 2;
        for (var py = Math.Max(0, (int)center - reach); py < Math.Min(grid, (int)center + reach); py++)
        {
            for (var px = Math.Max(0, (int)center - reach); px < Math.Min(grid, (int)center + reach); px++)
            {
                var sum = 0.0;
                for (var sy = 0; sy < supersample; sy++)
                {
                    for (var sx = 0; sx < supersample; sx++)
                    {
                        var dx = px - 0.5 + ((sx + 0.5) / supersample) - center;
                        var dy = py - 0.5 + ((sy + 0.5) / supersample) - center;
                        var u = ((dx * cos) + (dy * sin)) / radius;
                        var v = ((-dx * sin) + (dy * cos)) / (radius * axisRatio);
                        var rho2 = (u * u) + (v * v);
                        if (rho2 >= 1)
                        {
                            continue;
                        }
                        var mu = Math.Sqrt(1 - rho2);
                        var belts = 1
                            - (0.30 * Math.Exp(-Math.Pow((v - 0.25) / 0.05, 2)))
                            - (0.35 * Math.Exp(-Math.Pow((v + 0.20) / 0.06, 2)))
                            - (0.20 * Math.Exp(-Math.Pow((v - 0.55) / 0.04, 2)))
                            - (0.30 * Math.Exp(-(Math.Pow((u - 0.3) / 0.12, 2) + Math.Pow((v + 0.35) / 0.07, 2))));
                        sum += Math.Pow(mu, 2 * 0.9 - 1) * belts;
                    }
                }
                values[(py * grid) + px] = sum / (supersample * supersample);
            }
        }
        return values;
    }
}
