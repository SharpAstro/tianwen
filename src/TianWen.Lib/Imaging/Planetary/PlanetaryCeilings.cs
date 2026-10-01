using System;
using System.Collections.Immutable;
using System.Numerics;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// The ceilings a sharpening of a stack is judged against (docs/plans/planetary-restoration.md, R8 part 1), each needing the truth:
/// the best real gain per a trous band, the best isotropic linear filter, and the stack's Fourier magnitude or phase swapped for the
/// truth's (ASTRA-SR's oracle swap). Every plane is row-major, both normalised and registered on the same disk.
/// </summary>
public static class PlanetaryCeilings
{
    /// <summary>
    /// <paramref name="stack"/> with each a trous band times the least-squares gain that brings it nearest the truth's band inside 0.9
    /// radii, the residual kept: the most a real per-band gain can reach. The gains, finest first, beside it.
    /// </summary>
    public static (float[] Plane, ImmutableArray<double> Gains) PerBandOracle(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk,
        int bands = PlanetaryMetrics.Bands)
    {
        var s = ATrousWaveletTransform.Decompose(stack, width, height, bands);
        var t = ATrousWaveletTransform.Decompose(truth, width, height, bands);
        var inside = PlanetaryMetrics.Inside(width, height, disk, PlanetaryMetrics.InnerRadii);
        var gains = new float[bands];
        var report = ImmutableArray.CreateBuilder<double>(bands);
        for (var j = 0; j < bands; j++)
        {
            var sj = s.Detail(j);
            var tj = t.Detail(j);
            double st = 0, ss = 0;
            foreach (var i in inside)
            {
                st += (double)sj[i] * tj[i];
                ss += (double)sj[i] * sj[i];
            }
            var gain = ss > 0 ? st / ss : 1;
            gains[j] = (float)gain;
            report.Add(gain);
        }
        return (s.Reconstruct(gains), report.MoveToImmutable());
    }

    /// <summary>
    /// <paramref name="stack"/> with one gain per a trous band fitted JOINTLY: the gains that bring every band of the result nearest the
    /// truth's at once inside 0.9 radii. A trous bands overlap in frequency, so a band's gain reaches its neighbours' bands too, and the
    /// gains <see cref="PerBandOracle"/> fits one band at a time are not the best set.
    /// </summary>
    public static (float[] Plane, ImmutableArray<double> Gains) PerBandJointOracle(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk,
        int bands = PlanetaryMetrics.Bands)
    {
        var (a, b) = JointNormalEquations(stack, truth, width, height, disk, bands);
        var solved = Solve(a, b);
        var gains = new float[bands];
        for (var k = 0; k < bands; k++)
        {
            gains[k] = (float)solved[k];
        }
        return (ATrousWaveletTransform.Decompose(stack, width, height, bands).Reconstruct(gains), [.. solved]);
    }

    /// <summary>
    /// The least-squares system <see cref="PerBandJointOracle"/> solves: for gains x on <paramref name="stack"/>'s first
    /// <paramref name="bands"/> a trous layers (its residual kept), the error of every band of the result against
    /// <paramref name="truth"/>'s, summed over the pixels inside 0.9 radii, is x' a x - 2 b' x plus a constant.
    /// </summary>
    internal static (double[,] A, double[] B) JointNormalEquations(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, MetricDisk disk, int bands)
    {
        var s = ATrousWaveletTransform.Decompose(stack, width, height, bands);
        var t = ATrousWaveletTransform.Decompose(truth, width, height, bands);
        var inside = PlanetaryMetrics.Inside(width, height, disk, PlanetaryMetrics.InnerRadii);
        // Each of the stack's detail planes, and its residual, decomposed again: what a gain on it puts in every band of the result.
        var parts = new WaveletDecomposition[bands];
        for (var k = 0; k < bands; k++)
        {
            parts[k] = ATrousWaveletTransform.Decompose(s.Detail(k), width, height, bands);
        }
        var rest = ATrousWaveletTransform.Decompose(s.Residual, width, height, bands);
        var a = new double[bands, bands];
        var b = new double[bands];
        for (var j = 0; j < bands; j++)
        {
            var tj = t.Detail(j);
            var rj = rest.Detail(j);
            for (var k = 0; k < bands; k++)
            {
                var kj = parts[k].Detail(j);
                foreach (var i in inside)
                {
                    b[k] += (double)kj[i] * (tj[i] - rj[i]);
                }
                for (var l = k; l < bands; l++)
                {
                    var lj = parts[l].Detail(j);
                    double sum = 0;
                    foreach (var i in inside)
                    {
                        sum += (double)kj[i] * lj[i];
                    }
                    a[k, l] += sum;
                    if (l != k)
                    {
                        a[l, k] += sum;
                    }
                }
            }
        }
        return (a, b);
    }

