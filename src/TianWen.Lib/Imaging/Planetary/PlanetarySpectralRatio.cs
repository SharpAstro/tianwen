using System;
using System.Collections.Immutable;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// A camera's noise per pixel in ADU (docs/plans/planetary-restoration.md, R7): the shot noise of what stands over the offset, the
/// read noise, and the rounding to whole ADU (a twelfth of an ADU squared, which holds where the noise before rounding is more than
/// a fraction of an ADU, and near enough on an 8-bit sky, whose rounded values barely spread).
/// </summary>
public readonly record struct CameraNoise(double OffsetAdu, double ElectronsPerAdu, double ReadNoiseAdu)
{
    /// <summary>A pixel's noise variance, ADU squared, at <paramref name="adu"/>.</summary>
    public double VarianceAt(double adu) => (Math.Max(0, adu - OffsetAdu) / ElectronsPerAdu) + (ReadNoiseAdu * ReadNoiseAdu) + (1.0 / 12);
}

/// <summary>One ring of a capture's spectral ratio.</summary>
/// <param name="CyclesPerPixel">The ring's spatial frequency.</param>
/// <param name="Ratio">The frames' mean spectrum squared over their mean power less the noise's: abs(mean T)^2 / mean abs(T)^2.</param>
/// <param name="PowerOverNoise">The frames' mean power over the noise's, which a ring must be well above to be read.</param>
/// <param name="Samples">The spectrum samples the ring holds.</param>
public readonly record struct SpectralRatioRing(double CyclesPerPixel, double Ratio, double PowerOverNoise, int Samples);

/// <summary>A capture's spectral ratio as measured (<see cref="PlanetarySpectralRatio.MeasureAsync"/>).</summary>
/// <param name="Rings">The ratio, ring by ring, lowest frequency first.</param>
/// <param name="Frames">The frames it averages.</param>
/// <param name="WindowSize">The square window's side, pixels.</param>
/// <param name="Reference">The mean of every frame registered, which each was registered against: the caller's to release.</param>
public sealed record SpectralRatioMeasurement(ImmutableArray<SpectralRatioRing> Rings, int Frames, int WindowSize, Image Reference);

/// <summary>A spectral ratio's Fried parameter (<see cref="PlanetarySpectralRatio.Fit"/>).</summary>
/// <param name="R0M">The free air's r0 at 500 nm, metres, whose theory fits the rings best.</param>
/// <param name="LogRms">The fit's RMS in the logarithm of the ratio over the rings it used.</param>
/// <param name="Rings">The rings fitted: their frequency, the measured ratio and the fitted theory's.</param>
public sealed record SpectralRatioFit(double R0M, double LogRms, ImmutableArray<(double CyclesPerPixel, double Measured, double Theory)> Rings);

/// <summary>
/// The spectral ratio (docs/plans/planetary-restoration.md, R7, part 1; von der Luehe 1984): over a capture's frames, each registered
/// on the disk, the mean spectrum squared over the mean power, ring by ring. A frame is S T_i O, a static blur S (the telescope, the
/// scatter, the pixel), the seeing's varying transfer T_i and the object O, so the ratio is abs(mean T)^2 / mean abs(T)^2: the static
/// blur and the object cancel, and what is left says how strong the varying air is, its r0, read against the same ratio of the
/// seeing model the synthetic capture is made with (<see cref="Theory"/>).
/// <para>
/// The noise is kept out of both halves. The mean spectrum's square is taken from the frames' cross terms alone (the square of their
/// sum less the sum of their squares), in which independent noise has no part; the mean power has the camera's noise
/// (<see cref="CameraNoise"/>) taken off, since no annulus of the spectrum holds noise alone here (the pupil's cutoff lies past the
/// pixels' Nyquist frequency).
/// </para>
/// </summary>
public sealed class PlanetarySpectralRatio
{
    private readonly int _n;
    private readonly float[] _window;
    private readonly Complex[] _field;
    private readonly Complex[] _sum;
    private readonly double[] _power;
    private readonly int[] _ringOf;
    private readonly int[] _ringSamples;
    private double _noiseSum;

