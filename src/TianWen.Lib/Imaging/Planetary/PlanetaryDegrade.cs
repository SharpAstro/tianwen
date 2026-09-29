using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>What a synthetic capture is made with (<see cref="PlanetaryDegrade"/>): the seeing, the warp and the camera.</summary>
/// <param name="Pupil">The telescope.</param>
/// <param name="WavelengthM">The single wavelength the frames are imaged at: the filter's effective one.</param>
public sealed record DegradeOptions(Pupil Pupil, double WavelengthM)
{
    /// <summary>The Fried parameter at 500 nm.</summary>
    public double R0M { get; init; } = 0.05;

    /// <summary>The turbulence's outer scale, in metres (infinite for Kolmogorov's): what bounds the disk's seeing motion.</summary>
    public double OuterScaleM { get; init; } = double.PositiveInfinity;

    /// <summary>
    /// Each frame's exposure, in seconds (zero for an instant). The wind carries the air across the pupil while the shutter is
    /// open, 9 cm in 4 ms at 22 m/s, which smooths a frame's speckle: an instantaneous PSF's full-contrast speckle jittered the
    /// aligner and every frame-to-frame statistic of 2022-09-03's synthetic Red capture.
    /// </summary>
    public double ExposureSeconds { get; init; }

    /// <summary>
    /// The telescope's own defocus, its RMS wavefront error in nanometres (Zernike's, over the clear aperture): a blur every frame
    /// shares, which widens the limb without moving the disk. 2022-09-03's real limb was barely sharper in its best tenth of frames
    /// (7.28 px) than in all (7.37), where seeing alone made the best tenth 4 to 5 % sharper.
    /// </summary>
    public double DefocusNm { get; init; }

    /// <summary>The wind that carries the phase screen across the pupil, in metres a second.</summary>
    public double WindMps { get; init; } = 10;

    /// <summary>The wind's direction on the screen, from +x toward +y.</summary>
    public double WindAngleDeg { get; init; } = 30;

    /// <summary>Minnaert's exponent of the map's filter.</summary>
    public double MinnaertK { get; init; } = 0.95;

    /// <summary>A sample's full scale in ADU (255 for 8 bits, which is how the frames are written; else 16 bits).</summary>
    public double FullScaleAdu { get; init; } = 255;

    /// <summary>The sky's level, the camera's offset, in ADU.</summary>
    public double OffsetAdu { get; init; }

    /// <summary>The read noise, in ADU.</summary>
    public double ReadNoiseAdu { get; init; }

    /// <summary>The gain, in electrons an ADU, which sets the shot noise.</summary>
    public double ElectronsPerAdu { get; init; } = 1;

    /// <summary>The disk's mean over the sky inside 0.8 radii, in ADU, which the render is scaled to.</summary>
    public double DiskLevelAdu { get; init; } = 100;

    /// <summary>The local warp's RMS per axis, in pixels (zero for none).</summary>
    public double WarpRmsPx { get; init; }

    /// <summary>Where the warp's correlation between two points falls to 1/e, in pixels.</summary>
    public double WarpLengthPx { get; init; } = 20;

    /// <summary>The warp's correlation with itself a frame later.</summary>
    public double WarpLag1 { get; init; } = 0.9;

    /// <summary>How often the map is rendered afresh as the planet turns, in seconds.</summary>
    public double RenderEverySeconds { get; init; } = 2;

    /// <summary>The phase screen's side in samples, a power of two at least the PSF grid's: the air the wind carries.</summary>
    public int ScreenSamples { get; init; } = 512;

    /// <summary>
    /// Whether each frame keeps its phase screen's own tilt, the seeing's motion of the disk, on top of the shift it is given
    /// (then the mount's slow part alone). Otherwise the tilt is taken out and the shift given is the whole motion: a real
    /// capture's measured shifts replayed carry its aligner's own error, which the synthetic capture's measurement then adds a
    /// second time (2022-09-03's first 3,000 Red frames read 0.96 px and their replay 1.16).
    /// </summary>
    public bool KeepScreenTilt { get; init; } = true;

