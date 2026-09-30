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

/// <summary>
/// One colour of a Bayer synthetic capture (<see cref="PlanetaryDegrade.MakeBayerAsync"/>): its map, where its disk sits on the
/// sensor in the sensor's pixels (the colours' placements differ by the atmosphere's dispersion), and its own wavelength and camera
/// levels in <paramref name="Options"/>.
/// </summary>
public sealed record BayerColour(PlanetMap Map, DiskPlacement Placement, DegradeOptions Options);

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

    /// <summary>
    /// A second layer of turbulence at the telescope (the tube's air, the mirror's boundary layer, the dome), its Fried parameter
    /// at 500 nm; infinite for none. It sits in the pupil, so every point of the disk sees it alike (no warp), and with an outer
    /// scale about the tube's it blurs every frame while barely moving the disk: on 2022-09-03 the real frames were blurrier than
    /// the synthetic ones whose seeing moved the disk as much, and their lucky tenth barely sharper.
    /// </summary>
    public double LocalR0M { get; init; } = double.PositiveInfinity;

    /// <summary>The local layer's outer scale, in metres: about the tube's width.</summary>
    public double LocalOuterScaleM { get; init; } = 0.25;

    /// <summary>How fast the local layer's air drifts across the pupil, in metres a second (zero holds it still).</summary>
    public double LocalWindMps { get; init; } = 1;

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

    /// <summary>
    /// The share of the light the telescope scatters wide (its mirrors' roughness and dust), zero for none: spread over the whole
    /// window by a kernel of <see cref="ScatterCoreArcsec"/>, (1 + (r / core)^2)^(-3/2), past the PSF grid's reach. 2022-09-03's
    /// real frames hold 0.2 to 0.4 % of the disk's brightness 15 to 75 px beyond its limb, where seeing and the layer at the
    /// telescope, cut at the PSF grid's 32 px, leave none.
    /// </summary>
    public double ScatterFraction { get; init; }

    /// <summary>The scatter kernel's core, in arcseconds.</summary>
    public double ScatterCoreArcsec { get; init; } = 5;

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

    /// <summary>
    /// The phase screens' sample spacing, in metres; null (the default) for the pupil's own, which the wavelength and the fine
    /// grid set. A screen is its Fourier modes on a frequency grid its spacing fixes, so the same seed at another spacing is
    /// other air: colours that are to share one atmosphere (<see cref="PlanetaryDegrade.MakeBayerAsync"/>) share this, and each
    /// samples the one screen at its own pupil's spacing.
    /// </summary>
    public double? ScreenSpacingM { get; init; }
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
    internal const int PsfGrid = 128;

    /// <summary>
    /// The pixel-to-fine-sample factor the frames are rendered at: enough samples for the pupil's cutoff at
    /// <paramref name="wavelengthM"/>, never fewer than one.
    /// </summary>
    public static int OversampleFor(double arcsecPerPixel, double apertureM, double wavelengthM)
        => Math.Max(1, (int)Math.Ceiling(arcsecPerPixel / (wavelengthM / (2 * apertureM) * ShortExposurePsf.ArcsecPerRadian)));

    /// <summary>
    /// The level and noise, in ADU, of a flat sky as it was before the camera rounded it to whole ADU: the maximum-likelihood
    /// Gaussian whose rounding gives <paramref name="counts"/>, how many samples read each value from <paramref name="first"/> on.
    /// An 8-bit sky reads one or two values, whose mean is not its level and whose spread the mean alone sets: 2022-09-03's far sky
    /// read 17 in 92 % of its samples and 16 in 8 %, a mean of 16.92 and a spread of 0.27, from a level of 16.80 and a noise of
    /// 0.21. Solving the noise from the spread at the mean, as this once did, gave 0.27, and 0.19 in the ring beside the disk.
    /// A noise of several ADU is its moments, less the rounding's twelfth (Sheppard). NaN for no samples.
    /// </summary>
    public static (double Level, double Noise) RoundedGaussianFit(ReadOnlySpan<long> counts, int first)
    {
        double total = 0, m1 = 0, m2 = 0;
        for (var k = 0; k < counts.Length; k++)
        {
            total += counts[k];
            m1 += counts[k] * (double)(first + k);
            m2 += counts[k] * (double)(first + k) * (first + k);
        }
        if (total == 0)
        {
            return (double.NaN, double.NaN);
        }
        var mean = m1 / total;
        var variance = Math.Max(0, (m2 / total) - (mean * mean));
        if (variance > 4)
        {
            return (mean, Math.Sqrt(variance - (1.0 / 12)));
        }
        // A grid over a step either side of the mean and noises to two ADU, then twice a tenth of the grid around the best.
        var (bestLevel, bestNoise, best) = (mean, 0.3, double.NegativeInfinity);
        var (levelStep, noiseStep) = (0.02, 0.02);
        var (levelFrom, levelTo, noiseFrom, noiseTo) = (mean - 1, mean + 1, 0.01, 2.0);
        for (var pass = 0; pass < 3; pass++)
        {
            for (var level = levelFrom; level <= levelTo; level += levelStep)
            {
                for (var noise = noiseFrom; noise <= noiseTo; noise += noiseStep)
                {
                    var likelihood = RoundedLogLikelihood(counts, first, level, noise);
                    if (likelihood > best)
                    {
                        (bestLevel, bestNoise, best) = (level, noise, likelihood);
                    }
                }
            }
            (levelFrom, levelTo) = (bestLevel - (2 * levelStep), bestLevel + (2 * levelStep));
            (noiseFrom, noiseTo) = (Math.Max(0.001, bestNoise - (2 * noiseStep)), bestNoise + (2 * noiseStep));
            (levelStep, noiseStep) = (levelStep / 10, noiseStep / 10);
        }
        return (bestLevel, bestNoise);
    }

    /// <summary>The probability that round(level + noise z), z standard normal, reads <paramref name="value"/>.</summary>
    internal static double RoundedProbability(int value, double level, double noise)
    {
        return NormalCdf((value + 0.5 - level) / noise) - NormalCdf((value - 0.5 - level) / noise);
    }

    // The log-likelihood of `counts` under round(level + noise z), z standard normal.
    private static double RoundedLogLikelihood(ReadOnlySpan<long> counts, int first, double level, double noise)
    {
        double sum = 0;
        for (var k = 0; k < counts.Length; k++)
        {
            if (counts[k] > 0)
            {
                var v = first + k;
                var p = NormalCdf((v + 0.5 - level) / noise) - NormalCdf((v - 0.5 - level) / noise);
                sum += counts[k] * Math.Log(Math.Max(p, 1e-300));
            }
        }
        return sum;
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
    /// <param name="warps">Takes each frame's warp where the frame is, in order, when there is one: the truth a dewarp is scored
    /// against (R5 part 2).</param>
    /// <param name="cancellationToken">Stops the making.</param>
    public static async Task<ImmutableArray<SyntheticFrame>> MakeAsync(PlanetMap map, CatalogIndex planet, ImmutableArray<DateTimeOffset> times, DiskPlacement reference,
        double arcsecPerPixel, ImmutableArray<double> shiftX, ImmutableArray<double> shiftY, ImmutableArray<double> brightness, int width, int height, DegradeOptions options,
        Action<int, ushort[]> write, IProgress<int>? progress = null, Action<int, SyntheticWarp>? warps = null, CancellationToken cancellationToken = default)
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

        // The seeing, frame after frame: the air, the still layer, the exposure and the telescope, one code with R7's theory.
        var seeing = new SeeingPsfSequence(options, arcsecPerPixel);
        var diffractionPeak = seeing.DiffractionPeak;

        var warp = new WarpField(windowPx, options.WarpRmsPx, options.WarpLengthPx, options.WarpLag1, options.Seed + 1);
        var scatter = options.ScatterFraction > 0 ? ScatterSpectrum(fine, options.ScatterCoreArcsec / (arcsecPerPixel / os)) : null;

        var truths = new SyntheticFrame[n];
        var blockPsfs = new double[Block][];
        var blockWarps = new SyntheticWarpField[Block];
        var blockTilts = new (double X, double Y)[Block];
        for (var i = 0; i < Block; i++)
        {
            blockPsfs[i] = new double[PsfGrid * PsfGrid];
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
                    seeing.Step((times[t] - times[t - 1]).TotalSeconds);
                    warp.Step();
                }
                seeing.Exposure(blockPsfs[k]);
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
                frames[k] = MakeFrame(spectrum, blockPsfs[k], scatter, blockWarps[k], fine, os, windowX, windowY, shiftX[t] + blockTilts[k].X, shiftY[t] + blockTilts[k].Y, brightness.IsDefaultOrEmpty ? 1 : brightness[t], width, height, options,
                    new Random(unchecked((options.Seed * 1_000_003) + t)));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            for (var k = 0; k < count; k++)
            {
                var t = first + k;
                write(t, frames[k]);
                // The window lands on the whole pixels of the frame's shift, as MakeFrame places it.
                if (warps is not null && !blockWarps[k].IsEmpty)
                {
                    warps(t, new SyntheticWarp(windowX + (int)Math.Round(shiftX[t] + blockTilts[k].X), windowY + (int)Math.Round(shiftY[t] + blockTilts[k].Y), blockWarps[k]));
                }
            }
            progress?.Report(first + count);
        }
        return [.. truths];
    }

    /// <summary>
    /// A colour synthetic capture on a Bayer sensor (docs/plans/planetary-restoration.md, R5a): each colour made by
    /// <see cref="MakeAsync"/> at the sensor's full resolution, its own map, wavelength, camera levels and placement (the colours'
    /// placements differ by the atmosphere's dispersion), and each photosite taken from its own colour's frame, the two greens
    /// from the green one. One atmosphere for all three: the colours must share <see cref="DegradeOptions.Seed"/> and everything
    /// the air sets, so their screens, tilts and warp are the same path difference seen at three wavelengths (the phase is
    /// scaled by 500 nm over the wavelength). The frames are held until the last colour is made, then written in order; the warp,
    /// achromatic, is reported from the green pass.
    /// </summary>
    /// <returns>Each colour's frames as <see cref="MakeAsync"/> records them.</returns>
    /// <remarks>The screens are drawn once, at the finest of the three pupils' spacings (<see cref="DegradeOptions.ScreenSpacingM"/>),
    /// so every colour samples the same air at or above its own resolution.</remarks>
    public static async Task<(ImmutableArray<SyntheticFrame> Red, ImmutableArray<SyntheticFrame> Green, ImmutableArray<SyntheticFrame> Blue)> MakeBayerAsync(
        CatalogIndex planet, ImmutableArray<DateTimeOffset> times, BayerColour red, BayerColour green, BayerColour blue, double arcsecPerPixel,
        ImmutableArray<double> shiftX, ImmutableArray<double> shiftY, ImmutableArray<double> brightness, int width, int height, int bayerOffsetX, int bayerOffsetY,
        Action<int, ushort[]> write, IProgress<int>? progress = null, Action<int, SyntheticWarp>? warps = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        ArgumentNullException.ThrowIfNull(write);
        if (red.Options.Seed != green.Options.Seed || blue.Options.Seed != green.Options.Seed)
        {
            throw new ArgumentException("The three colours are one atmosphere, so they share one seed.");
        }
        ArgumentOutOfRangeException.ThrowIfNotEqual(width % 2, 0, nameof(width));
        ArgumentOutOfRangeException.ThrowIfNotEqual(height % 2, 0, nameof(height));

        // One screen for the three: its modes are set by its spacing AND its size, so both are the same for every colour, the
        // size the largest any colour's pupil needs at its stride.
        var spacings = new[] { PupilSpacingM(arcsecPerPixel, red.Options), PupilSpacingM(arcsecPerPixel, green.Options), PupilSpacingM(arcsecPerPixel, blue.Options) };
        var screenSpacing = Math.Min(spacings[0], Math.Min(spacings[1], spacings[2]));
        var screenSamples = green.Options.ScreenSamples;
        foreach (var spacing in spacings)
        {
            screenSamples = Math.Max(screenSamples, NextPowerOfTwo((int)Math.Ceiling(PsfGrid * spacing / screenSpacing) + 2));
        }
        (red, green, blue) = (Shared(red), Shared(green), Shared(blue));
        BayerColour Shared(BayerColour colour) => colour with { Options = colour.Options with { ScreenSpacingM = screenSpacing, ScreenSamples = screenSamples } };

        var n = times.Length;
        var mosaic = new ushort[]?[n];
        var passes = new (BayerColour Colour, int[] Channels)[]
        {
            (red, [CfaPlaneStream.Red]),
            (green, [CfaPlaneStream.Green1, CfaPlaneStream.Green2]),
            (blue, [CfaPlaneStream.Blue]),
        };
        var records = new ImmutableArray<SyntheticFrame>[passes.Length];
        for (var p = 0; p < passes.Length; p++)
        {
            var (colour, channels) = passes[p];
            var phases = Array.ConvertAll(channels, c => CfaPlaneStream.PhaseOf(c, bayerOffsetX, bayerOffsetY));
            var passProgress = progress is null ? null : new PassProgress(progress, p, passes.Length, n);
            records[p] = await MakeAsync(colour.Map, planet, times, colour.Placement, arcsecPerPixel, shiftX, shiftY, brightness, width, height, colour.Options,
                (index, samples) =>
                {
                    var frame = mosaic[index] ??= new ushort[width * height];
                    foreach (var (px, py) in phases)
                    {
                        for (var y = py; y < height; y += 2)
                        {
                            var row = y * width;
                            for (var x = px; x < width; x += 2)
                            {
                                frame[row + x] = samples[row + x];
                            }
                        }
                    }
                }, passProgress, p == 1 ? warps : null, cancellationToken).ConfigureAwait(false);
        }

        for (var t = 0; t < n; t++)
        {
            write(t, mosaic[t] ?? throw new InvalidOperationException($"Frame {t} was made by no colour."));
            mosaic[t] = null;
        }
        return (records[0], records[1], records[2]);
    }

    // The pupil's sample spacing for a PSF on the fine grid: the grid's angular sample is the wavelength over the pupil grid's
    // extent, so it depends on the wavelength and the oversampling as well as the pixel scale.
    internal static double PupilSpacingM(double arcsecPerPixel, DegradeOptions options)
    {
        var sampleRadians = arcsecPerPixel / OversampleFor(arcsecPerPixel, options.Pupil.DiameterM, options.WavelengthM) / ShortExposurePsf.ArcsecPerRadian;
        return options.WavelengthM / (PsfGrid * sampleRadians);
    }

    // A screen's phase between its samples, bilinear: where a colour's pupil samples a screen drawn at another spacing.
    internal static double ScreenAt(double[] screen, int n, double x, double y)
    {
        var (x0, y0) = ((int)Math.Floor(x), (int)Math.Floor(y));
        var (fx, fy) = (x - x0, y - y0);
        var top = (screen[(y0 * n) + x0] * (1 - fx)) + (screen[(y0 * n) + x0 + 1] * fx);
        var bottom = (screen[((y0 + 1) * n) + x0] * (1 - fx)) + (screen[((y0 + 1) * n) + x0 + 1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }

    // A pass's frames reported as the share of the whole three-colour make they are.
    private sealed class PassProgress(IProgress<int> inner, int pass, int passes, int frames) : IProgress<int>
    {
        public void Report(int value) => inner.Report(((pass * frames) + value) / passes);
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
    private static ushort[] MakeFrame(Complex[] objectSpectrum, double[] psf, Complex[]? scatter, SyntheticWarpField warp, int fine, int os, int windowX, int windowY,
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
        // The telescope's wide scatter takes its share of the light from the frame's PSF, both unit-sum.
        var kept = scatter is null ? 1 : 1 - options.ScatterFraction;
        for (var ky = 0; ky < fine; ky++)
        {
            var fy = (ky < fine / 2 ? ky : ky - fine) / (double)fine;
            for (var kx = 0; kx < fine; kx++)
            {
                var fx = (kx < fine / 2 ? kx : kx - fine) / (double)fine;
                var i = (ky * fine) + kx;
                var transfer = scatter is null ? field[i] : (kept * field[i]) + (options.ScatterFraction * scatter[i]);
                field[i] = transfer * objectSpectrum[i] * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * dx) + (fy * dy)));
            }
        }
        Fft2D.Inverse(field, fine, fine);

        // Warped (each fine sample takes the value the field moved onto it) and binned to the detector's pixels.
        // Only the fine samples that make whole pixels: the grid is a power of two for the transform, so where the oversampling is
        // not (3 for a blue channel at 0.5"/px), the last fine % os samples are a partial pixel beyond the window, out in the sky.
        var windowPx = fine / os;
        var extent = windowPx * os;
        var binned = new double[windowPx * windowPx];
        var hasWarp = !warp.IsEmpty;
        for (var y = 0; y < extent; y++)
        {
            for (var x = 0; x < extent; x++)
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
    internal static double[] DefocusPhase(int n, double spacingM, double diameterM, double rmsM, double wavelengthM)
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

    // The scatter kernel, (1 + (r / core)^2)^(-3/2) with r and core in fine samples, periodic over the fine grid, unit-sum, as its
    // spectrum (centred on the origin, as the frame's PSF is).
    private static Complex[] ScatterSpectrum(int fine, double core)
    {
        var kernel = new Complex[fine * fine];
        double sum = 0;
        for (var y = 0; y < fine; y++)
        {
            var ry = Math.Min(y, fine - y);
            for (var x = 0; x < fine; x++)
            {
                var rx = Math.Min(x, fine - x);
                var v = Math.Pow(1 + (((rx * rx) + (ry * ry)) / (core * core)), -1.5);
                kernel[(y * fine) + x] = v;
                sum += v;
            }
        }
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }
        Fft2D.Forward(kernel, fine, fine);
        return kernel;
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

    internal static int NextPowerOfTwo(int value)
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
        public const int GridStep = SyntheticWarpField.GridStep;
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
        public SyntheticWarpField Current()
        {
            if (_rms <= 0)
            {
                return SyntheticWarpField.Empty;
            }
            var (x, y) = (new float[_x.Length], new float[_y.Length]);
            for (var i = 0; i < x.Length; i++)
            {
                x[i] = (float)_x[i];
                y[i] = (float)_y[i];
            }
            return new SyntheticWarpField(_nodes, x, y);
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

}
