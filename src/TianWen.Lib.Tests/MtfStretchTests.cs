using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for the input-normalisation MTF stretch helpers
/// (<see cref="Image.MidtonesBalanceFor"/>, <see cref="Image.MtfStretch"/>,
/// <see cref="Image.MtfUnstretch"/>). These wrap the existing
/// <see cref="Image.MidtonesTransferFunction"/> primitive into a per-channel
/// pedestal-subtract + adaptive-MTF round-trip suitable for normalising data
/// to an ML model's training distribution (e.g. AI4 NAFNet at
/// target_median = 0.25).
/// </summary>
[Collection("Imaging")]
public class MtfStretchTests
{
    private const float Eps = 1e-5f;

    // --- MidtonesBalanceFor (scalar parameter translator) -------------------

    [Theory]
    // Reference value: orig_med=0.5, t=0.25 -> beta = 0.5 * (0.25-1) / (0.25 * (2*0.5-1) - 0.5)
    //                                              = 0.5 * -0.75 / (0 - 0.5) = -0.375 / -0.5 = 0.75
    [InlineData(0.5, 0.25, 0.75)]
    // When orig_med == target_median, beta must be 0.5 (identity MTF, no-op).
    [InlineData(0.25, 0.25, 0.5)]
    [InlineData(0.7, 0.7, 0.5)]
    // Spot-check a few off-diagonal pairs.
    [InlineData(0.25, 0.5, 0.25)]
    [InlineData(0.1, 0.5, 0.1)]
    public void MidtonesBalanceFor_KnownPairs(double origMed, double targetMed, double expectedBeta)
    {
        var beta = Image.MidtonesBalanceFor(origMed, targetMed);
        beta.ShouldBe(expectedBeta, 1e-9);
    }