    /// <summary>The seed every draw comes from: the same seed, the same capture.</summary>
    public int Seed { get; init; } = 1;
}

/// <summary>What one synthetic frame was made with: the truth a later phase measures itself against.</summary>
/// <param name="ShiftX">The disk's shift over the reference placement, x, in pixels: the shift given, plus the screen's tilt where it is kept.</param>
/// <param name="ShiftY">The same in y.</param>
/// <param name="Strehl">The frame's PSF peak over the diffraction-limited peak.</param>
public readonly record struct SyntheticFrame(double ShiftX, double ShiftY, double Strehl);

/// <summary>
/// A synthetic lucky-imaging capture made from a global map (docs/plans/planetary-restoration.md, R2). The map is rendered on
/// the ephemeris' spheroid at the capture's geometry, sampled finely enough for the pupil's cutoff and rendered afresh as the
/// planet turns. Each frame is that disk through the pupil and a phase screen that evolves from the last frame's
/// (<see cref="EvolvingPhaseScreen"/>), whose tilt is the seeing's motion of the disk, moved on by the mount's slow drift as
/// the real capture measured it, as bright as the real frame was, warped by a smooth random field correlated in space and time, binned to the detector's
/// pixels, and read out as the camera does:
/// Poisson electrons, read noise, the offset, rounded and clipped to the ADC's range.
/// <para>
/// Not modelled: the blur varying over the disk (one PSF per frame; only the warp varies), the camera's fixed pattern, and
/// the filter's width (one wavelength).
/// </para>
/// </summary>
public static class PlanetaryDegrade
{
    // Frames made at once: the screens and warps are drawn in order, the frames themselves in parallel.
    private const int Block = 64;
    private const int PsfGrid = 128;

    /// <summary>
    /// The pixel-to-fine-sample factor the frames are rendered at: enough samples for the pupil's cutoff at
    /// <paramref name="wavelengthM"/>, never fewer than one.
    /// </summary>
    public static int OversampleFor(double arcsecPerPixel, double apertureM, double wavelengthM)
        => Math.Max(1, (int)Math.Ceiling(arcsecPerPixel / (wavelengthM / (2 * apertureM) * ShortExposurePsf.ArcsecPerRadian)));

