using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Measures whether star centroids detected on a BAYER MOSAIC land where the star actually is.
/// </summary>
/// <remarks>
/// <para>Detection cannot run on a CFA mosaic directly, so <c>FindStarsAsync</c> debayers to mono
/// first and detects on that. The question this answers is whether the mono image shares the
/// mosaic's pixel grid, because the returned centroids are consumed as if it does -- the viewer's
/// star overlay maps them straight onto the displayed mosaic.</para>
/// <para>The reference centroid is computed on the MOSAIC, summing every photosite in the window.
/// That is unbiased for POSITION regardless of the CFA: the four channels have different
/// sensitivities, but summing all of them weights each photosite by its own flux, and a symmetric
/// star lands on its true centre. So a systematic difference is a grid mismatch, not a colour
/// effect.</para>
/// </remarks>
[Collection("Imaging")]
public class BayerCentroidShiftProbe(ITestOutputHelper output)
{
    private const string BayerFixture = "RGGB_frame_bx0_by0_top_down";

    /// <summary>Half-width of the window the reference centroid is computed over.</summary>
    private const int Half = 6;

    [Fact]
    public async Task DetectedCentroidsOnAMosaicAgreeWithTheMosaicItself()
    {
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(
            BayerFixture, cancellationToken: TestContext.Current.CancellationToken);

        image.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        image.ChannelCount.ShouldBe(1);

        var stars = await image.FindStarsAsync(
            0, snrMin: 30f, maxStars: 5000, cancellationToken: TestContext.Current.CancellationToken);
        stars.ShouldNotBeEmpty();

        var (_, width, height) = image.Shape;
        var mosaic = image.GetChannelSpan(0);

        var dxs = new List<double>();
        var dys = new List<double>();

        foreach (var star in stars)
        {
            // Well inside the frame, and tight enough that neighbours in a dense field stay out.
            var xi = (int)MathF.Round(star.XCentroid);
            var yi = (int)MathF.Round(star.YCentroid);
            if (xi - Half < 0 || yi - Half < 0 || xi + Half >= width || yi + Half >= height)
            {
                continue;
            }

            // Local background from the window's border ring, so the flux weights are above-sky only:
            // a pedestal biases a flux-weighted centroid toward the window centre.
            double border = 0;
            var borderCount = 0;
            for (var y = yi - Half; y <= yi + Half; y++)
            {
                for (var x = xi - Half; x <= xi + Half; x++)
                {
                    if (y == yi - Half || y == yi + Half || x == xi - Half || x == xi + Half)
                    {
                        border += mosaic[y * width + x];
                        borderCount++;
                    }
                }
            }
            var sky = border / borderCount;

            double sum = 0, sx = 0, sy = 0;
            for (var y = yi - Half; y <= yi + Half; y++)
            {
                for (var x = xi - Half; x <= xi + Half; x++)
                {
                    var v = mosaic[y * width + x] - sky;
                    if (v <= 0)
                    {
                        continue;
                    }
                    sum += v;
                    sx += v * x;
                    sy += v * y;
                }
            }

            if (sum <= 0)
            {
                continue;
            }

            dxs.Add(sx / sum - star.XCentroid);
            dys.Add(sy / sum - star.YCentroid);
        }

        dxs.Count.ShouldBeGreaterThan(200);

        // The MEDIAN, not the mean: a dense field puts a neighbour inside some windows, which drags
        // that sample hard. A systematic grid shift moves every sample the same way, so the median
        // reports it and the outliers cannot hide it.
        var mx = Median(dxs);
        var my = Median(dys);

        output.WriteLine($"samples={dxs.Count}");
        output.WriteLine($"median dx={mx:F4} px, dy={my:F4} px  (mosaic reference minus detected)");
        output.WriteLine($"mean   dx={dxs.Average():F4} px, dy={dys.Average():F4} px");

        // Before the BilinearMonoGridOffset correction this read +0.2250 / +0.2306; it now reads
        // -0.0012 / -0.0097. Note it UNDER-reported the real error, which was exactly -0.5 on both
        // axes: the window is centred on the DETECTED position, so truncating at v <= 0 pulls the
        // reference toward the answer under test. That dilution is why the synthetic
        // BayerCentroidGroundTruthTests exists and is the tighter of the two.
        MathF.Abs((float)mx).ShouldBeLessThan(0.1f);
        MathF.Abs((float)my).ShouldBeLessThan(0.1f);
    }

    private static double Median(List<double> xs)
    {
        var a = xs.ToArray();
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
    }
}
