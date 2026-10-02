using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace TianWen.Lib.Stat;

/// <summary>
/// Sub-pixel image registration by phase correlation. The normalised cross-power spectrum of two tiles
/// inverse-transforms to a sharp correlation peak whose offset is the translation between them; a
/// parabolic fit around the peak refines it to sub-pixel. Translation-only (rotation/scale are handled
/// upstream by the disk centroid + the AP mesh); both tiles must share the same power-of-two dimensions
/// (the caller crops / zero-pads a tile around the disk). This is the global-bootstrap + per-AP matcher
/// of the planetary aligner, kept in <c>Stat/</c> as a general primitive.
/// </summary>
public static class PhaseCorrelation
{
    /// <summary>
    /// The displacement of <c>moving</c> relative to <c>reference</c>: <c>moving(x, y) ~=
    /// reference(x - Dx, y - Dy)</c>. To register <c>moving</c> onto <c>reference</c>, translate it by
    /// <c>(-Dx, -Dy)</c>. <see cref="PeakValue"/> is the correlation peak height (a confidence proxy in
    /// [0, ~1] for the normalised spectrum).
    /// </summary>
    public readonly record struct Shift(double Dx, double Dy, double PeakValue);

    /// <summary>
    /// Estimates the translation between two equal-size, power-of-two tiles by phase correlation.
    /// </summary>
    /// <param name="reference">Reference tile, row-major, length <c>width*height</c>.</param>
    /// <param name="moving">Moving tile, same dimensions.</param>
    /// <param name="width">Tile width (power of two).</param>
    /// <param name="height">Tile height (power of two).</param>
    /// <param name="applyWindow">
    /// Apply a 2D Hann window before transforming, to suppress edge-discontinuity spectral leakage on
    /// real (non-periodic) imagery. Default true. Pass false when the shift is a genuine circular shift
    /// (a window would break the circular-shift relationship and bias the peak).
    /// </param>
    public static Shift Estimate(ReadOnlySpan<float> reference, ReadOnlySpan<float> moving, int width, int height, bool applyWindow = true)
    {
        ValidateTile(width, height);
        if (reference.Length != width * height)
        {
            throw new ArgumentException($"reference must be {width * height} samples ({width}x{height}).");
        }

        // Equivalent to the precomputed-reference path below: build the reference spectrum, then correlate.
        // Callers that hold a FIXED reference across many frames (e.g. GlobalAligner) should instead call
        // PrepareReferenceSpectrum once and reuse it -- skipping this forward FFT per frame.
        var referenceSpectrum = PrepareReferenceSpectrum(reference, width, height, applyWindow);
        return Estimate(referenceSpectrum, moving, width, height, applyWindow);
    }

    /// <summary>
    /// Forward-transforms a FIXED reference tile (optionally Hann-windowed) into the spectrum
    /// <see cref="Estimate(ReadOnlySpan{Complex}, ReadOnlySpan{float}, int, int, bool)"/> consumes. Compute
    /// it ONCE for a reference that is correlated against many moving tiles -- the reference's forward FFT
    /// is identical every time, so recomputing it per frame is pure dead work. The returned array must not
    /// be mutated (the Estimate overload reads it without modifying it) and <paramref name="applyWindow"/>
    /// must match the value passed to that overload.
    /// </summary>
    public static Complex[] PrepareReferenceSpectrum(ReadOnlySpan<float> reference, int width, int height, bool applyWindow = true)
        => PrepareReferenceSpectrum(reference, width, height, applyWindow, whiten: true);

    /// <summary>
    /// <see cref="PrepareReferenceSpectrum(ReadOnlySpan{float}, int, int, bool)"/> for a correlation that is
    /// <paramref name="whiten"/>ed or not: without whitening the tile's (windowed) mean is taken out first, since a
    /// pedestal under the window would correlate with itself and pull every peak to zero. Must match the value the
    /// estimate is made with.
    /// </summary>
    public static Complex[] PrepareReferenceSpectrum(ReadOnlySpan<float> reference, int width, int height, bool applyWindow, bool whiten)
    {
        ValidateTile(width, height);
        if (reference.Length != width * height)
        {
            throw new ArgumentException($"reference must be {width * height} samples ({width}x{height}).");
        }

        var spectrum = new Complex[width * height];
        FillWindowed(reference, spectrum, width, height, applyWindow, removeMean: !whiten);
        Fft2D.Forward(spectrum, width, height);
        return spectrum;
    }

