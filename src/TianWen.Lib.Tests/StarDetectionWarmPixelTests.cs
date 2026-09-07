using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A single warm photosite on an RGGB mosaic is not a star. The mono fold the detector measures on turns
/// it into a 2 by 2 blob that passes the size floor, and on a night whose dark left them in, half of every
/// star list was warm pixels (Orion 2025-10-15: they halved the registration's shifts and refused the PSF
/// store's fit). The guard reads the raw mosaic: a star's flux is spread over its photosites, a warm
/// pixel's is not (<see cref="Image.PeakPhotositeFraction"/> against
/// <see cref="Image.SinglePhotositeFractionMax"/>; measured 0.92 to 0.99 against 0.15 to 0.41 on the real
/// frames).
/// </summary>
[Collection("Imaging")]
public class StarDetectionWarmPixelTests
{
    private const int Size = 320;
    private const float Background = 1000f;
    private const float Sigma = 0.9f;          // FWHM 2.1 px, the Orion night's green width
    private const float Amplitude = 20000f;
    private const float WarmExcess = 6000f;
    private const float NoiseSigma = 8f;

    private static readonly (float X, float Y)[] Stars =
    [
        (60.37f, 70.62f), (150.13f, 60.88f), (230.74f, 150.26f), (80.51f, 210.44f), (170.62f, 250.19f), (260.30f, 40.70f),
    ];

    /// <summary>Warm photosites on every parity of the pattern, none within 12 px of a star or of each other.</summary>
    private static readonly (int X, int Y)[] WarmPixels =
    [
        (30, 30), (31, 120), (100, 140), (200, 200), (280, 280), (45, 260), (300, 100), (120, 300), (250, 90), (190, 20),
        (70, 150), (141, 231), (261, 201), (21, 201), (301, 21),
    ];

    private static ImageMeta Meta => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10),
        FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
        float.NaN, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>
    /// Six Gaussian stars on an RGGB mosaic with the channel gains a colour sensor applies to a white
    /// star, Gaussian read noise, and optionally fifteen warm photosites. Returns the plane too, for the
    /// measure to be tested on the same pixels the detector saw.
    /// </summary>
    private static (Image Image, float[,] Plane) Render(bool warm)
    {
        var rng = new Random(7);
        var data = new float[Size, Size];
        var twoSigmaSq = 2f * Sigma * Sigma;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = 0f;
                foreach (var (sx, sy) in Stars)
                {
                    var dx = x - sx;
                    var dy = y - sy;
                    v += Amplitude * MathF.Exp(-((dx * dx) + (dy * dy)) / twoSigmaSq);
                }

                var gain = ((y & 1), (x & 1)) switch
                {
                    (0, 0) => 1.00f,   // R
                    (1, 1) => 0.55f,   // B
                    _ => 0.80f,        // G
                };
                var u1 = 1.0 - rng.NextDouble();
                var u2 = rng.NextDouble();
                var noise = (float)(NoiseSigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                data[y, x] = Background + (v * gain) + noise;
            }
        }

        if (warm)
        {
            foreach (var (wx, wy) in WarmPixels)
            {
                data[wy, wx] += WarmExcess;
            }
        }

        var image = new Image([data], BitDepth.Float32, Background + Amplitude + WarmExcess, Background - (5f * NoiseSigma), 0f, Meta);
        return (image, data);
    }

    [Fact]
    public void TheMeasureSeparatesAWarmPhotositeFromAStar()
    {
        var (_, plane) = Render(warm: true);

        foreach (var (wx, wy) in WarmPixels)
        {
            Image.PeakPhotositeFraction(plane, wx, wy).ShouldBeGreaterThan(Image.SinglePhotositeFractionMax, $"warm photosite at ({wx}, {wy})");
        }

        foreach (var (sx, sy) in Stars)
        {
            Image.PeakPhotositeFraction(plane, sx, sy).ShouldBeLessThan(0.6f, $"star at ({sx}, {sy})");
        }
    }

    /// <summary>
    /// The real frame behind <c>FindStarsFromFitsFileTests</c>' RGGB counts (an uncalibrated ASI frame):
    /// the mono path is run by hand, as the RGGB branch runs it, and every detection measured on the
    /// mosaic. The ones the guard removes are the 38 the count dropped by, they all carry over 85 percent
    /// of their flux in one photosite, and they are narrower on the mono plane than the stars kept, which
    /// is what says they are spikes and not faint stars lost.
    /// </summary>
    [Fact]
    public async Task OnTheRealFixtureTheDroppedDetectionsAreTheNarrowSpikes()
    {
        var image = await SharedTestData.ExtractGZippedFitsImageAsync("RGGB_frame_bx0_by0_top_down", cancellationToken: TestContext.Current.CancellationToken);
        var mosaic = image.GetChannel(0).Data;
        var mono = await image.DebayerAsync(DebayerAlgorithm.BilinearMono, cancellationToken: TestContext.Current.CancellationToken);
        var unguarded = (await mono.FindStarsAsync(0, snrMin: 10f, maxStars: 5000, cancellationToken: TestContext.Current.CancellationToken))
            .ShiftedBy(Image.BilinearMonoGridOffset, Image.BilinearMonoGridOffset);

        var dropped = unguarded.Where(s => Image.PeakPhotositeFraction(mosaic, s.XCentroid, s.YCentroid) > Image.SinglePhotositeFractionMax).ToList();
        var kept = unguarded.Where(s => !(Image.PeakPhotositeFraction(mosaic, s.XCentroid, s.YCentroid) > Image.SinglePhotositeFractionMax)).ToList();

        unguarded.Count.ShouldBe(3065);
        dropped.Count.ShouldBe(38);
        kept.Count.ShouldBe(3027);
        static float Median(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted[sorted.Length / 2];
        }

        var droppedFwhm = Median(dropped.Select(s => s.StarFWHM));
        var keptFwhm = Median(kept.Select(s => s.StarFWHM));
        droppedFwhm.ShouldBeLessThan(keptFwhm, $"dropped median FWHM {droppedFwhm:F2} against kept {keptFwhm:F2}");
    }

    [Fact]
    public async Task TheDetectorReportsTheStarsAndNotTheWarmPhotosites()
    {
        var (image, _) = Render(warm: true);
        var (control, _) = Render(warm: false);

        var detected = await image.FindStarsAsync(0, snrMin: 5f);
        var withoutWarm = await control.FindStarsAsync(0, snrMin: 5f);

        detected.Count.ShouldBe(Stars.Length);
        withoutWarm.Count.ShouldBe(Stars.Length, "the guard must remove nothing from a clean field");
        foreach (var (sx, sy) in Stars)
        {
            detected.Any(s => MathF.Abs(s.XCentroid - sx) < 0.5f && MathF.Abs(s.YCentroid - sy) < 0.5f)
                .ShouldBeTrue($"star at ({sx}, {sy}) is missing");
        }

        foreach (var (wx, wy) in WarmPixels)
        {
            detected.Any(s => MathF.Abs(s.XCentroid - wx) < 1.5f && MathF.Abs(s.YCentroid - wy) < 1.5f)
                .ShouldBeFalse($"warm photosite at ({wx}, {wy}) was reported as a star");
        }
    }
}
