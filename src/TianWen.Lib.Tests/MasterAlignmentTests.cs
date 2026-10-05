using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="MasterAlignment"/> and <see cref="ContinuumSubtractor"/>: masters of one target on one grid, and a line
/// master's continuum taken out against a broadband one (#874).
/// </summary>
[Collection("Imaging")]
public class MasterAlignmentTests(ITestOutputHelper output)
{
    private const int Size = 512;

    /// <summary>
    /// Another night of the same sky, turned 3 degrees and shifted, with noise of its own: the fit recovers the turn and
    /// the shift, and the master's stars land on the reference's, its uncovered corners absent.
    /// </summary>
    [Fact]
    public async Task AMasterFromAnotherNightLandsOnTheReferencesGrid()
    {
        var ct = TestContext.Current.CancellationToken;
        var reference = Field(noiseSeed: 1);
        // The other night shows the sky at T^-1 of each of its pixels, so the fit taking it onto the reference is T^-1.
        var turn = Matrix3x2.CreateRotation(3f * MathF.PI / 180f, new Vector2(Size / 2f, Size / 2f)) * Matrix3x2.CreateTranslation(14.5f, -9.25f);
        var other = await Field(noiseSeed: 2).WarpToReferenceGridAsync(turn, Size, Size, ct);
        Matrix3x2.Invert(turn, out var expected).ShouldBeTrue();

        var (aligned, reason) = await MasterAlignment.AlignAsync(reference, other, cancellationToken: ct);

        aligned.ShouldNotBeNull(reason);
        output.WriteLine($"stars {aligned.Stars}, budget {aligned.QuadStars}, rms {aligned.RmsPx:F3} px, scale {aligned.Scale:F5}, " +
                         $"rotation {aligned.RotationDeg:F3} deg, shift ({aligned.ToReference.M31:F2}, {aligned.ToReference.M32:F2}) " +
                         $"against ({expected.M31:F2}, {expected.M32:F2})");
        aligned.RotationDeg.ShouldBe(-3.0, 0.05);
        aligned.Scale.ShouldBe(1.0, 0.002);
        aligned.ToReference.M31.ShouldBe(expected.M31, 0.2);
        aligned.ToReference.M32.ShouldBe(expected.M32, 0.2);

        var referenceStars = await MasterAlignment.FindStarsAsync(reference, ct);
        var alignedStars = await MasterAlignment.FindStarsAsync(aligned.Image, ct);
        var offsets = NearestOffsets(referenceStars, alignedStars, 2f);
        output.WriteLine($"{offsets.Count} stars matched, median offset {Median(offsets):F3} px");
        offsets.Count.ShouldBeGreaterThan(30);
        Median(offsets).ShouldBeLessThan(0.15);
        // The turned night does not reach the reference's corners.
        float.IsNaN(aligned.Image[0, 0, Size - 1]).ShouldBeTrue();
    }