    // The n-by-n system a x = b by Gaussian elimination with partial pivoting.
    internal static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var (m, x) = ((double[,])a.Clone(), (double[])b.Clone());
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
            {
                if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c]))
                {
                    pivot = r;
                }
            }
            for (var k = 0; k < n; k++)
            {
                (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
            }
            (x[c], x[pivot]) = (x[pivot], x[c]);
            for (var r = c + 1; r < n; r++)
            {
                var f = m[r, c] / m[c, c];
                for (var k = c; k < n; k++)
                {
                    m[r, k] -= f * m[c, k];
                }
                x[r] -= f * x[c];
            }
        }
        for (var c = n - 1; c >= 0; c--)
        {
            for (var k = c + 1; k < n; k++)
            {
                x[c] -= m[c, k] * x[k];
            }
            x[c] /= m[c, c];
        }
        return x;
    }

    /// <summary>
    /// <paramref name="stack"/> through the best isotropic linear filter the truth allows: over each ring of frequencies, the sum of
    /// Re(T conj(S)) over the sum of abs(S)^2.
    /// </summary>
    public static float[] PerRingOracle(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height)
    {
        var n = PlanetaryInverse.GridFor(width, height, 0);
        var s = PlanetaryInverse.Transform(stack, width, height, n);
        var t = PlanetaryInverse.Transform(truth, width, height, n);
        var (num, den) = (new double[n], new double[n]);
        for (var i = 0; i < s.Length; i++)
        {
            var ring = Ring(i, n);
            num[ring] += (t[i].Real * s[i].Real) + (t[i].Imaginary * s[i].Imaginary);
            den[ring] += (s[i].Real * s[i].Real) + (s[i].Imaginary * s[i].Imaginary);
        }
        for (var i = 0; i < s.Length; i++)
        {
            var ring = Ring(i, n);
            s[i] *= den[ring] > 0 ? num[ring] / den[ring] : 0;
        }
        return Back(s, width, height, n);
    }

    /// <summary>The truth's Fourier magnitude with the stack's phase: the best any zero-phase filter could do, noise taken away too.</summary>
    public static float[] MagnitudeSwap(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height) => Swap(stack, truth, width, height, magnitudeFromTruth: true);

    /// <summary>The stack's Fourier magnitude with the truth's phase: how much of the stack's error lies in its phase.</summary>
    public static float[] PhaseSwap(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height) => Swap(stack, truth, width, height, magnitudeFromTruth: false);

    private static float[] Swap(ReadOnlySpan<float> stack, ReadOnlySpan<float> truth, int width, int height, bool magnitudeFromTruth)
    {
        var n = PlanetaryInverse.GridFor(width, height, 0);
        var s = PlanetaryInverse.Transform(stack, width, height, n);
        var t = PlanetaryInverse.Transform(truth, width, height, n);
        for (var i = 0; i < s.Length; i++)
        {
            var (magnitude, phase) = magnitudeFromTruth ? (t[i].Magnitude, s[i].Phase) : (s[i].Magnitude, t[i].Phase);
            s[i] = Complex.FromPolarCoordinates(magnitude, phase);
        }
        return Back(s, width, height, n);
    }

    // The ring of frequency index i on an n-by-n grid: its distance from zero frequency, rounded.
    internal static int Ring(int i, int n)
    {
        var (ky, kx) = Math.DivRem(i, n);
        var sy = ky < n / 2 ? ky : ky - n;
        var sx = kx < n / 2 ? kx : kx - n;
        return (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
    }

    private static float[] Back(Complex[] field, int width, int height, int n)
    {
        Fft2D.Inverse(field, n, n);
        var result = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[(y * width) + x] = (float)field[(y * n) + x].Real;
            }
        }
        return result;
    }
}

