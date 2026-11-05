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
public class DebayerAhdGainEqualisationTests(ITestOutputHelper output)
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

        AssertSources(await DebayerInStripsAsync(BuildMosaic(), ct));
    }

    /// <summary>
    /// The undersampled regime of the issue (FWHM 2.2 and a 2 px point source), over a grid of sources at spread
    /// sub-pixel phases: one source's ratio carries its own phase's lattice aliasing, the MEAN over phases does not,
    /// so what is left is the demosaic's bias. The red and blue photosites repeat every TWO pixels, so the phases must
    /// cover that whole period (0, .5, 1, 1.5 on each axis): over half of it (0 to .75) red always sat near its photosite
    /// and blue between two of its own, which read red about 1.4 percent high and blue as much low at FWHM 2.2 (2.8 at
    /// 2.0) in ANY demosaic; MHC showed it as plainly as AHD. MHC on the same mosaic is the reference, and the same
    /// mosaic with no colour cast is the control: equalised, the cast must not move AHD's ratios at all.
    /// </summary>
    [Theory]
    [InlineData(2.2)]
    [InlineData(2.0)]
    public async Task PhaseAveraged_UndersampledSources_KeepColourRatiosAndCentroids(double fwhm)
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = PhaseGrid(fwhm);
        var cast = BuildMosaic(sources, Size, GainR, GainB);
        var unity = BuildMosaic(sources, Size, 1f, 1f);

        var (ahdRg, ahdBg, ahdShift) = PhaseMean(
            await cast.DebayerAsync(DebayerAlgorithm.AHD, normalizeToUnit: false, cancellationToken: ct), sources, GainR, GainB);
        var (stripRg, stripBg, stripShift) = PhaseMean(await DebayerInStripsAsync(cast, ct), sources, GainR, GainB);
        var (unityRg, unityBg, _) = PhaseMean(
            await unity.DebayerAsync(DebayerAlgorithm.AHD, normalizeToUnit: false, cancellationToken: ct), sources, 1f, 1f);
        var (mhcRg, mhcBg, _) = PhaseMean(
            await cast.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: false, cancellationToken: ct), sources, GainR, GainB);

        var report = FormattableString.Invariant(
            $"FWHM {fwhm}, error against the cast: AHD R/G {(ahdRg / GainR) - 1:P2} B/G {(ahdBg / GainB) - 1:P2} shift {ahdShift:F3} px; strips R/G {(stripRg / GainR) - 1:P2} B/G {(stripBg / GainB) - 1:P2}; AHD with no cast R/G {unityRg - 1:P2} B/G {unityBg - 1:P2}; MHC R/G {(mhcRg / GainR) - 1:P2} B/G {(mhcBg / GainB) - 1:P2}");
        output.WriteLine(report);

        // The measure itself is unbiased: MHC, a linear demosaic, keeps both ratios.
        Math.Abs((mhcRg / GainR) - 1).ShouldBeLessThan(0.005, report);
        Math.Abs((mhcBg / GainB) - 1).ShouldBeLessThan(0.005, report);
        // The equalisation's own claim: a colour cast no longer moves AHD's ratios.
        Math.Abs((ahdRg / GainR) - unityRg).ShouldBeLessThan(0.0025, report);
        Math.Abs((ahdBg / GainB) - unityBg).ShouldBeLessThan(0.0025, report);
        // The issue's bound, on the full frame and on strips alike.
        foreach (var (rg, bg, shift) in new[] { (ahdRg, ahdBg, ahdShift), (stripRg, stripBg, stripShift) })
        {
            Math.Abs(rg - GainR).ShouldBeLessThanOrEqualTo(GainR * 0.03, report);
            Math.Abs(bg - GainB).ShouldBeLessThanOrEqualTo(GainB * 0.03, report);
            shift.ShouldBeLessThan(0.15, report);
        }
    }

    private static async Task<Image> DebayerInStripsAsync(Image image, System.Threading.CancellationToken ct)
    {
        // Only a call's own rect is valid afterwards: it also writes intermediate values into its halo rows outside the rect
        // (which is why TilePipelinedStrategy samples only inside it). So each strip's rows are copied out before the next
        // call, whose halo reaches three rows into them.
        var scratch = new Channel[] { Channel.Create(Size, Size), Channel.Create(Size, Size), Channel.Create(Size, Size) };
        var assembled = new[] { new float[Size, Size], new float[Size, Size], new float[Size, Size] };
        // Overlapping strips: each owns 32 rows and re-debayers 8 rows of its neighbour.
        foreach (var (y, h) in new[] { (0, 40), (32, 40), (64, 32) })
        {
            var strip = await image.DebayerRegionIntoAsync(scratch, DebayerAlgorithm.AHD, new PixelRect(0, y, Size, h), ct);
            for (var c = 0; c < 3; c++)
            {
                for (var row = y; row < y + h; row++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        assembled[c][row, x] = strip[c, row, x];
                    }
                }
            }
        }
        return new Image(assembled, BitDepth.Float32, 100000f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
    }

    /// <summary>Sixteen sources 22 px apart, each at its own phase on the two-pixel period of the red and blue
    /// photosites: every pairing of the four x phases (0, .5, 1, 1.5) with the four y phases occurs once.</summary>
    private static (double X, double Y, double Fwhm)[] PhaseGrid(double fwhm)
    {
        var sources = new System.Collections.Generic.List<(double X, double Y, double Fwhm)>();
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                sources.Add((14.0 + (i * 22) + (((i + j) % 4) * 0.5), 14.0 + (j * 22) + (((i + (2 * j)) % 4) * 0.5), fwhm));
            }
        }
        return [.. sources];
    }

    private static (double Rg, double Bg, double Shift) PhaseMean(Image result, (double X, double Y, double Fwhm)[] sources, float gainR, float gainB)
    {
        var skyOut = new[] { Sky * gainR, Sky, Sky * gainB };
        var total = new double[3];
        double shiftSum = 0;
        foreach (var (sx, sy, _) in sources)
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
                total[c] += flux[c];
                cx[c] /= flux[c];
                cy[c] /= flux[c];
            }
            shiftSum += Math.Max(
                Math.Max(Math.Abs(cx[0] - cx[1]), Math.Abs(cy[0] - cy[1])),
                Math.Max(Math.Abs(cx[2] - cx[1]), Math.Abs(cy[2] - cy[1])));
        }
        return (total[0] / total[1], total[2] / total[1], shiftSum / sources.Length);
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
            ok &= Math.Abs(rg - GainR) <= GainR * 0.05 && Math.Abs(bg - GainB) <= GainB * 0.05 && worstShift < 0.15;
        }

        ok.ShouldBeTrue($"expected R/G {GainR}, B/G {GainB} within 5 percent and centroids within 0.15 px; got {report}");
    }

    private static Image BuildMosaic() => BuildMosaic(Sources, Size, GainR, GainB);

    private static Image BuildMosaic((double X, double Y, double Fwhm)[] sources, int size, float gainR, float gainB)
    {
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // RGGB: even row R G, odd row G B.
                var gain = (y & 1) == 0 ? ((x & 1) == 0 ? gainR : 1f) : ((x & 1) == 0 ? 1f : gainB);
                double signal = Sky;
                foreach (var (sx, sy, fwhm) in sources)
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