    /// <summary>
    /// A line master is the line plus a slice of the continuum. On a field whose stars and reflection nebula are in both
    /// and whose emission is in the line alone, the flattest residual finds the slice exactly and the stars agree with it;
    /// the subtraction takes the stars out and leaves the emission.
    /// </summary>
    [Fact]
    public async Task TheContinuumComesOutAndTheEmissionStays()
    {
        var ct = TestContext.Current.CancellationToken;
        const double k = 0.08;
        var rng = new Random(9);
        var stars = new float[Size, Size];
        for (var s = 0; s < 300; s++)
        {
            AddStar(stars, rng.NextDouble() * Size, rng.NextDouble() * Size, 2000 + (rng.NextDouble() * 60000));
        }
        var line = new float[Size, Size];
        var continuum = new float[Size, Size];
        var emission = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var reflection = 400 * Math.Exp(-((Sq(x - 150) + Sq(y - 360)) / (2 * Sq(60))));
                var glow = 120 * Math.Exp(-((Sq(x - 330) + Sq(y - 170)) / (2 * Sq(80))));
                emission[y, x] = (float)glow;
                continuum[y, x] = (float)(200 + stars[y, x] + reflection + (3 * Gaussian(rng)));
                line[y, x] = (float)(50 + (k * (stars[y, x] + reflection)) + glow + (3 * Gaussian(rng)));
            }
        }
        var lineImage = new Image([line], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());
        var continuumImage = new Image([continuum], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());

        var flattest = ContinuumSubtractor.FlattestResidualScale(lineImage, continuumImage);
        var lineStars = await MasterAlignment.FindStarsAsync(lineImage, ct);
        var continuumStars = await MasterAlignment.FindStarsAsync(continuumImage, ct);
        var (photometric, matched, spread) = ContinuumSubtractor.PhotometricScale(lineStars, continuumStars);
        output.WriteLine($"flattest residual k {flattest.K:F5} (residual AAD {flattest.ResidualAad:F2} against the line's {flattest.LineAad:F2}); " +
                         $"photometric k {photometric:F5} over {matched} stars, spread {spread:P1}");
        flattest.K.ShouldBe(k, k * 0.03);
        photometric.ShouldBe(k, k * 0.05);

        var pure = ContinuumSubtractor.Subtract(lineImage, continuumImage, flattest.K);
        double starResidual = 0, starLine = 0, emissionError = 0;
        var starPixels = 0;
        var emissionPixels = 0;
        var background = StatisticsHelper.MedianFast(pure.GetChannelSpan(0).ToArray().AsSpan());
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (stars[y, x] > 500)
                {
                    starResidual += Math.Abs(pure[0, y, x] - background - emission[y, x]);
                    starLine += k * stars[y, x];
                    starPixels++;
                }
                else if (emission[y, x] > 60)
                {
                    emissionError += Math.Abs(pure[0, y, x] - background - emission[y, x]);
                    emissionPixels++;
                }
            }
        }
        output.WriteLine($"on {starPixels} star pixels the residual is {starResidual / starLine:P1} of the stars the line carried; " +
                         $"the emission is kept to {emissionError / emissionPixels:F2} over {emissionPixels} pixels");
        (starResidual / starLine).ShouldBeLessThan(0.1);
        (emissionError / emissionPixels).ShouldBeLessThan(10.0);
    }

    /// <summary>The weighted median is the brute-force one: the smallest value whose cumulative weight reaches half.</summary>
    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 100)]
    [InlineData(3, 10_001)]
    public void TheWeightedMedianIsTheBruteForceOne(int seed, int n)
    {
        var rng = new Random(seed);
        var values = new float[n];
        var weights = new float[n];
        for (var i = 0; i < n; i++)
        {
            // Ties on purpose: a value drawn from a small set.
            values[i] = rng.Next(0, Math.Max(3, n / 4));
            weights[i] = (float)rng.NextDouble();
        }
        var pairs = values.Zip(weights).OrderBy(p => p.First).ToArray();
        var half = weights.Sum(w => (double)w) / 2;
        double cumulative = 0;
        var expected = float.NaN;
        foreach (var (v, w) in pairs)
        {
            cumulative += w;
            if (cumulative >= half)
            {
                expected = v;
                break;
            }
        }
        StatisticsHelper.WeightedMedian(values, weights).ShouldBe(expected);
    }

    private static Image Field(int noiseSeed)
        => new([SyntheticStarFieldRenderer.Render(Size, Size, defocusSteps: 0, exposureSeconds: 10, skyBackground: 100, readNoise: 5,
            starCount: 250, seed: 5, noiseSeed: noiseSeed)], BitDepth.Float32, 65535f, 0f, 0f, new ImageMeta());

    private static void AddStar(float[,] plane, double cx, double cy, double flux)
    {
        const double sigma = 1.5;
        var norm = flux / (2 * Math.PI * sigma * sigma);
        for (var y = Math.Max(0, (int)cy - 8); y < Math.Min(Size, (int)cy + 9); y++)
        {
            for (var x = Math.Max(0, (int)cx - 8); x < Math.Min(Size, (int)cx + 9); x++)
            {
                plane[y, x] += (float)(norm * Math.Exp(-(Sq(x - cx) + Sq(y - cy)) / (2 * sigma * sigma)));
            }
        }
    }

    private static List<double> NearestOffsets(StarList reference, StarList moved, float radius)
    {
        var offsets = new List<double>();
        var candidates = moved.ToList();
        foreach (var star in reference)
        {
            var best = double.MaxValue;
            foreach (var other in candidates)
            {
                var d = Math.Sqrt(Sq(other.XCentroid - star.XCentroid) + Sq(other.YCentroid - star.YCentroid));
                best = Math.Min(best, d);
            }
            if (best <= radius)
            {
                offsets.Add(best);
            }
        }
        return offsets;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static double Sq(double v) => v * v;

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