/// <summary>
/// The multi-frame bound on a synthetic capture whose every frame's PSF is known (docs/plans/planetary-restoration.md, R8 part 1), in
/// the Fourier domain on a fixed square window of the detector. A frame, its offset taken off and divided by its brightness, is its
/// true transfer G times the truth: G is the frame's PSF and the telescope's wide scatter over the pupil's own diffraction (the truth is
/// rendered through it), at the shift the frame was given. Two restorations know the kernels exactly: shift-and-add at the TRUE shifts
/// with a single-image Wiener by that sum's own 2-D transfer, and the multi-frame Wiener, which weights every frame per frequency.
/// </summary>
public sealed class MultiFrameBound
{
    // A frequency where the perfect telescope passes less than this is past what any restoration toward its truth can reach.
    private const double DiffractionFloor = 0.02;

    private readonly SyntheticPsfHeader _header;
    private readonly int _fine;
    private readonly Complex[] _diffraction;
    private readonly double[] _scatter;

    /// <param name="header">What every frame of the capture shares.</param>
    /// <param name="size">The window's side, in detector pixels, a power of two.</param>
    /// <param name="originX">The window's corner on the detector, x.</param>
    /// <param name="originY">The window's corner on the detector, y.</param>
    public MultiFrameBound(SyntheticPsfHeader header, int size, int originX, int originY)
    {
        ArgumentNullException.ThrowIfNull(header);
        (_header, Size, OriginX, OriginY) = (header, size, originX, originY);
        _fine = size * header.Oversample;
        if (_fine < header.PsfGrid)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"A window of {size} px is narrower than the PSF grid.");
        }
        _diffraction = TransferOnGrid(header.Diffraction);
        // The scatter's (1 + (r / a)^2)^(-3/2), unit-sum, has the transfer exp(-2 pi a f).
        var core = header.ScatterCoreArcsec / header.ArcsecPerPixel;
        _scatter = new double[size * size];
        for (var i = 0; i < _scatter.Length; i++)
        {
            var (fx, fy) = Frequency(i);
            _scatter[i] = Math.Exp(-2 * Math.PI * core * Math.Sqrt((fx * fx) + (fy * fy)));
        }
    }

    /// <summary>The window's side, px.</summary>
    public int Size { get; }

    /// <summary>The window's corner on the detector, x.</summary>
    public int OriginX { get; }

    /// <summary>The window's corner on the detector, y.</summary>
    public int OriginY { get; }

    /// <summary>One frame in the window's Fourier domain, with what it was imaged through.</summary>
    /// <param name="Spectrum">The frame, offset off and over its brightness, transformed.</param>
    /// <param name="Transfer">Its true transfer to the truth.</param>
    /// <param name="Noise">The variance of a coefficient of <paramref name="Spectrum"/> the camera adds.</param>
    /// <param name="MoveX">Where the frame's content lies over the truth's, x, px: the shift given and the PSF's own tilt.</param>
    /// <param name="MoveY">The same in y.</param>
    public sealed record Frame(Complex[] Spectrum, Complex[] Transfer, double Noise, double MoveX, double MoveY);

    /// <summary>
    /// One frame's samples (row-major, <paramref name="frameWidth"/> wide) in the window, with its PSF (the header's fine grid,
    /// centred), the shift it was given and its brightness, as the capture's PSF file records them.
    /// </summary>
    public Frame Prepare(ReadOnlySpan<ushort> samples, int frameWidth, int frameHeight, double[] psf, double shiftX, double shiftY, double brightness)
    {
        ArgumentNullException.ThrowIfNull(psf);
        var n = Size;
        var field = new Complex[n * n];
        double signal = 0;
        for (var y = 0; y < n; y++)
        {
            var fy = OriginY + y;
            if (fy < 0 || fy >= frameHeight)
            {
                continue;
            }
            for (var x = 0; x < n; x++)
            {
                var fx = OriginX + x;
                if (fx < 0 || fx >= frameWidth)
                {
                    continue;
                }
                var v = samples[(fy * frameWidth) + fx] - _header.OffsetAdu;
                signal += Math.Max(0, v);
                field[(y * n) + x] = v / brightness;
            }
        }
        Fft2D.Forward(field, n, n);
        // White noise of a variance per pixel puts n^2 times it on every coefficient: read noise, the rounding, and the shot noise of
        // the window's mean signal.
        var variance = (_header.ReadNoiseAdu * _header.ReadNoiseAdu) + (1.0 / 12) + (signal / (n * n) / _header.ElectronsPerAdu);
        var noise = n * n * variance / (brightness * brightness);

        var h = TransferOnGrid(psf);
        var s = _header.ScatterFraction;
        var transfer = new Complex[n * n];
        for (var i = 0; i < transfer.Length; i++)
        {
            var d = _diffraction[i];
            if (d.Magnitude < DiffractionFloor)
            {
                continue;
            }
            var (fx, fy) = Frequency(i);
            var optics = ((1 - s) * h[i]) + (s * _scatter[i]);
            transfer[i] = optics * Complex.FromPolarCoordinates(1, -2 * Math.PI * ((fx * shiftX) + (fy * shiftY))) / d;
        }
        var (cx, cy) = Centroid(psf, _header.PsfGrid);
        return new Frame(field, transfer, noise, shiftX + (cx / _header.Oversample), shiftY + (cy / _header.Oversample));
    }

    /// <summary>The truth's power, ring by ring, on the window's grid: the prior both Wiener restorations take.</summary>
    public double[] Prior(ReadOnlySpan<float> truth, int width, int height)
    {
        var t = Window(truth, width, height);
        var n = Size;
        var (sum, count) = (new double[n], new int[n]);
        for (var i = 0; i < t.Length; i++)
        {
            var ring = PlanetaryCeilings.Ring(i, n);
            sum[ring] += (t[i].Real * t[i].Real) + (t[i].Imaginary * t[i].Imaginary);
            count[ring]++;
        }
        var prior = new double[n * n];
        for (var i = 0; i < prior.Length; i++)
        {
            var ring = PlanetaryCeilings.Ring(i, n);
            prior[i] = count[ring] > 0 ? sum[ring] / count[ring] : 0;
        }
        return prior;
    }

    /// <summary>The truth in the window, transformed.</summary>
    public Complex[] Window(ReadOnlySpan<float> plane, int width, int height)
    {
        var n = Size;
        var field = new Complex[n * n];
        for (var y = 0; y < n; y++)
        {
            var fy = OriginY + y;
            for (var x = 0; x < n; x++)
            {
                var fx = OriginX + x;
                if (fx >= 0 && fy >= 0 && fx < width && fy < height)
                {
                    field[(y * n) + x] = plane[(fy * width) + fx];
                }
            }
        }
        Fft2D.Forward(field, n, n);
        return field;
    }

    /// <summary>A spectrum on the window's grid back on a detector of <paramref name="width"/> by <paramref name="height"/>, zero outside the window.</summary>
    public float[] ToDetector(Complex[] spectrum, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        var n = Size;
        var field = (Complex[])spectrum.Clone();
        Fft2D.Inverse(field, n, n);
        var plane = new float[width * height];
        for (var y = 0; y < n; y++)
        {
            var fy = OriginY + y;
            for (var x = 0; x < n; x++)
            {
                var fx = OriginX + x;
                if (fx >= 0 && fy >= 0 && fx < width && fy < height)
                {
                    plane[(fy * width) + fx] = (float)field[(y * n) + x].Real;
                }
            }
        }
        return plane;
    }

    /// <summary>The frames a restoration is made from, gathered as they come.</summary>
    public sealed class Sums
    {
        private readonly MultiFrameBound _owner;
        private readonly Complex[] _numerator;
        private readonly double[] _denominator;
        private readonly Complex[] _sum;
        private readonly Complex[] _sumTransfer;
        private double _noise;

        internal Sums(MultiFrameBound owner)
        {
            _owner = owner;
            var cells = owner.Size * owner.Size;
            (_numerator, _denominator, _sum, _sumTransfer) = (new Complex[cells], new double[cells], new Complex[cells], new Complex[cells]);
        }

        /// <summary>The frames gathered.</summary>
        public int Count { get; private set; }

        /// <summary>One more frame.</summary>
        public void Add(Frame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            var weight = 1 / frame.Noise;
            for (var i = 0; i < _numerator.Length; i++)
            {
                var g = frame.Transfer[i];
                _numerator[i] += weight * Complex.Conjugate(g) * frame.Spectrum[i];
                _denominator[i] += weight * ((g.Real * g.Real) + (g.Imaginary * g.Imaginary));
                // At the true shift: the content moved back onto the truth's, its transfer with it.
                var (fx, fy) = _owner.Frequency(i);
                var back = Complex.FromPolarCoordinates(1, 2 * Math.PI * ((fx * frame.MoveX) + (fy * frame.MoveY)));
                _sum[i] += frame.Spectrum[i] * back;
                _sumTransfer[i] += g * back;
            }
            _noise += frame.Noise;
            Count++;
        }

        /// <summary>The multi-frame Wiener: the sum of conj(G) F over that of abs(G)^2 plus the noise over <paramref name="prior"/>.</summary>
        public Complex[] MultiFrame(double[] prior)
        {
            ArgumentNullException.ThrowIfNull(prior);
            var result = new Complex[_numerator.Length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = prior[i] > 0 && _denominator[i] > 0 ? _numerator[i] / (_denominator[i] + (1 / prior[i])) : Complex.Zero;
            }
            return result;
        }

        /// <summary>The frames summed at their true shifts, and a single-image Wiener with that sum's own 2-D transfer.</summary>
        public Complex[] ShiftAndAdd(double[] prior)
        {
            ArgumentNullException.ThrowIfNull(prior);
            var count = Math.Max(1, Count);
            var noise = _noise / ((double)count * count);
            var result = new Complex[_sum.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var h = _sumTransfer[i] / count;
                var power = (h.Real * h.Real) + (h.Imaginary * h.Imaginary);
                result[i] = prior[i] > 0 && power > 0 ? Complex.Conjugate(h) * (_sum[i] / count) / (power + (noise / prior[i])) : Complex.Zero;
            }
            return result;
        }

        /// <summary>The frames summed at their true shifts, unrestored.</summary>
        public Complex[] Sum()
        {
            var count = Math.Max(1, Count);
            var result = new Complex[_sum.Length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = _sum[i] / count;
            }
            return result;
        }
    }

    /// <summary>A fresh gathering of frames.</summary>
    public Sums NewSums() => new Sums(this);

    /// <summary>
    /// How well the frames follow the model: in each a trous band's frequencies (band j from 2^-(j+1) to 2^-j cycles a pixel), the
    /// power of what a frame holds beyond its transfer times the truth, over the power its camera noise puts there.
    /// </summary>
    public sealed class ModelCheck
    {
        private readonly MultiFrameBound _owner;
        private readonly Complex[] _truth;
        private readonly double[] _residual;
        private readonly double[] _expected;
        private readonly double[] _frame;
        private readonly double[] _bandCross;
        private readonly double[] _bandPower;
        private double _cross;
        private double _power;

        internal ModelCheck(MultiFrameBound owner, Complex[] truth, int bands)
        {
            (_owner, _truth) = (owner, truth);
            (_residual, _expected, _frame, _bandCross, _bandPower) = (new double[bands], new double[bands], new double[bands], new double[bands], new double[bands]);
        }

        /// <summary>One more frame.</summary>
        public void Add(Frame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            for (var i = 0; i < _truth.Length; i++)
            {
                var (fx, fy) = _owner.Frequency(i);
                var f = Math.Sqrt((fx * fx) + (fy * fy));
                if (f <= 0 || f >= 0.5)
                {
                    continue;
                }
                var band = (int)Math.Floor(-Math.Log2(f)) - 1;
                if (band < 0 || band >= _residual.Length)
                {
                    continue;
                }
                var model = frame.Transfer[i] * _truth[i];
                var spectrum = frame.Spectrum[i];
                var r = spectrum - model;
                _residual[band] += (r.Real * r.Real) + (r.Imaginary * r.Imaginary);
                _expected[band] += frame.Noise;
                var cross = (spectrum.Real * model.Real) + (spectrum.Imaginary * model.Imaginary);
                var power = (model.Real * model.Real) + (model.Imaginary * model.Imaginary);
                _frame[band] += (spectrum.Real * spectrum.Real) + (spectrum.Imaginary * spectrum.Imaginary);
                _bandCross[band] += cross;
                _bandPower[band] += power;
                if (band >= 1)
                {
                    _cross += cross;
                    _power += power;
                }
            }
        }

        /// <summary>Band by band, finest first, the residual's power over the noise's.</summary>
        public ImmutableArray<double> ResidualOverNoise
        {
            get
            {
                var result = ImmutableArray.CreateBuilder<double>(_residual.Length);
                for (var j = 0; j < _residual.Length; j++)
                {
                    result.Add(_expected[j] > 0 ? _residual[j] / _expected[j] : double.NaN);
                }
                return result.MoveToImmutable();
            }
        }

        /// <summary>The least-squares scale of the model to the frames over bands 2 and on: one when the frames are in the truth's units.</summary>
        public double Scale => _power > 0 ? _cross / _power : double.NaN;

        /// <summary>
        /// Band by band, the residual's power over the noise's once the model is multiplied by <see cref="Scale"/>: the transfer's shape
        /// alone, where a scale off one is the truth's units (a twin's object is scaled to its disk level before the blur, its truth after).
        /// </summary>
        public ImmutableArray<double> ScaledResidualOverNoise
        {
            get
            {
                var a = Scale;
                var result = ImmutableArray.CreateBuilder<double>(_residual.Length);
                for (var j = 0; j < _residual.Length; j++)
                {
                    result.Add(_expected[j] > 0 ? (_frame[j] - (2 * a * _bandCross[j]) + (a * a * _bandPower[j])) / _expected[j] : double.NaN);
                }
                return result.MoveToImmutable();
            }
        }
    }

    /// <summary>A fresh model check against <paramref name="truth"/> (on the window, transformed) over <paramref name="bands"/> bands.</summary>
    public ModelCheck NewModelCheck(Complex[] truth, int bands = 4) => new ModelCheck(this, truth, bands);

    // A PSF's centroid over its centre sample, in fine samples.
    private static (double X, double Y) Centroid(double[] psf, int grid)
    {
        double cx = 0, cy = 0, sum = 0;
        for (var y = 0; y < grid; y++)
        {
            for (var x = 0; x < grid; x++)
            {
                var v = psf[(y * grid) + x];
                cx += v * (x - (grid / 2));
                cy += v * (y - (grid / 2));
                sum += v;
            }
        }
        return sum > 0 ? (cx / sum, cy / sum) : (0, 0);
    }

    // The frequency, cycles a pixel, of index i on the window's grid.
    private (double X, double Y) Frequency(int i)
    {
        var n = Size;
        var (ky, kx) = Math.DivRem(i, n);
        return ((kx < n / 2 ? kx : kx - n) / (double)n, (ky < n / 2 ? ky : ky - n) / (double)n);
    }

    // A PSF on the header's fine grid (centred on PsfGrid / 2) as its transfer on the window's grid: transformed on a fine grid the
    // window's side times the oversampling, whose index k is the window's frequency k / size cycles a pixel.
    private Complex[] TransferOnGrid(ReadOnlySpan<double> psf)
    {
        var (g, m, n) = (_header.PsfGrid, _fine, Size);
        var field = new Complex[m * m];
        for (var y = 0; y < g; y++)
        {
            var my = ((y - (g / 2)) + m) % m;
            for (var x = 0; x < g; x++)
            {
                var mx = ((x - (g / 2)) + m) % m;
                field[(my * m) + mx] = psf[(y * g) + x];
            }
        }
        Fft2D.Forward(field, m, m);
        var result = new Complex[n * n];
        for (var ky = 0; ky < n; ky++)
        {
            var my = ((ky < n / 2 ? ky : ky - n) + m) % m;
            for (var kx = 0; kx < n; kx++)
            {
                var mx = ((kx < n / 2 ? kx : kx - n) + m) % m;
                result[(ky * n) + kx] = field[(my * m) + mx];
            }
        }
        return result;
    }
}