    /// <param name="size">The square window's side, a power of two.</param>
    /// <param name="taper">The fraction of each side the window tapers over, which must lie on the sky.</param>
    public PlanetarySpectralRatio(int size, double taper = 0.1)
    {
        if (!ComplexFft.IsPowerOfTwo(size) || size < 16)
        {
            throw new ArgumentException($"The window's side must be a power of two of at least 16, got {size}.", nameof(size));
        }
        _n = size;
        var line = PlanetaryPowerSpectrum.Tukey(size, taper);
        _window = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                _window[(y * size) + x] = line[x] * line[y];
            }
        }
        _field = new Complex[size * size];
        _sum = new Complex[size * size];
        _power = new double[size * size];
        _ringOf = new int[size * size];
        var rings = 0;
        for (var ky = 0; ky < size; ky++)
        {
            var sy = ky < size / 2 ? ky : ky - size;
            for (var kx = 0; kx < size; kx++)
            {
                var sx = kx < size / 2 ? kx : kx - size;
                // The axes are left out, as the averaged power spectrum leaves them: row and column banding and the taper's own
                // leakage put power there.
                if (Math.Abs(sx) <= 1 || Math.Abs(sy) <= 1)
                {
                    _ringOf[(ky * size) + kx] = -1;
                    continue;
                }
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                _ringOf[(ky * size) + kx] = ring;
                rings = Math.Max(rings, ring + 1);
            }
        }
        _ringSamples = new int[rings];
        foreach (var ring in _ringOf)
        {
            if (ring >= 0)
            {
                _ringSamples[ring]++;
            }
        }
    }

    /// <summary>The window's side.</summary>
    public int Size => _n;

    /// <summary>How many frames have been added.</summary>
    public int Frames { get; private set; }

    /// <summary>
    /// Adds one frame's window, <see cref="Size"/> squared samples in ADU, row-major, whose content sampled at (x + <paramref name="dx"/>,
    /// y + <paramref name="dy"/>) lies where the reference's does: the fraction of the frame's shift its whole-pixel crop left, applied
    /// to its spectrum as a phase ramp. The sky, the mean of the tapered border, is taken off before the taper.
    /// </summary>
    public void Add(ReadOnlySpan<float> window, double dx, double dy, in CameraNoise camera)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(window.Length, _n * _n);
        double skySum = 0;
        var skyCount = 0;
        for (var i = 0; i < window.Length; i++)
        {
            if (_window[i] < 1f)
            {
                skySum += window[i];
                skyCount++;
            }
        }
        var sky = skySum / Math.Max(1, skyCount);
        double noise = 0;
        for (var i = 0; i < window.Length; i++)
        {
            var w = _window[i];
            _field[i] = new Complex((window[i] - sky) * w, 0);
            noise += w * w * camera.VarianceAt(window[i]);
        }
        Fft2D.Forward(_field, _n, _n);
        for (var ky = 0; ky < _n; ky++)
        {
            var sy = ky < _n / 2 ? ky : ky - _n;
            for (var kx = 0; kx < _n; kx++)
            {
                var i = (ky * _n) + kx;
                var sx = kx < _n / 2 ? kx : kx - _n;
                // g(x) = f(x + d) has the spectrum F(k) exp(2 pi i k d / n).
                var phase = 2 * Math.PI * ((sx * dx) + (sy * dy)) / _n;
                var value = _field[i] * new Complex(Math.Cos(phase), Math.Sin(phase));
                _sum[i] += value;
                _power[i] += (value.Real * value.Real) + (value.Imaginary * value.Imaginary);
            }
        }
        _noiseSum += noise;
        Frames++;
    }

    /// <summary>The ratio so far, ring by ring, lowest frequency first; empty under two frames.</summary>
    public ImmutableArray<SpectralRatioRing> Rings()
    {
        if (Frames < 2)
        {
            return [];
        }
        var rings = _ringSamples.Length;
        var cross = new double[rings];
        var power = new double[rings];
        for (var i = 0; i < _sum.Length; i++)
        {
            var ring = _ringOf[i];
            if (ring < 0)
            {
                continue;
            }
            var s = _sum[i];
            // The cross terms of the frames' sum: its square less each frame's own, which is the only place independent noise enters.
            cross[ring] += ((s.Real * s.Real) + (s.Imaginary * s.Imaginary) - _power[i]) / ((double)Frames * (Frames - 1));
            power[ring] += _power[i] / Frames;
        }
        // White noise of per-pixel variance v puts sum(w^2 v) in every sample of an unnormalised transform.
        var noisePerSample = _noiseSum / Frames;
        var builder = ImmutableArray.CreateBuilder<SpectralRatioRing>(rings);
        for (var ring = 0; ring < rings; ring++)
        {
            var samples = _ringSamples[ring];
            if (samples == 0)
            {
                continue;
            }
            var noise = noisePerSample * samples;
            builder.Add(new SpectralRatioRing((double)ring / _n, cross[ring] / (power[ring] - noise), power[ring] / noise, samples));
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The spectral ratio of <paramref name="stream"/>'s frames (one plane each, a value of 1 being <paramref name="fullScaleAdu"/>): the
    /// mean of every frame registered by a plain correlation is the reference, every frame is registered against it again and its
    /// window about the disk added (<see cref="Add"/>). A frame whose window leaves it is skipped. Null where no disk is found.
    /// </summary>
    public static async Task<SpectralRatioMeasurement?> MeasureAsync(IPlanetaryFrameStream stream, double fullScaleAdu, CameraNoise camera,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // The reference: every frame, registered plain (R5), which a single 8-bit frame is far too noisy to be.
        progress?.Report($"stacking every one of {stream.FrameCount} frames for the reference");
        var stack = await new LuckyImagingStacker().StackGlobalAsync(stream, new PlanetaryStackOptions { KeepFraction = 1, WhitenedCorrelation = false }, cancellationToken)
            .ConfigureAwait(false);
        var reference = stack.Master;
        var box = PlanetaryDisk.BoundingBox(reference);
        if (box.Width < 8 || box.Height < 8)
        {
            reference.Release();
            return null;
        }
        var (cx, cy) = PlanetaryDisk.CenterOfMass(reference, box);
        var radius = Math.Max(box.Width, box.Height) / 2.0;
        // The window: the disk, its halo and the taper's margin, as the synthetic capture's own window is sized; no larger than the frame.
        var size = PlanetaryDegrade.NextPowerOfTwo((int)Math.Ceiling((2.6 * radius) + 16));
        while (size > Math.Min(stream.Width, stream.Height))
        {
            size /= 2;
        }
        var (x0, y0) = ((int)Math.Round(cx) - (size / 2), (int)Math.Round(cy) - (size / 2));
        var aligner = LuckyImagingStacker.AlignerFor(reference, box, 0, whiten: false);
        var ratio = new PlanetarySpectralRatio(size);
        var window = new float[size * size];
        for (var i = 0; i < stream.FrameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await stream.LoadAsync(i, cancellationToken).ConfigureAwait(false);
            try
            {
                var shift = aligner.Estimate(frame, PlanetaryDisk.BoundingBox(frame));
                var (ix, iy) = ((int)Math.Round(shift.Dx), (int)Math.Round(shift.Dy));
                var (fx, fy) = (x0 + ix, y0 + iy);
                if (fx < 0 || fy < 0 || fx + size > frame.Width || fy + size > frame.Height)
                {
                    continue;
                }
                var plane = frame.GetChannelSpan(0);
                for (var y = 0; y < size; y++)
                {
                    var row = plane.Slice(((fy + y) * frame.Width) + fx, size);
                    for (var x = 0; x < size; x++)
                    {
                        window[(y * size) + x] = (float)(row[x] * fullScaleAdu);
                    }
                }
                ratio.Add(window, shift.Dx - ix, shift.Dy - iy, camera);
            }
            finally
            {
                frame.Release();
            }
            if ((i + 1) % 500 == 0)
            {
                progress?.Report($"{i + 1} of {stream.FrameCount} frames");
            }
        }
        return new SpectralRatioMeasurement(ratio.Rings(), ratio.Frames, size, reference);
    }

    /// <summary>
    /// The spectral ratio of the seeing model <paramref name="seeing"/> describes (<see cref="SeeingPsfSequence"/>, the synthetic
    /// capture's own), its free air's phase multiplied by <paramref name="freeAirScale"/>: <paramref name="exposures"/> PSFs, each
    /// <paramref name="stepSeconds"/> of wind after the last (by default the time the wind takes to carry the air one and a half pupils,
    /// so they are all but independent), each registered on its centroid as a frame is on its disk. Ring by ring in cycles a detector
    /// pixel, lowest first.
    /// </summary>
    public static ImmutableArray<(double CyclesPerPixel, double Ratio)> Theory(DegradeOptions seeing, double arcsecPerPixel, double freeAirScale = 1, int exposures = 400,
        double stepSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(seeing);
        ArgumentOutOfRangeException.ThrowIfLessThan(exposures, 2);
        const int n = PlanetaryDegrade.PsfGrid;
        var os = PlanetaryDegrade.OversampleFor(arcsecPerPixel, seeing.Pupil.DiameterM, seeing.WavelengthM);
        var step = stepSeconds > 0 ? stepSeconds : 1.5 * seeing.Pupil.DiameterM / Math.Max(seeing.WindMps, 1);
        var sequence = new SeeingPsfSequence(seeing, arcsecPerPixel, freeAirScale);
        var psf = new double[n * n];
        var field = new Complex[n * n];
        var sum = new Complex[n * n];
        var power = new double[n * n];
        for (var e = 0; e < exposures; e++)
        {
            if (e > 0)
            {
                sequence.Step(step);
            }
            sequence.Exposure(psf);
            var (cx, cy) = PlanetaryDegrade.Centroid(psf);
            for (var i = 0; i < psf.Length; i++)
            {
                field[i] = psf[i];
            }
            Fft2D.Forward(field, n, n);
            for (var ky = 0; ky < n; ky++)
            {
                var sy = ky < n / 2 ? ky : ky - n;
                for (var kx = 0; kx < n; kx++)
                {
                    var i = (ky * n) + kx;
                    var sx = kx < n / 2 ? kx : kx - n;
                    // Registered on its centroid: p(x + c) has the spectrum P(k) exp(2 pi i k c / n).
                    var phase = 2 * Math.PI * ((sx * cx) + (sy * cy)) / n;
                    var value = field[i] * new Complex(Math.Cos(phase), Math.Sin(phase));
                    sum[i] += value;
                    power[i] += (value.Real * value.Real) + (value.Imaginary * value.Imaginary);
                }
            }
        }
        var rings = (n / 2) + 1;
        var cross = new double[rings];
        var mean = new double[rings];
        for (var ky = 0; ky < n; ky++)
        {
            var sy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var sx = kx < n / 2 ? kx : kx - n;
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                if (ring >= rings)
                {
                    continue;
                }
                var i = (ky * n) + kx;
                var s = sum[i];
                cross[ring] += ((s.Real * s.Real) + (s.Imaginary * s.Imaginary) - power[i]) / ((double)exposures * (exposures - 1));
                mean[ring] += power[i] / exposures;
            }
        }
        var builder = ImmutableArray.CreateBuilder<(double, double)>(rings);
        for (var ring = 0; ring < rings; ring++)
        {
            // A ring of the fine grid is os / n cycles a detector pixel.
            builder.Add(((double)ring * os / n, mean[ring] > 0 ? cross[ring] / mean[ring] : double.NaN));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// The free air's r0 (at 500 nm) whose <see cref="Theory"/> fits <paramref name="measured"/> best in the logarithm of the ratio, over the
    /// rings whose power is at least <paramref name="minPowerOverNoise"/> times the noise's, between <paramref name="minCyclesPerPixel"/>
    /// and the pixels' Nyquist frequency, with a ratio above zero. The theories are read on a grid of r0 from <paramref name="minR0M"/> to
    /// <paramref name="maxR0M"/>, every one from the same draws (<paramref name="seeing"/>'s seed) scaled, so the fit is smooth in r0;
    /// the best is placed between them by a parabola in log r0. Null when no ring can be read.
    /// </summary>
    public static SpectralRatioFit? Fit(ImmutableArray<SpectralRatioRing> measured, DegradeOptions seeing, double arcsecPerPixel, double minPowerOverNoise = 4,
        double minCyclesPerPixel = 0.02, double minR0M = 0.02, double maxR0M = 0.4, int grid = 61, int exposures = 400)
    {
        ArgumentNullException.ThrowIfNull(seeing);
        var used = ImmutableArray.CreateBuilder<SpectralRatioRing>();
        foreach (var ring in measured)
        {
            if (ring.CyclesPerPixel >= minCyclesPerPixel && ring.CyclesPerPixel <= 0.5 && ring.PowerOverNoise >= minPowerOverNoise && ring.Ratio > 0 && double.IsFinite(ring.Ratio))
            {
                used.Add(ring);
            }
        }
        if (used.Count < 3)
        {
            return null;
        }
        // One r0 the draws are made at; every other is the same air, its phase scaled by (r0 / r0')^(5/6).
        var baseR0 = seeing.R0M;
        var r0s = new double[grid];
        var theories = new ImmutableArray<(double CyclesPerPixel, double Ratio)>[grid];
        for (var j = 0; j < grid; j++)
        {
            r0s[j] = minR0M * Math.Pow(maxR0M / minR0M, (double)j / (grid - 1));
        }
        Parallel.For(0, grid, j => theories[j] = Theory(seeing, arcsecPerPixel, Math.Pow(baseR0 / r0s[j], 5.0 / 6), exposures));
        var residuals = new double[grid];
        for (var j = 0; j < grid; j++)
        {
            residuals[j] = LogResidual(used, theories[j], out _);
        }
        var best = 0;
        for (var j = 1; j < grid; j++)
        {
            if (residuals[j] < residuals[best])
            {
                best = j;
            }
        }
        var logR0 = Math.Log(r0s[best]);
        if (best > 0 && best < grid - 1 && double.IsFinite(residuals[best - 1]) && double.IsFinite(residuals[best + 1]))
        {
            var (m, z, p) = (residuals[best - 1], residuals[best], residuals[best + 1]);
            var curvature = m - (2 * z) + p;
            var step = Math.Log(r0s[best + 1] / r0s[best]);
            logR0 += curvature > 0 ? 0.5 * (m - p) / curvature * step : 0;
        }
        var r0 = Math.Exp(logR0);
        var fitted = Theory(seeing, arcsecPerPixel, Math.Pow(baseR0 / r0, 5.0 / 6), exposures);
        var rms = Math.Sqrt(LogResidual(used, fitted, out var curve));
        return new SpectralRatioFit(r0, rms, curve);
    }

    // The mean square of log(measured / theory) over the rings, the theory read between its own rings; its curve for a report.
    private static double LogResidual(ImmutableArray<SpectralRatioRing>.Builder used, ImmutableArray<(double CyclesPerPixel, double Ratio)> theory,
        out ImmutableArray<(double, double, double)> curve)
    {
        var builder = ImmutableArray.CreateBuilder<(double, double, double)>(used.Count);
        double sum = 0;
        var count = 0;
        foreach (var ring in used)
        {
            var t = Interpolate(theory, ring.CyclesPerPixel);
            builder.Add((ring.CyclesPerPixel, ring.Ratio, t));
            if (t > 0 && double.IsFinite(t))
            {
                var d = Math.Log(ring.Ratio / t);
                sum += d * d;
                count++;
            }
        }
        curve = builder.ToImmutable();
        return count == used.Count ? sum / count : double.PositiveInfinity;
    }

    private static double Interpolate(ImmutableArray<(double CyclesPerPixel, double Ratio)> curve, double f)
    {
        for (var i = 1; i < curve.Length; i++)
        {
            if (curve[i].CyclesPerPixel >= f)
            {
                var (f0, r0) = curve[i - 1];
                var (f1, r1) = curve[i];
                return r0 + ((r1 - r0) * (f - f0) / (f1 - f0));
            }
        }
        return double.NaN;
    }
}
