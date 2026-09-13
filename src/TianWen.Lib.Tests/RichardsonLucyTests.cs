using System;
using Shouldly;
using TianWen.Lib.Imaging.Deconvolution;
using TianWen.Lib.Imaging.Degradation;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Validates <see cref="RichardsonLucy"/> against answers known in advance, because it is about to be
/// used as the ORACLE that P2's trained deconvolver is scored against
/// (<c>docs/plans/deconvolver-training.md</c>, H1). A ceiling measured with a broken instrument is
/// worse than no ceiling: every arm would be judged against it and the error would never surface.
/// </summary>
/// <remarks>
/// The star here is a Moffat evaluated in closed form rather than a detected one, and the width is
/// read off the profile's own half-maximum crossing rather than from the star detector. Both are
/// deliberate: a unit test of the deconvolution must not be able to fail because the detector changed.
/// </remarks>
public class RichardsonLucyTests(ITestOutputHelper output)
{
    private const int Size = 96;
    private const float Background = 0.05f;
    private const float Peak = 1.0f;

    /// <summary>A centred Moffat star of the given FWHM on a flat background, row-major.</summary>
    private static float[] Star(double fwhm, double beta = 4.0)
    {
        var plane = new float[Size * Size];
        var alpha = fwhm / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / beta) - 1.0));
        var c = (Size - 1) / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = x - c;
                var dy = y - c;
                var r2 = ((dx * dx) + (dy * dy)) / (alpha * alpha);
                plane[(y * Size) + x] = Background + (float)(Peak * Math.Pow(1.0 + r2, -beta));
            }
        }

        return plane;
    }

    /// <summary>
    /// The profile's FWHM along +X, measured off the plane itself: walk out from the centre to the
    /// half-maximum crossing above background and interpolate. Independent of the star detector.
    /// </summary>
    private static double ProfileFwhm(float[] plane)
    {
        var c = (Size - 1) / 2;
        var peak = plane[(c * Size) + c] - Background;
        var half = peak / 2.0;
        for (var r = 1; r < Size / 2; r++)
        {
            var here = plane[(c * Size) + c + r] - Background;
            if (here <= half)
            {
                var prev = plane[(c * Size) + c + r - 1] - Background;
                var t = (prev - half) / (prev - here);
                return 2.0 * (r - 1 + t);
            }
        }

        return Size;
    }

    private static double Flux(float[] plane, int margin)
    {
        var sum = 0.0;
        for (var y = margin; y < Size - margin; y++)
        {
            for (var x = margin; x < Size - margin; x++)
            {
                sum += plane[(y * Size) + x];
            }
        }

        return sum;
    }

    /// <summary>
    /// The claim <see cref="PsfKernel.Mirrored"/>'s remarks make, pinned. If this ever fails, the
    /// kernel family has grown an asymmetric member and <see cref="RichardsonLucy"/>'s correction pass
    /// is the reason to care: it convolves with the adjoint, which stops being a no-op exactly here.
    /// </summary>
    [Theory]
    [InlineData(3.0, 4.0, 1.0, 0.0)]
    [InlineData(2.5, 2.0, 1.6, 37.0)]
    [InlineData(4.0, 8.0, 1.25, 115.0)]
    public void TheKernelFamilyIsCentrallySymmetricSoTheAdjointIsTheKernel(double fwhm, double beta, double elongation, double pa)
    {
        var k = PsfKernel.Moffat(fwhm, beta, elongation, pa);
        var mirrored = k.Mirrored();

        mirrored.Radius.ShouldBe(k.Radius);
        for (var i = 0; i < k.Weights.Length; i++)
        {
            mirrored.Weights[i].ShouldBe(k.Weights[i], 1e-7f);
        }
    }

    /// <summary>
    /// The property the oracle rests on: handed the exact kernel, the iteration takes a blurred star
    /// back toward the width it had before the blur.
    /// </summary>
    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(2.0, 3.0)]
    [InlineData(2.5, 1.5)]
    public void TheExactKernelRecoversMostOfAKnownBlur(double truthFwhm, double blurFwhm)
    {
        var truth = Star(truthFwhm);
        var psf = PsfKernel.Moffat(blurFwhm, 4.0);
        var blurred = psf.Convolve(truth, Size, Size);

        var result = RichardsonLucy.Deconvolve(blurred, Size, Size, psf, iterations: 40);

        var wTruth = ProfileFwhm(truth);
        var wBlurred = ProfileFwhm(blurred);
        var wRecovered = ProfileFwhm(result.Estimate);
        var recovered = (wBlurred - wRecovered) / (wBlurred - wTruth);
        output.WriteLine($"truth {wTruth:F2} px -> blurred {wBlurred:F2} -> recovered {wRecovered:F2}  "
            + $"({recovered:P0} of the injected blur, clamped {result.ClampedFraction:P2})");

        wBlurred.ShouldBeGreaterThan(wTruth);
        // Noise-free with the exact kernel is the easiest case there is, so most of it must come back.
        recovered.ShouldBeGreaterThan(0.5);
        // And it must not overshoot into a star NARROWER than the one that was there: that direction is
        // fabrication, and it is the failure mode the whole oracle exists to bound.
        wRecovered.ShouldBeGreaterThan(wTruth * 0.85);
        result.ClampedFraction.ShouldBe(0.0);
    }

    /// <summary>
    /// Flux in, flux out. The kernel sums to one and the iteration is a rescaling of the estimate, so
    /// an interior measurement must not gain or lose light; a violation here means the convolution's
    /// normalisation or the update's denominator guard is wrong, and it would read as the oracle
    /// inventing or destroying signal.
    /// </summary>
    [Fact]
    public void TheIterationConservesInteriorFlux()
    {
        var truth = Star(2.0);
        var psf = PsfKernel.Moffat(3.0, 4.0);
        var blurred = psf.Convolve(truth, Size, Size);

        var result = RichardsonLucy.Deconvolve(blurred, Size, Size, psf, iterations: 30);

        var margin = psf.Radius + 2;
        var before = Flux(blurred, margin);
        var after = Flux(result.Estimate, margin);
        output.WriteLine($"interior flux {before:F3} -> {after:F3} ({(after / before) - 1.0:P3})");
        (after / before).ShouldBe(1.0, 0.02);
    }

    /// <summary>
    /// A kernel that barely blurs must barely change the image. This is the degenerate end of the
    /// sweep the oracle table walks, and it is where an iteration that amplifies noise announces
    /// itself first.
    /// </summary>
    [Fact]
    public void ANearDeltaKernelIsNearlyANoOp()
    {
        var truth = Star(2.0);
        var psf = PsfKernel.Moffat(0.35, 4.0);
        var blurred = psf.Convolve(truth, Size, Size);

        var result = RichardsonLucy.Deconvolve(blurred, Size, Size, psf, iterations: 20);

        var worst = 0.0;
        var margin = psf.Radius + 2;
        for (var y = margin; y < Size - margin; y++)
        {
            for (var x = margin; x < Size - margin; x++)
            {
                worst = Math.Max(worst, Math.Abs(result.Estimate[(y * Size) + x] - blurred[(y * Size) + x]));
            }
        }

        output.WriteLine($"worst interior deviation from the input: {worst:E3} (peak is {Peak})");
        worst.ShouldBeLessThan(0.02);
    }

    /// <summary>
    /// A negative sample flips the sign of its own multiplicative correction, so the input contract is
    /// non-negative and a violation has to be REPORTED rather than absorbed. A sky-subtracted master is
    /// the realistic way in.
    /// </summary>
    [Fact]
    public void NegativeInputIsClampedAndTheCallerIsTold()
    {
        var truth = Star(2.0);
        for (var i = 0; i < 40; i++)
        {
            truth[i] = -0.01f;
        }

        truth[500] = float.NaN;

        var psf = PsfKernel.Moffat(2.5, 4.0);
        var result = RichardsonLucy.Deconvolve(truth, Size, Size, psf, iterations: 5);

        result.ClampedFraction.ShouldBe(41.0 / (Size * Size), 1e-6);
        foreach (var v in result.Estimate)
        {
            float.IsFinite(v).ShouldBeTrue();
            v.ShouldBeGreaterThanOrEqualTo(0f);
        }
    }
}