    /// <summary>
    /// The read noise, in ADU, that leaves a sky at <paramref name="offsetAdu"/> with <paramref name="recordedNoiseAdu"/> of noise
    /// once rounded to whole ADU. An 8-bit sky is mostly one value, and rounding hides a noise far below a step: the spread
    /// recorded is the rounding's, so the noise is solved through it rather than read off it.
    /// </summary>
    public static double ReadNoiseFor(double offsetAdu, double recordedNoiseAdu)
    {
        if (!(recordedNoiseAdu > 0))
        {
            return 0;
        }
        double lo = 0, hi = Math.Max(1, 2 * recordedNoiseAdu);
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (RoundedStd(offsetAdu, mid) < recordedNoiseAdu)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo + hi) / 2;
    }

    /// <summary>
    /// The gain, in electrons an ADU, that gives a disk at <paramref name="diskLevelAdu"/> over the sky the finest a trous
    /// band's noise <paramref name="band1NoiseAdu"/> it was measured with, beside <paramref name="readNoiseAdu"/> and the
    /// rounding: a pixel's variance is the band's over the band's share of white noise, and shot noise is its level over the
    /// gain. Null when the noise leaves no room for shot noise.
    /// </summary>
    public static double? GainFor(double diskLevelAdu, double band1NoiseAdu, double readNoiseAdu)
    {
        var shot = (band1NoiseAdu * band1NoiseAdu / Band1WhiteNoiseShare) - (readNoiseAdu * readNoiseAdu) - (1.0 / 12);
        return shot > 0 && diskLevelAdu > 0 ? diskLevelAdu / shot : null;
    }

    // The share of white noise's variance the finest a trous band holds: the sum of its impulse response's squares.
    private static readonly double Band1WhiteNoiseShare = ComputeBand1WhiteNoiseShare();

    private static double ComputeBand1WhiteNoiseShare()
    {
        const int size = 64;
        var impulse = new float[size * size];
        impulse[((size / 2) * size) + (size / 2)] = 1;
        var detail = ATrousWaveletTransform.Decompose(impulse, size, size, 1).Detail(0);
        double sum = 0;
        foreach (var v in detail)
        {
            sum += (double)v * v;
        }
        return sum;
    }

    // The spread of round(mean + sigma z), z standard normal.
    private static double RoundedStd(double mean, double sigma)
    {
        if (sigma <= 0)
        {
            return 0;
        }
        double m1 = 0, m2 = 0;
        var from = (int)Math.Floor(mean - (8 * sigma)) - 1;
        var to = (int)Math.Ceiling(mean + (8 * sigma)) + 1;
        for (var k = from; k <= to; k++)
        {
            var p = NormalCdf((k + 0.5 - mean) / sigma) - NormalCdf((k - 0.5 - mean) / sigma);
            m1 += p * k;
            m2 += p * k * k;
        }
        return Math.Sqrt(Math.Max(0, m2 - (m1 * m1)));
    }

    // Abramowitz and Stegun 7.1.26 through erfc, good to 1.5e-7.
    private static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + (0.3275911 * x));
        var erfc = t * (0.254829592 + (t * (-0.284496736 + (t * (1.421413741 + (t * (-1.453152027 + (t * 1.061405429)))))))) * Math.Exp(-x * x);
        return z >= 0 ? 1 - (erfc / 2) : erfc / 2;
    }

    /// <summary>
    /// Makes one frame per entry of <paramref name="times"/> and hands each, in order, to <paramref name="write"/> as the
    /// camera's samples (row-major, <paramref name="width"/> by <paramref name="height"/>, whole numbers in ADU).
    /// </summary>
    /// <param name="map">The map rendered.</param>
    /// <param name="planet">Whose ephemeris gives the geometry at each time.</param>
    /// <param name="times">Each frame's time.</param>
    /// <param name="reference">The disk where a frame of no shift has it.</param>
    /// <param name="arcsecPerPixel">The detector's scale.</param>
    /// <param name="shiftX">Each frame's shift of the disk over <paramref name="reference"/>, x.</param>
    /// <param name="shiftY">The same in y.</param>
    /// <param name="brightness">Each frame's light over the capture's mean (scintillation and transparency), or empty for none.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="options">The seeing, the warp and the camera.</param>
    /// <param name="write">Takes each frame's samples, in order.</param>
    /// <param name="progress">Told the frames done.</param>
    /// <param name="cancellationToken">Stops the making.</param>
    public static async Task<ImmutableArray<SyntheticFrame>> MakeAsync(PlanetMap map, CatalogIndex planet, ImmutableArray<DateTimeOffset> times, DiskPlacement reference,
        double arcsecPerPixel, ImmutableArray<double> shiftX, ImmutableArray<double> shiftY, ImmutableArray<double> brightness, int width, int height, DegradeOptions options,
        Action<int, ushort[]> write, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(write);
        var n = times.Length;
        ArgumentOutOfRangeException.ThrowIfNotEqual(shiftX.Length, n);
        ArgumentOutOfRangeException.ThrowIfNotEqual(shiftY.Length, n);
        if (!brightness.IsDefaultOrEmpty)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(brightness.Length, n);
        }

        // The fine grid: a window of the detector around the reference disk, sampled `os` times finer, big enough for the
        // disk, its halo, the PSF's reach and the warp.
        var os = OversampleFor(arcsecPerPixel, options.Pupil.DiameterM, options.WavelengthM);
        var fine = NextPowerOfTwo((int)Math.Ceiling(((2.6 * reference.EquatorialRadius) + 16) * os) + PsfGrid);
        var windowPx = fine / os;
        var (windowX, windowY) = ((int)Math.Round(reference.CenterX) - (windowPx / 2), (int)Math.Round(reference.CenterY) - (windowPx / 2));
        var finePlacement = reference with
        {
            CenterX = ((reference.CenterX - windowX + 0.5) * os) - 0.5,
            CenterY = ((reference.CenterY - windowY + 0.5) * os) - 0.5,
            EquatorialRadius = reference.EquatorialRadius * os,
        };

        // The pupil sampled for a PSF of fine samples, and the screen that crosses it.
        var sampleRadians = arcsecPerPixel / os / ShortExposurePsf.ArcsecPerRadian;
        var spacing = options.WavelengthM / (PsfGrid * sampleRadians);
        var pupil = options.Pupil.Rasterise(PsfGrid, spacing);
        var defocus = DefocusPhase(PsfGrid, spacing, options.Pupil.DiameterM, options.DefocusNm * 1e-9, options.WavelengthM);
        var diffraction = new double[PsfGrid * PsfGrid];
        // The diffraction limit the Strehl ratio is taken against is the perfect telescope's, so a defocused one scores below 1.
        ShortExposurePsf.Compute(pupil, ReadOnlySpan<double>.Empty, PsfGrid, diffraction);
        var diffractionPeak = Max(diffraction);
        var screenSamples = Math.Max(options.ScreenSamples, PsfGrid);
        var screen = new EvolvingPhaseScreen(screenSamples, spacing, options.R0M, new Random(options.Seed), options.OuterScaleM);
        // Phase in radians at 500 nm, where r0 is stated, scaled to the imaging wavelength (the path difference is achromatic).
        var phaseScale = 500e-9 / options.WavelengthM;
        var (windX, windY) = (options.WindMps * Math.Cos(options.WindAngleDeg * Math.PI / 180), options.WindMps * Math.Sin(options.WindAngleDeg * Math.PI / 180));
        // The periodic screen comes round again after its side over the wind; it is renewed three e-folds in that time.
        var renewSeconds = screen.SizeM / Math.Max(options.WindMps, 1e-3) / 3;

        var warp = new WarpField(windowPx, options.WarpRmsPx, options.WarpLengthPx, options.WarpLag1, options.Seed + 1);

        var truths = new SyntheticFrame[n];
        var screenPhase = new double[screenSamples * screenSamples];
        var blockPsfs = new double[Block][];
        var blockWarps = new WarpFrame[Block];
        var blockTilts = new (double X, double Y)[Block];
        for (var i = 0; i < Block; i++)
        {
            blockPsfs[i] = new double[PsfGrid * PsfGrid];
        }
        var phase = new double[PsfGrid * PsfGrid];
        var psfScratch = new Complex[PsfGrid * PsfGrid];
        var subPsf = new double[PsfGrid * PsfGrid];
        // The exposure in frozen-flow steps of at most a centimetre: the pupil's window slides across the same screen, the air
        // being the same air within a frame.
        var sweepM = options.WindMps * options.ExposureSeconds;
        var subSteps = Math.Max(1, (int)Math.Ceiling(sweepM / 0.01));
        var screenMargin = (screenSamples - PsfGrid) / 2;
        if (sweepM / spacing > screenMargin)
        {
            throw new ArgumentException($"The exposure sweeps {sweepM:0.000} m of air, more than the screen's margin of {screenMargin * spacing:0.000} m: use a larger screen.", nameof(options));
        }

        Complex[]? objectSpectrum = null;
        var objectEpoch = double.NaN;
        var start = times[0];
        for (var first = 0; first < n; first += Block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Block, n - first);

            // The object, rendered afresh when the planet has turned long enough; the block shares the render nearest its middle.
            var middle = (times[first + (count / 2)] - start).TotalSeconds;
            var epoch = Math.Floor(middle / options.RenderEverySeconds) * options.RenderEverySeconds;
            if (objectSpectrum is null || epoch != objectEpoch)
            {
                var aspect = PhysicalEphemeris.Compute(planet, start + TimeSpan.FromSeconds(epoch + (options.RenderEverySeconds / 2)));
                objectSpectrum = ObjectSpectrum(map, aspect, finePlacement, fine, options);
                objectEpoch = epoch;
            }

            // The screens and warps, in order: each is the last one moved on.
            for (var k = 0; k < count; k++)
            {
                var t = first + k;
                if (t > 0)
                {
                    var dt = (times[t] - times[t - 1]).TotalSeconds;
                    screen.Step(windX, windY, dt, Math.Exp(-dt / renewSeconds));
                    warp.Step();
                }
                screen.Fill(screenPhase);
                // The PSF over the exposure: the pupil's window stepped upwind across the screen, as the air moves past it.
                Array.Clear(blockPsfs[k]);
                for (var step = 0; step < subSteps; step++)
                {
                    var along = subSteps == 1 ? 0 : (sweepM * ((step + 0.5) / subSteps - 0.5)) / spacing;
                    var offsetX = screenMargin - (int)Math.Round(along * Math.Cos(options.WindAngleDeg * Math.PI / 180));
                    var offsetY = screenMargin - (int)Math.Round(along * Math.Sin(options.WindAngleDeg * Math.PI / 180));
                    for (var y = 0; y < PsfGrid; y++)
                    {
                        for (var x = 0; x < PsfGrid; x++)
                        {
                            phase[(y * PsfGrid) + x] = (screenPhase[((y + offsetY) * screenSamples) + x + offsetX] * phaseScale) + defocus[(y * PsfGrid) + x];
                        }
                    }
                    ShortExposurePsf.Compute(pupil, phase, PsfGrid, subPsf, psfScratch);
                    for (var i = 0; i < subPsf.Length; i++)
                    {
                        blockPsfs[k][i] += subPsf[i] / subSteps;
                    }
                }
                blockWarps[k] = warp.Current();
                // The PSF's tilt in pixels: kept as the seeing's motion, or taken out where the shift given is the whole of it.
                var (cx, cy) = Centroid(blockPsfs[k]);
                blockTilts[k] = options.KeepScreenTilt ? (0, 0) : (-cx / os, -cy / os);
                var (tiltX, tiltY) = options.KeepScreenTilt ? (cx / os, cy / os) : (0, 0);
                truths[t] = new SyntheticFrame(shiftX[t] + tiltX, shiftY[t] + tiltY, Max(blockPsfs[k]) / diffractionPeak);
            }

            // The frames themselves, in parallel, each from its own draws.
            var spectrum = objectSpectrum;
            var frames = new ushort[count][];
            await Parallel.ForAsync(0, count, new ParallelOptions { CancellationToken = cancellationToken }, (k, _) =>
            {
                var t = first + k;
                frames[k] = MakeFrame(spectrum, blockPsfs[k], blockWarps[k], fine, os, windowX, windowY, shiftX[t] + blockTilts[k].X, shiftY[t] + blockTilts[k].Y, brightness.IsDefaultOrEmpty ? 1 : brightness[t], width, height, options,
                    new Random(unchecked((options.Seed * 1_000_003) + t)));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            for (var k = 0; k < count; k++)
            {
                write(first + k, frames[k]);
            }
            progress?.Report(first + count);
        }
        return [.. truths];
    }

    // The map at `aspect`, on the fine grid, scaled so the disk's mean inside 0.8 radii is its level in electrons, and taken
    // to the Fourier domain once for every frame that shares it.
    private static Complex[] ObjectSpectrum(PlanetMap map, in PlanetAspect aspect, in DiskPlacement placement, int fine, DegradeOptions options)
    {
        var render = PlanetaryRender.Render(map, aspect, placement, fine, fine, options.MinnaertK, supersample: 2);
        double sum = 0;
        var count = 0;
        for (var y = 0; y < fine; y++)
        {
            for (var x = 0; x < fine; x++)
            {
                var dx = x - placement.CenterX;
                var dy = y - placement.CenterY;
                if ((dx * dx) + (dy * dy) < 0.64 * placement.EquatorialRadius * placement.EquatorialRadius)
                {
                    sum += render[(y * fine) + x];
                    count++;
                }
            }
        }
        var scale = count > 0 && sum > 0 ? options.DiskLevelAdu * options.ElectronsPerAdu / (sum / count) : 0;
        var spectrum = new Complex[fine * fine];
        for (var i = 0; i < spectrum.Length; i++)
        {
            spectrum[i] = render[i] * scale;
        }
        Fft2D.Forward(spectrum, fine, fine);
        return spectrum;
    }

    // One frame: the object through this frame's PSF, moved by `shiftX`, `shiftY` (the fraction of a pixel in the Fourier
    // domain, the whole pixels in where the window lands), warped, binned, and read out.
    private static ushort[] MakeFrame(Complex[] objectSpectrum, double[] psf, WarpFrame warp, int fine, int os, int windowX, int windowY,
        double shiftX, double shiftY, double brightness, int width, int height, DegradeOptions options, Random random)
    {
        var (ix, iy) = ((int)Math.Round(shiftX), (int)Math.Round(shiftY));
        var dx = (shiftX - ix) * os;
        var dy = (shiftY - iy) * os;

        // The PSF on the fine grid with its centre at the origin, times the object, moved by (dx, dy) fine samples.
        var field = new Complex[fine * fine];
        for (var y = 0; y < PsfGrid; y++)
        {
            var fy = ((y - (PsfGrid / 2)) + fine) % fine;
            for (var x = 0; x < PsfGrid; x++)
            {
                var fx = ((x - (PsfGrid / 2)) + fine) % fine;
                field[(fy * fine) + fx] = psf[(y * PsfGrid) + x];
            }
        }
        Fft2D.Forward(field, fine, fine);
        for (var ky = 0; ky < fine; ky++)
        {
            var fy = (ky < fine / 2 ? ky : ky - fine) / (double)fine;
            for (var kx = 0; kx < fine; kx++)
            {
                var fx = (kx < fine / 2 ? kx : kx - fine) / (double)fine;
                var i = (ky * fine) + kx;
                field[i] *= objectSpectrum[i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * dx) + (fy * dy)));
            }
        }
        Fft2D.Inverse(field, fine, fine);

        // Warped (each fine sample takes the value the field moved onto it) and binned to the detector's pixels.
        var windowPx = fine / os;
        var binned = new double[windowPx * windowPx];
        var hasWarp = !warp.IsEmpty;
        for (var y = 0; y < fine; y++)
        {
            for (var x = 0; x < fine; x++)
            {
                double value;
                if (hasWarp)
                {
                    // The value is the field's at the point the warp brought here, with no Jacobian: a lossless screen that
                    // bends the rays keeps the radiance, the surface brightness, as a gravitational lens does, the screen's
                    // focusing (scintillation) making up exactly what the map's squeeze would. Multiplying by det(I - grad w)
                    // was tried and was wrong twice: unphysical, and, taken from the interpolated displacement, a gradient
                    // that jumps at every node line printed the grid into the frames (the coarse bands five times the real).
                    var (px, py) = (((x + 0.5) / os) - 0.5, ((y + 0.5) / os) - 0.5);
                    var (wx, wy) = warp.At(px, py);
                    value = Bilinear(field, fine, x - (wx * os), y - (wy * os));
                }
                else
                {
                    value = field[(y * fine) + x].Real;
                }
                binned[((y / os) * windowPx) + (x / os)] += value;
            }
        }

        // Read out: electrons (Poisson), read noise, the offset, rounded and clipped.
        var frame = new ushort[width * height];
        var perPixel = brightness / (os * os);
        var (originX, originY) = (windowX + ix, windowY + iy);
        for (var y = 0; y < height; y++)
        {
            var by = y - originY;
            for (var x = 0; x < width; x++)
            {
                var bx = x - originX;
                var electrons = bx >= 0 && by >= 0 && bx < windowPx && by < windowPx ? Math.Max(0, binned[(by * windowPx) + bx] * perPixel) : 0;
                var adu = (Poisson(electrons, random) / options.ElectronsPerAdu) + options.OffsetAdu + (options.ReadNoiseAdu * PhaseScreen.Gaussian(random));
                frame[(y * width) + x] = (ushort)Math.Clamp(Math.Round(adu), 0, options.FullScaleAdu);
            }
        }
        return frame;
    }

    // Zernike's defocus, sqrt(3) (2 rho^2 - 1), scaled to `rmsM` of wavefront over the unit disk of the aperture, in radians at
    // `wavelengthM`, on the pupil grid (centred on sample n/2, n/2).
    private static double[] DefocusPhase(int n, double spacingM, double diameterM, double rmsM, double wavelengthM)
    {
        var phase = new double[n * n];
        if (rmsM == 0)
        {
            return phase;
        }
        var radius = diameterM / 2;
        var scale = 2 * Math.PI * rmsM / wavelengthM * Math.Sqrt(3);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var (px, py) = ((x - (n / 2)) * spacingM, (y - (n / 2)) * spacingM);
                var rho2 = ((px * px) + (py * py)) / (radius * radius);
                phase[(y * n) + x] = rho2 <= 1 ? scale * ((2 * rho2) - 1) : 0;
            }
        }
        return phase;
    }

    // A PSF's centroid over its centre sample, in fine samples.
    private static (double X, double Y) Centroid(double[] psf)
    {
        double cx = 0, cy = 0;
        for (var y = 0; y < PsfGrid; y++)
        {
            for (var x = 0; x < PsfGrid; x++)
            {
                var v = psf[(y * PsfGrid) + x];
                cx += v * (x - (PsfGrid / 2));
                cy += v * (y - (PsfGrid / 2));
            }
        }
        return (cx, cy);
    }

    private static double Bilinear(Complex[] field, int size, double x, double y)
    {
        if (x < 0 || y < 0 || x > size - 1 || y > size - 1)
        {
            return 0;
        }
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(x0 + 1, size - 1), Math.Min(y0 + 1, size - 1));
        var (tx, ty) = (x - x0, y - y0);
        var top = (field[(y0 * size) + x0].Real * (1 - tx)) + (field[(y0 * size) + x1].Real * tx);
        var bottom = (field[(y1 * size) + x0].Real * (1 - tx)) + (field[(y1 * size) + x1].Real * tx);
        return (top * (1 - ty)) + (bottom * ty);
    }

    // A Poisson draw: by multiplication below 30, where it is cheap and a Gaussian would be skewed; by a Gaussian above.
    private static double Poisson(double mean, Random random)
    {
        if (mean <= 0)
        {
            return 0;
        }
        if (mean >= 30)
        {
            return Math.Max(0, Math.Round(mean + (Math.Sqrt(mean) * PhaseScreen.Gaussian(random))));
        }
        var limit = Math.Exp(-mean);
        var product = random.NextDouble();
        var k = 0;
        while (product > limit)
        {
            product *= random.NextDouble();
            k++;
        }
        return k;
    }

    private static double Max(double[] values)
    {
        var max = double.MinValue;
        foreach (var v in values)
        {
            max = Math.Max(max, v);
        }
        return max;
    }

    private static int NextPowerOfTwo(int value)
    {
        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }
        return p;
    }

    /// <summary>
    /// The local warp: a displacement field on a grid a few pixels apart over the window, each axis white noise smoothed by a
    /// Gaussian (whose correlation <c>exp(-d^2 / 4 s^2)</c> falls to 1/e at twice its sigma), scaled to the RMS asked, and
    /// carried from frame to frame as <c>W(t) = rho W(t - 1) + sqrt(1 - rho^2) Z(t)</c>.
    /// </summary>
    private sealed class WarpField
    {
        public const int GridStep = 4;
        private readonly int _nodes;
        private readonly double _rms;
        private readonly double _rho;
        private readonly double[] _kernel;
        private readonly double _norm;
        private readonly Random _random;
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly double[] _noise;
        private readonly double[] _smooth;
        private readonly double[] _scratch;

        public WarpField(int windowPx, double rms, double length, double rho, int seed)
        {
            _nodes = (windowPx / GridStep) + 2;
            _rms = rms;
            _rho = Math.Clamp(rho, 0, 1);
            _random = new Random(seed);
            var sigma = Math.Max(length / 2 / GridStep, 0.3);
            var radius = (int)Math.Ceiling(3.5 * sigma);
            _kernel = new double[(2 * radius) + 1];
            double sum = 0;
            for (var t = -radius; t <= radius; t++)
            {
                _kernel[t + radius] = Math.Exp(-0.5 * t * t / (sigma * sigma));
                sum += _kernel[t + radius];
            }
            double squares = 0;
            for (var t = 0; t < _kernel.Length; t++)
            {
                _kernel[t] /= sum;
                squares += _kernel[t] * _kernel[t];
            }
            // A separable smoothing of unit white noise leaves a variance of the kernel's squares, squared: its RMS is their sum.
            _norm = 1 / squares;
            var cells = _nodes * _nodes;
            (_x, _y, _noise, _smooth, _scratch) = (new double[cells], new double[cells], new double[cells], new double[cells], new double[cells]);
            if (_rms > 0)
            {
                Draw(_x);
                Draw(_y);
            }
        }

        public void Step()
        {
            if (_rms <= 0)
            {
                return;
            }
            var renew = Math.Sqrt(1 - (_rho * _rho));
            Draw(_smooth);
            for (var i = 0; i < _x.Length; i++)
            {
                _x[i] = (_rho * _x[i]) + (renew * _smooth[i]);
            }
            Draw(_smooth);
            for (var i = 0; i < _y.Length; i++)
            {
                _y[i] = (_rho * _y[i]) + (renew * _smooth[i]);
            }
        }

        // The field now, in pixels, as float copies the parallel frames read; empty when there is no warp.
        public WarpFrame Current()
        {
            if (_rms <= 0)
            {
                return WarpFrame.Empty;
            }
            var (x, y) = (new float[_x.Length], new float[_y.Length]);
            for (var i = 0; i < x.Length; i++)
            {
                x[i] = (float)_x[i];
                y[i] = (float)_y[i];
            }
            return new WarpFrame(_nodes, x, y);
        }

        // Unit-variance smoothed noise scaled to the RMS, into `into`.
        private void Draw(double[] into)
        {
            for (var i = 0; i < _noise.Length; i++)
            {
                _noise[i] = PhaseScreen.Gaussian(_random);
            }
            var radius = _kernel.Length / 2;
            for (var y = 0; y < _nodes; y++)
            {
                for (var x = 0; x < _nodes; x++)
                {
                    double s = 0;
                    for (var t = -radius; t <= radius; t++)
                    {
                        s += _kernel[t + radius] * _noise[(y * _nodes) + ((x + t + _nodes) % _nodes)];
                    }
                    _scratch[(y * _nodes) + x] = s;
                }
            }
            var scale = _rms * _norm;
            for (var y = 0; y < _nodes; y++)
            {
                for (var x = 0; x < _nodes; x++)
                {
                    double s = 0;
                    for (var t = -radius; t <= radius; t++)
                    {
                        s += _kernel[t + radius] * _scratch[(((y + t + _nodes) % _nodes) * _nodes) + x];
                    }
                    into[(y * _nodes) + x] = s * scale;
                }
            }
        }
    }

    /// <summary>One frame's warp on the node grid, read between the nodes bilinearly.</summary>
    private sealed record WarpFrame(int Nodes, float[] X, float[] Y)
    {
        public static readonly WarpFrame Empty = new WarpFrame(0, [], []);

        public bool IsEmpty => Nodes == 0;

        // The displacement at detector position (px, py) of the window, in pixels.
        public (double X, double Y) At(double px, double py)
        {
            var gx = Math.Clamp(px / WarpField.GridStep, 0, Nodes - 1.001);
            var gy = Math.Clamp(py / WarpField.GridStep, 0, Nodes - 1.001);
            var (x0, y0) = ((int)gx, (int)gy);
            var (tx, ty) = (gx - x0, gy - y0);
            var i = (y0 * Nodes) + x0;
            double Read(float[] f) => (((f[i] * (1 - tx)) + (f[i + 1] * tx)) * (1 - ty)) + (((f[i + Nodes] * (1 - tx)) + (f[i + Nodes + 1] * tx)) * ty);
            return (Read(X), Read(Y));
        }
    }
}