    [Theory]
    [InlineData(0.05, 0.25)]
    [InlineData(0.10, 0.25)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.50, 0.25)]
    [InlineData(0.75, 0.25)]
    [InlineData(0.95, 0.25)]
    [InlineData(0.30, 0.50)]
    [InlineData(0.40, 0.10)]
    public void MidtonesBalanceFor_LandsOrigMedAtTargetMed(double origMed, double targetMed)
    {
        // Closed-form contract: feeding orig_med through MTF(beta, .) must land
        // exactly at target_med. This is the whole reason the helper exists.
        var beta = Image.MidtonesBalanceFor(origMed, targetMed);
        var result = Image.MidtonesTransferFunction(beta, origMed);
        result.ShouldBe(targetMed, 1e-9);
    }

    [Theory]
    [InlineData(0.30, 0.25)]
    [InlineData(0.40, 0.25)]
    [InlineData(0.05, 0.25)]
    public void InverseMtfIdentity_OneMinusBetaInverts(double origMed, double targetMed)
    {
        // The algebraic identity MTF^-1(beta, y) == MTF(1 - beta, y) is the
        // basis of MtfUnstretch -- regress on a synthetic sample to confirm.
        var beta = Image.MidtonesBalanceFor(origMed, targetMed);
        for (var i = 1; i < 100; i++)
        {
            var x = i / 100.0;
            var y = Image.MidtonesTransferFunction(beta, x);
            var roundTrip = Image.MidtonesTransferFunction(1.0 - beta, y);
            roundTrip.ShouldBe(x, 1e-9, $"x={x}, y={y}");
        }
    }

    // --- MtfStretch / MtfUnstretch (per-channel image wrap) -----------------

    private static Image BuildMonoImage(float[,] data)
        => Image.FromChannel(data, maxValue: 1.0f, minValue: 0f);

    private static Image BuildRgbImage(float[,] r, float[,] g, float[,] b)
        => new Image([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f,
            new ImageMeta { SensorType = SensorType.Color });

    [Fact]
    public void MtfStretch_MonoRoundTripWithinEps()
    {
        // Random-ish but deterministic mono plane spanning a non-trivial range.
        const int w = 32, h = 24;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                plane[y, x] = 0.1f + 0.8f * ((x * 13 + y * 7) % 100) / 100f;

        var src = BuildMonoImage(plane);
        var stretched = src.MtfStretch(0.25, out var origMin, out var balances);
        var roundTripped = stretched.MtfUnstretch(origMin, balances);

        // Round-trip equality within float epsilon, per pixel.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                roundTripped[0, y, x].ShouldBe(src[0, y, x], Eps, $"@({x},{y})");
            }
        }
    }

    [Fact]
    public void MtfStretch_RgbRoundTripPerChannelIndependent()
    {
        // Each channel has its own min + median, so balances differ per channel
        // and we must preserve the per-channel context through the round trip.
        const int w = 16, h = 16;
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                r[y, x] = 0.05f + 0.30f * ((x * 11 + y * 3) % 50) / 50f;  // low-key
                g[y, x] = 0.20f + 0.50f * ((x * 5 + y * 9) % 70) / 70f;   // mid-key
                b[y, x] = 0.40f + 0.55f * ((x * 17 + y * 13) % 60) / 60f; // high-key
            }

        var src = BuildRgbImage(r, g, b);
        var stretched = src.MtfStretch(0.25, out var origMin, out var balances);

        // Each channel's β must be distinct (because each channel has a
        // different shifted median); the test ensures we didn't accidentally
        // share state across channels.
        origMin.Length.ShouldBe(3);
        balances.Length.ShouldBe(3);
        balances[0].ShouldNotBe(balances[1]);
        balances[1].ShouldNotBe(balances[2]);

        var roundTripped = stretched.MtfUnstretch(origMin, balances);
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    roundTripped[c, y, x].ShouldBe(src[c, y, x], Eps, $"c={c} @({x},{y})");
                }
            }
        }
    }

    [Fact]
    public void MtfStretch_StretchedMedianLandsAtTargetMedian()
    {
        // The forward stretch's contract: post-stretch median of each channel
        // must equal target_median (within numerical tolerance) -- that's
        // exactly what MidtonesBalanceFor was derived to deliver.
        const int w = 40, h = 40;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                plane[y, x] = 0.02f + 0.4f * ((x + y) % 50) / 50f;

        var src = BuildMonoImage(plane);
        var stretched = src.MtfStretch(0.25, out _, out _);

        // Recompute median of the stretched channel.
        var samples = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                samples[y * w + x] = stretched[0, y, x];
        System.Array.Sort(samples);
        var med = samples[samples.Length / 2];
        med.ShouldBe(0.25f, 5e-3f);  // within 0.5% of target
    }

    [Fact]
    public void MtfStretch_NaNPreserved()
    {
        const int w = 8, h = 8;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                plane[y, x] = 0.1f + 0.6f * ((x + y) % 10) / 10f;
        plane[3, 5] = float.NaN;
        plane[6, 2] = float.NaN;

        var src = BuildMonoImage(plane);
        var stretched = src.MtfStretch(0.25, out var origMin, out var balances);

        // NaN must propagate through both stretch + unstretch.
        float.IsNaN(stretched[0, 3, 5]).ShouldBeTrue();
        float.IsNaN(stretched[0, 6, 2]).ShouldBeTrue();

        var roundTripped = stretched.MtfUnstretch(origMin, balances);
        float.IsNaN(roundTripped[0, 3, 5]).ShouldBeTrue();
        float.IsNaN(roundTripped[0, 6, 2]).ShouldBeTrue();

        // Non-NaN pixel round-trips cleanly.
        roundTripped[0, 0, 0].ShouldBe(src[0, 0, 0], Eps);
    }

    [Fact]
    public void MtfStretch_FlatPlaneIsNoOp()
    {
        // Constant-valued plane: shifted median is 0, β falls back to 0.5
        // (identity MTF). Unstretch must reproduce the input exactly modulo
        // the per-channel min subtract -- which is the entire plane.
        const int w = 4, h = 4;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                plane[y, x] = 0.3f;

        var src = BuildMonoImage(plane);
        var stretched = src.MtfStretch(0.25, out var origMin, out var balances);
        balances[0].ShouldBe(0.5);
        origMin[0].ShouldBe(0.3f);

        var roundTripped = stretched.MtfUnstretch(origMin, balances);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                roundTripped[0, y, x].ShouldBe(0.3f, Eps);
    }

    [Fact]
    public void MtfUnstretch_ChannelCountMismatchThrows()
    {
        var src = BuildMonoImage(new float[4, 4]);
        Should.Throw<System.ArgumentException>(
            () => src.MtfUnstretch(new float[2], new double[1]));
    }
}