    /// <summary>
    /// Phase correlation against a precomputed reference spectrum (from <see cref="PrepareReferenceSpectrum"/>).
    /// Numerically identical to <see cref="Estimate(ReadOnlySpan{float}, ReadOnlySpan{float}, int, int, bool)"/>
    /// but skips the reference's forward FFT (~half the per-frame transform work for a fixed reference).
    /// <paramref name="applyWindow"/> must match the value used to build <paramref name="referenceSpectrum"/>.
    /// </summary>
    public static Shift Estimate(ReadOnlySpan<Complex> referenceSpectrum, ReadOnlySpan<float> moving, int width, int height, bool applyWindow = true)
        => Estimate(referenceSpectrum, moving, width, height, new Complex[width * height], applyWindow);

    /// <summary>
    /// <see cref="Estimate(ReadOnlySpan{Complex}, ReadOnlySpan{float}, int, int, bool)"/> into caller-owned
    /// <paramref name="scratch"/> of <c>width * height</c> samples, which is overwritten: the call then
    /// allocates nothing. For a caller that correlates tile after tile, such as the planetary aligners,
    /// where a new spectrum per call was 1 MB of large-object garbage per frame at a 256 px tile.
    /// Numerically identical to the allocating overload.
    /// </summary>
    public static Shift Estimate(ReadOnlySpan<Complex> referenceSpectrum, ReadOnlySpan<float> moving, int width, int height, Span<Complex> scratch, bool applyWindow = true)
        => Estimate(referenceSpectrum, moving, width, height, scratch, applyWindow, whiten: true);

    /// <summary>
    /// <see cref="Estimate(ReadOnlySpan{Complex}, ReadOnlySpan{float}, int, int, Span{Complex}, bool)"/>, whitened (phase
    /// correlation, every frequency weighted alike) or not (a plain cross-correlation, each frequency weighted by the power
    /// both tiles hold there). Whitening sharpens the peak where the detail stands above the noise everywhere; where the
    /// finest frequencies are noise, as on a single 8-bit planetary frame, it hands the peak to the noise
    /// (docs/plans/planetary-restoration.md, R4 and R5). Unwhitened, <see cref="Shift.PeakValue"/> is not bounded.
    /// <paramref name="referenceSpectrum"/> must come from <see cref="PrepareReferenceSpectrum(ReadOnlySpan{float}, int, int, bool, bool)"/>
    /// with the same <paramref name="whiten"/>.
    /// </summary>
    public static Shift Estimate(ReadOnlySpan<Complex> referenceSpectrum, ReadOnlySpan<float> moving, int width, int height, Span<Complex> scratch, bool applyWindow, bool whiten)
    {
        ValidateTile(width, height);
        var n = width * height;
        if (referenceSpectrum.Length != n || moving.Length != n)
        {
            throw new ArgumentException($"referenceSpectrum/moving must both be {n} samples ({width}x{height}).");
        }

        if (scratch.Length < n)
        {
            throw new ArgumentException($"scratch must hold at least {n} samples ({width}x{height}).", nameof(scratch));
        }

        // Forward-transform the moving tile into the scratch; the cross-power spectrum then overwrites it (the
        // cached reference spectrum is never mutated).
        var f2 = scratch[..n];
        FillWindowed(moving, f2, width, height, applyWindow, removeMean: !whiten);
        Fft2D.Forward(f2, width, height);

        // The cross-power spectrum R = F1 * conj(F2) (F1 = reference spectrum), normalised to unit magnitude when whitened.
        for (var i = 0; i < n; i++)
        {
            var c = referenceSpectrum[i] * Complex.Conjugate(f2[i]);
            if (whiten)
            {
                var mag = c.Magnitude;
                c = mag > 1e-12 ? c / mag : Complex.Zero;
            }
            f2[i] = c;
        }

        Fft2D.Inverse(f2, width, height);

        var shift = PeakShift(f2, width, height);
        if (whiten)
        {
            return shift;
        }

        // Unwhitened, the peak is a disk's autocorrelation, a rounded cone, and the parabola through its three samples locks
        // toward the whole pixel: along a planet's belts, where only the limb places the frame, it misplaced a noise-free
        // disk by 0.20 px RMS (RegistrationComparisonTests; docs/plans/planetary-restoration.md, R5 part 3). So the peak is
        // climbed on the correlation itself, exact between the pixels from its spectrum, which one forward transform of the
        // surface gives back. The surface peaks at minus the shift.
        Fft2D.Forward(f2, width, height);
        var (x, y) = ClimbPeak(f2, width, height, -shift.Dx, -shift.Dy);
        return shift with { Dx = -x, Dy = -y };
    }

