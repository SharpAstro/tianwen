using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #1991: AHD interpolates each channel from photosites at different positions, so on a mosaic whose
/// channels have very different gains (a colour-cast sky, 3:1 red to green) an undersampled star or a
/// 2 px point source gets its red and blue peaks displaced and its colour ratio inflated. AHD equalises
/// the CFA gains before interpolating and undoes them after, so the ratios and centroids survive.
/// </summary>
[Collection("Imaging")]
public class DebayerAhdGainEqualisationTests
{
    private const int Size = 96;
    private const float GainR = 3.0f, GainB = 1.3f;
    private const float Sky = 100f;
    private const float Amplitude = 2000f;

    // Two compact sources at sub-pixel positions. A FWHM near 2 px is the undersampled regime
    // that matters; below about 2.4 the sparse red and blue lattices (pitch 2) alias on their
    // own, which shaves the flux ratio by several percent whatever the demosaic does.
    private static readonly (double X, double Y, double Fwhm)[] Sources =
    [
        (47.3, 47.6, 2.6),
        (20.4, 70.6, 2.4),
    ];

    [Fact]
    public async Task FullFrame_KeepsColourRatiosAndCentroids()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = BuildMosaic();
        var debayered = await image.DebayerAsync(DebayerAlgorithm.AHD, normalizeToUnit: false, cancellationToken: ct);

        AssertSources(debayered);
    }

    [Fact]
    public async Task RegionStrips_KeepColourRatiosAndCentroids()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = BuildMosaic();
        var dest = new Channel[]
        {
            Channel.Create(Size, Size),
            Channel.Create(Size, Size),
            Channel.Create(Size, Size),
        };

        Image? last = null;
        // Overlapping strips: each owns 32 rows and re-debayers 8 rows of its neighbour.
        foreach (var (y, h) in new[] { (0, 40), (32, 40), (64, 32) })
        {
            last = await image.DebayerRegionIntoAsync(dest, DebayerAlgorithm.AHD, new PixelRect(0, y, Size, h), ct);
        }

        AssertSources(last!);
    }

    private static void AssertSources(Image result)
    {
        var skyOut = new[] { Sky * GainR, Sky, Sky * GainB };
        var report = "";
        var ok = true;

        foreach (var (sx, sy, _) in Sources)
        {
            var flux = new double[3];
            var cx = new double[3];
            var cy = new double[3];
            var x0 = (int)Math.Round(sx) - 6;
            var y0 = (int)Math.Round(sy) - 6;
            for (var c = 0; c < 3; c++)
            {
                for (var y = y0; y <= y0 + 12; y++)
                {
                    for (var x = x0; x <= x0 + 12; x++)
                    {
                        var v = result[c, y, x] - skyOut[c];
                        flux[c] += v;
                        cx[c] += v * x;
                        cy[c] += v * y;
                    }
                }
                cx[c] /= flux[c];
                cy[c] /= flux[c];
            }

            var rg = flux[0] / flux[1];
            var bg = flux[2] / flux[1];
            var worstShift = Math.Max(
                Math.Max(Math.Abs(cx[0] - cx[1]), Math.Abs(cy[0] - cy[1])),
                Math.Max(Math.Abs(cx[2] - cx[1]), Math.Abs(cy[2] - cy[1])));
            report += FormattableString.Invariant($"({sx}, {sy}): R/G {rg:F4}, B/G {bg:F4}, worst centroid shift {worstShift:F3} px; ");
            ok &= Math.Abs(rg - GainR) <= GainR * 0.03 && Math.Abs(bg - GainB) <= GainB * 0.03 && worstShift < 0.15;
        }

        ok.ShouldBeTrue($"expected R/G {GainR}, B/G {GainB} within 3 percent and centroids within 0.15 px; got {report}");
    }

    private static Image BuildMosaic()
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // RGGB: even row R G, odd row G B.
                var gain = (y & 1) == 0 ? ((x & 1) == 0 ? GainR : 1f) : ((x & 1) == 0 ? 1f : GainB);
                double signal = Sky;
                foreach (var (sx, sy, fwhm) in Sources)
                {
                    var sigma = fwhm / 2.3548;
                    var dx = x - sx;
                    var dy = y - sy;
                    signal += Amplitude * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                }
                plane[y, x] = (float)(signal * gain);
            }
        }

        var meta = new ImageMeta(
            Instrument: "synthetic",
            ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(1),
            FrameType: FrameType.Light,
            Telescope: "synthetic",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 275,
            FocusPos: FocuserPositions.None,
            Filter: Filter.None,
            BinX: 1, BinY: 1,
            CCDTemperature: float.NaN,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN);

        return new Image([plane], BitDepth.Float32, maxValue: 100000f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }
}
