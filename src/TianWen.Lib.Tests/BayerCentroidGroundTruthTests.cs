using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Does a star detected on a Bayer MOSAIC land where the star actually is?
/// </summary>
/// <remarks>
/// <para>Synthetic, because ground truth has to be exact. The companion
/// <see cref="BayerCentroidShiftProbe"/> measures the same thing on a real frame, but its reference
/// centroid is computed in a window centred on the DETECTED position, so truncation pulls the
/// reference toward the answer being tested and any real offset is under-reported. Here the truth is
/// a number chosen before the pixels exist.</para>
/// <para>The mono case is the CONTROL, and it is what makes this diagnostic rather than merely
/// descriptive: detection cannot run on a CFA mosaic, so <c>FindStarsAsync</c> debayers to mono
/// first. If mono is accurate and mosaic is not, the debayer is the cause -- nothing else differs
/// between the two paths.</para>
/// </remarks>
[Collection("Imaging")]
public class BayerCentroidGroundTruthTests(ITestOutputHelper output)
{
    private const int Size = 240;
    private const float Background = 1000f;
    private const float Sigma = 1.9f;
    private const float Amplitude = 24000f;

    // Deliberately off-centre and non-half-integer on both axes, so a half-pixel error cannot be
    // mistaken for rounding and the two axes are independently checked.
    private static readonly (float X, float Y)[] TruePositions =
    [
        (60.37f, 70.62f),
        (120.13f, 60.88f),
        (170.74f, 150.26f),
        (80.51f, 170.44f),
        (150.62f, 100.19f),
    ];

    private static ImageMeta Meta(SensorType sensorType) => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10),
        FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
        float.NaN, sensorType, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>
    /// Renders the star field. With <paramref name="applyCfa"/> each photosite is scaled by its
    /// RGGB channel gain, which is what a colour sensor does to a white star.
    /// </summary>
    private static Image Render(SensorType sensorType, bool applyCfa)
    {
        var data = new float[Size, Size];
        var twoSigmaSq = 2f * Sigma * Sigma;

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = Background;
                foreach (var (sx, sy) in TruePositions)
                {
                    var dx = x - sx;
                    var dy = y - sy;
                    v += Amplitude * MathF.Exp(-(dx * dx + dy * dy) / twoSigmaSq);
                }

                if (applyCfa)
                {
                    // RGGB with BayerOffset 0,0: R at (even col, even row), B at (odd, odd), G on the
                    // diagonal. Unequal gains are the whole point -- equal ones would make the mosaic
                    // indistinguishable from mono for centroid purposes.
                    var gain = ((y & 1), (x & 1)) switch
                    {
                        (0, 0) => 1.00f,   // R
                        (1, 1) => 0.55f,   // B
                        _ => 0.80f,        // G
                    };
                    v = Background + (v - Background) * gain;
                }

                data[y, x] = v;
            }
        }

        var max = Background + Amplitude;
        return new Image([data], BitDepth.Float32, max, Background, 0f, Meta(sensorType));
    }

    private static async Task<List<(float Dx, float Dy)>> MeasureAsync(Image image)
    {
        var stars = await image.FindStarsAsync(
            0, snrMin: 20f, maxStars: 100, cancellationToken: TestContext.Current.CancellationToken);

        var deltas = new List<(float, float)>();
        foreach (var (tx, ty) in TruePositions)
        {
            // Nearest detection to each planted star; the field is sparse so this cannot cross-match.
            var best = stars
                .Select(s => (s, d: MathF.Sqrt((s.XCentroid - tx) * (s.XCentroid - tx) + (s.YCentroid - ty) * (s.YCentroid - ty))))
                .OrderBy(t => t.d)
                .FirstOrDefault();

            // ImagedStar is a value type, so an empty match shows as d == 0 from default(); the
            // distance gate below is what rejects it, since a planted star is never at (0, 0).
            if (best.d > 0f && best.d < 4f)
            {
                deltas.Add((best.s.XCentroid - tx, best.s.YCentroid - ty));
            }
        }
        return deltas;
    }

    [Fact]
    public async Task OnAMonochromeFrameTheCentroidIsWhereTheStarIs()
    {
        var deltas = await MeasureAsync(Render(SensorType.Monochrome, applyCfa: false));

        deltas.Count.ShouldBe(TruePositions.Length);
        var mx = deltas.Average(d => d.Dx);
        var my = deltas.Average(d => d.Dy);
        output.WriteLine($"mono   mean dx={mx:F4} dy={my:F4}");
        foreach (var d in deltas)
        {
            output.WriteLine($"  dx={d.Dx:F4} dy={d.Dy:F4}");
        }

        MathF.Abs(mx).ShouldBeLessThan(0.15f);
        MathF.Abs(my).ShouldBeLessThan(0.15f);
    }

    [Fact]
    public async Task OnABayerMosaicTheCentroidIsWhereTheStarIs()
    {
        var deltas = await MeasureAsync(Render(SensorType.RGGB, applyCfa: true));

        deltas.Count.ShouldBe(TruePositions.Length);
        var mx = deltas.Average(d => d.Dx);
        var my = deltas.Average(d => d.Dy);
        output.WriteLine($"mosaic mean dx={mx:F4} dy={my:F4}");
        foreach (var d in deltas)
        {
            output.WriteLine($"  dx={d.Dx:F4} dy={d.Dy:F4}");
        }

        // Same bar as mono. Detection debayers a mosaic to mono internally, and the caller consumes
        // the result as mosaic coordinates -- the viewer's star overlay draws it straight onto the
        // displayed mosaic -- so the two paths owe the same answer.
        MathF.Abs(mx).ShouldBeLessThan(0.15f);
        MathF.Abs(my).ShouldBeLessThan(0.15f);
    }
}