    /// <summary>
    /// The maximum near (<paramref name="x"/>, <paramref name="y"/>) of <c>c(s) = Re sum over k of cross(k) exp(2 pi i k.s / n)</c>,
    /// the correlation surface <paramref name="cross"/> transforms back to, by Newton's method: its gradient and curvature are
    /// sums of the same terms, so each step is exact. A step that would leave the start's pixel, or a curvature that is not a
    /// maximum's, stops the climb where it is. A parabola through the peak's neighbours is biased by up to tenths of a pixel on
    /// a disk's correlation, whose peak is a rounded cone; this is how a correlation's peak is placed between the pixels.
    /// <para>The phase splits by axis, <c>exp(i (wx x + wy y)) = exp(i wx x) exp(i wy y)</c>, so a step takes one phasor a
    /// column and one a row and sums each row against the columns' first: three complex multiply-adds a frequency. A sine and
    /// cosine of every frequency made plain correlation cost 22 ms a frame more than phase correlation on a 512 px tile, 9 ms
    /// after (<c>RollingFoldBenchmarks</c>).</para>
    /// </summary>
    internal static (double X, double Y) ClimbPeak(ReadOnlySpan<Complex> cross, int width, int height, double x, double y)
    {
        Span<double> wxs = width <= 1024 ? stackalloc double[width] : new double[width];
        Span<Complex> ex = width <= 1024 ? stackalloc Complex[width] : new Complex[width];
        for (var kx = 0; kx < width; kx++)
        {
            wxs[kx] = 2 * Math.PI * (kx < width / 2 ? kx : kx - width) / width;
        }
        for (var iteration = 0; iteration < 5; iteration++)
        {
            for (var kx = 0; kx < width; kx++)
            {
                var (sin, cos) = Math.SinCos(wxs[kx] * x);
                ex[kx] = new Complex(cos, sin);
            }
            // With c e^{i theta} summed as S0 (plain), S1 (times wx) and S2 (times wx squared) over each row, then over the rows
            // with the row's own phasor and weights: the gradient is minus the imaginary part and the curvature minus the real.
            Complex t1 = Complex.Zero, t2 = Complex.Zero, ty = Complex.Zero, tyy = Complex.Zero, txy = Complex.Zero;
            for (var ky = 0; ky < height; ky++)
            {
                var wy = 2 * Math.PI * (ky < height / 2 ? ky : ky - height) / height;
                var row = cross.Slice(ky * width, width);
                double r0re = 0, r0im = 0, r1re = 0, r1im = 0, r2re = 0, r2im = 0;
                for (var kx = 0; kx < width; kx++)
                {
                    var c = row[kx];
                    var e = ex[kx];
                    var pre = (c.Real * e.Real) - (c.Imaginary * e.Imaginary);
                    var pim = (c.Real * e.Imaginary) + (c.Imaginary * e.Real);
                    var w = wxs[kx];
                    r0re += pre;
                    r0im += pim;
                    r1re += w * pre;
                    r1im += w * pim;
                    r2re += w * w * pre;
                    r2im += w * w * pim;
                }
                var (sinY, cosY) = Math.SinCos(wy * y);
                var ey = new Complex(cosY, sinY);
                var s0 = ey * new Complex(r0re, r0im);
                var s1 = ey * new Complex(r1re, r1im);
                t1 += s1;
                t2 += ey * new Complex(r2re, r2im);
                ty += wy * s0;
                tyy += wy * wy * s0;
                txy += wy * s1;
            }
            var (gx, gy) = (-t1.Imaginary, -ty.Imaginary);
            var (hxx, hyy, hxy) = (-t2.Real, -tyy.Real, -txy.Real);
            var det = (hxx * hyy) - (hxy * hxy);
            if (!(hxx < 0 && det > 0))
            {
                break;
            }
            var sx = ((hyy * gx) - (hxy * gy)) / det;
            var sy = ((hxx * gy) - (hxy * gx)) / det;
            if (Math.Abs(sx) > 1 || Math.Abs(sy) > 1)
            {
                break;
            }
            (x, y) = (x - sx, y - sy);
            if (Math.Abs(sx) < 1e-5 && Math.Abs(sy) < 1e-5)
            {
                break;
            }
        }
        return (x, y);
    }

    // Windows (or copies) a real tile into a complex buffer. The window multiply keeps the original
    // operation order -- w = wx*wy first, then src*w -- so the precomputed-reference path is bit-identical
    // to the single-call path (float multiply is not associative).
    private static void FillWindowed(ReadOnlySpan<float> src, Span<Complex> dst, int width, int height, bool applyWindow, bool removeMean = false)
    {
        if (removeMean)
        {
            FillWindowedLessMean(src, dst, width, height, applyWindow);
            return;
        }
        if (applyWindow)
        {
            // Separable Hann window, per axis, built once per length and shared: the values are a pure
            // function of the length, so a cached window is the very array a fresh one would be.
            var wx = HannWindows.GetOrAdd(width, MakeHannWindow);
            var wy = HannWindows.GetOrAdd(height, MakeHannWindow);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width) + x;
                    var w = wx[x] * wy[y];
                    dst[i] = new Complex(src[i] * w, 0);
                }
            }
        }
        else
        {
            for (var i = 0; i < src.Length; i++)
            {
                dst[i] = new Complex(src[i], 0);
            }
        }
    }

    // A tile less its mean under the window (the weighted mean, so a constant tile fills with zeros), windowed or not.
    private static void FillWindowedLessMean(ReadOnlySpan<float> src, Span<Complex> dst, int width, int height, bool applyWindow)
    {
        var wx = applyWindow ? HannWindows.GetOrAdd(width, MakeHannWindow) : null;
        var wy = applyWindow ? HannWindows.GetOrAdd(height, MakeHannWindow) : null;
        double sum = 0, weights = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var w = wx is null || wy is null ? 1.0 : wx[x] * wy[y];
                sum += src[(y * width) + x] * w;
                weights += w;
            }
        }
        var mean = weights > 0 ? sum / weights : 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var w = wx is null || wy is null ? 1.0 : wx[x] * wy[y];
                dst[(y * width) + x] = new Complex((src[(y * width) + x] - mean) * w, 0);
            }
        }
    }

    // Locates the correlation peak on the inverse-transformed surface and refines it to sub-pixel.
    private static Shift PeakShift(ReadOnlySpan<Complex> surface, int width, int height)
    {
        var n = width * height;

        // Integer peak over the real correlation surface.
        var peakIndex = 0;
        var peak = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var v = surface[i].Real;
            if (v > peak)
            {
                peak = v;
                peakIndex = i;
            }
        }

        var px = peakIndex % width;
        var py = peakIndex / width;

        // Sub-pixel parabolic refinement with circular neighbours.
        var subX = ParabolicOffset(
            surface[(py * width) + Wrap(px - 1, width)].Real,
            surface[(py * width) + px].Real,
            surface[(py * width) + Wrap(px + 1, width)].Real);
        var subY = ParabolicOffset(
            surface[(Wrap(py - 1, height) * width) + px].Real,
            surface[(py * width) + px].Real,
            surface[(Wrap(py + 1, height) * width) + px].Real);

        // The correlation peak sits at -shift (mod N); wrap each index into [-N/2, N/2] then negate.
        var signedX = px <= width / 2 ? px : px - width;
        var signedY = py <= height / 2 ? py : py - height;
        var dx = -(signedX + subX);
        var dy = -(signedY + subY);

        return new Shift(dx, dy, peak);
    }

    private static void ValidateTile(int width, int height)
    {
        if (!ComplexFft.IsPowerOfTwo(width) || !ComplexFft.IsPowerOfTwo(height))
        {
            throw new ArgumentException($"Phase correlation tile dimensions must each be a power of two, got {width}x{height}.");
        }
    }

    /// <summary>
    /// Hann windows by length, read-only once built. Written once per distinct tile size a process uses
    /// (two or three in practice) and read on every correlation, which is the shape a lock-free
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> is for. Two per call used to be allocated fresh.
    /// </summary>
    private static readonly ConcurrentDictionary<int, double[]> HannWindows = new();

    // A cached delegate, so GetOrAdd does not allocate one per call.
    private static readonly Func<int, double[]> MakeHannWindow = HannWindow;

    private static double[] HannWindow(int length)
    {
        var w = new double[length];
        if (length == 1)
        {
            w[0] = 1;
            return w;
        }

        for (var i = 0; i < length; i++)
        {
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (length - 1)));
        }

        return w;
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    /// <summary>Vertex offset in [-0.5, 0.5] of the parabola through (-1, vm), (0, v0), (+1, vp).</summary>
    private static double ParabolicOffset(double vm, double v0, double vp)
    {
        var denom = vm - (2.0 * v0) + vp;
        if (Math.Abs(denom) < 1e-12)
        {
            return 0;
        }

        var offset = 0.5 * (vm - vp) / denom;
        return Math.Clamp(offset, -1.0, 1.0);
    }
}
