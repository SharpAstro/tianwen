using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the flat's defect mask (<see cref="BadPixelDetection.BuildMaskFromFlat"/>) and what it is for. The SVBONY
/// SV605CC flats of 2026-10-17 were taken while the camera cooled, and condensation formed on the sensor: 499 droplets,
/// each a 2x2 core at 1.5 to 1.8 of the flat in a ring of 8 photosites at 0.35 to 0.44. The lights were clean, so every
/// calibrated sub carried the inverse, a bright ring round a dark core, at the same sensor positions. The rings were 15
/// to 42 of the 100 brightest detections, and 52 of 154 subs registered on them at the identity.
/// </summary>
public class FlatDefectMaskTests
{
    private const int Size = 256;

    /// <summary>Top-left corners of the planted droplets' 4x4 boxes, on both Bayer parities.</summary>
    private static readonly (int X, int Y)[] Droplets = [(40, 40), (101, 60), (60, 151), (171, 181), (200, 20)];

    /// <summary>Real stars, well away from every droplet.</summary>
    private static readonly (int X, int Y)[] Stars = [(30, 120), (90, 210), (130, 100), (150, 40), (220, 120), (230, 230)];

    [Fact]
    public void CondensationOnTheSensorIsFlagged_VignettingAndADustShadowAreNot()
    {
        var mask = BadPixelDetection.BuildMaskFromFlat(Flat(withDroplets: true)).ShouldHaveSingleItem();

        var outside = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (!InDroplet(x, y))
                {
                    outside += mask[y, x] ? 1 : 0;
                }
            }
        }
        foreach (var (x0, y0) in Droplets)
        {
            foreach (var (x, y) in DropletPhotosites(x0, y0))
            {
                mask[y, x].ShouldBeTrue($"the droplet photosite ({x}, {y})");
            }
        }
        outside.ShouldBe(0, "vignetting, a dust shadow and a 0.5% response scatter are what a flat is FOR");
    }

    [Fact]
    public void ACleanFlatMasksNothing()
        => BadPixelDetection.CountMaskedPixels(BadPixelDetection.BuildMaskFromFlat(Flat(withDroplets: false))).ShouldBe(0);

    /// <summary>A light divided by the droplet flat carries a bright ring at each droplet. Read as is, detection
    /// returns rings as stars (the case that registered subs at the identity); with the flat's mask it returns the real
    /// stars and no ring.</summary>
    [Fact]
    public async Task ADropletRingIsNoStarOnceTheFlatsDefectsAreKnown()
    {
        var ct = TestContext.Current.CancellationToken;
        var flat = Flat(withDroplets: true);
        var calibrated = Calibrated(flat);
        var mask = BadPixelDetection.BuildMaskFromFlat(flat);

        var (unmasked, _) = await FrameRegistration.DetectAsync(calibrated, DebayerAlgorithm.VNG, 5f, 20, cancellationToken: ct);
        var (masked, _) = await FrameRegistration.DetectAsync(calibrated, DebayerAlgorithm.VNG, 5f, 20, knownBad: mask, cancellationToken: ct);

        OnDroplets(unmasked).ShouldBeGreaterThan(0, "the test must show the rings ARE stars without the mask");
        OnDroplets(masked).ShouldBe(0);
        foreach (var (sx, sy) in Stars)
        {
            masked.Any(s => MathF.Abs(s.XCentroid - sx) < 2f && MathF.Abs(s.YCentroid - sy) < 2f)
                .ShouldBeTrue($"the real star at ({sx}, {sy})");
        }

        static int OnDroplets(StarList stars)
            => stars.Count(s => Droplets.Any(d => MathF.Abs(s.XCentroid - (d.X + 1.5f)) <= 3f && MathF.Abs(s.YCentroid - (d.Y + 1.5f)) <= 3f));
    }

    /// <summary>The measured droplet: a 2x2 core inside a ring of 8, in a 4x4 box whose corners are untouched.</summary>
    private static (int X, int Y, float Factor)[] DropletShape(int x0, int y0) =>
    [
        (x0 + 1, y0 + 1, 1.6f), (x0 + 2, y0 + 1, 1.8f), (x0 + 1, y0 + 2, 1.5f), (x0 + 2, y0 + 2, 1.6f),
        (x0 + 1, y0, 0.36f), (x0 + 2, y0, 0.33f), (x0, y0 + 1, 0.36f), (x0 + 3, y0 + 1, 0.35f),
        (x0, y0 + 2, 0.39f), (x0 + 3, y0 + 2, 0.36f), (x0 + 1, y0 + 3, 0.40f), (x0 + 2, y0 + 3, 0.37f),
    ];

    private static (int X, int Y)[] DropletPhotosites(int x0, int y0) => [.. DropletShape(x0, y0).Select(static p => (p.X, p.Y))];

    private static bool InDroplet(int x, int y) => Droplets.Any(d => x >= d.X && x < d.X + 4 && y >= d.Y && y < d.Y + 4);

    /// <summary>A normalised RGGB flat: each colour at its own level, vignetting, a soft dust shadow 40 px across, and a
    /// response scatter of about 0.5% per photosite; the droplets on top when asked.</summary>
    private static Image Flat(bool withDroplets)
    {
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var level = ((y & 1, x & 1)) switch { (0, 0) => 0.95f, (1, 1) => 0.85f, _ => 1f };
                var r2 = ((x - 128f) * (x - 128f) + (y - 128f) * (y - 128f)) / (128f * 128f);
                var vignetting = 1f - 0.15f * r2;
                var d = MathF.Sqrt((x - 180f) * (x - 180f) + (y - 90f) * (y - 90f));
                var dust = 1f - 0.08f * Smooth(d - 12f) * Smooth(40f - d);
                data[y, x] = level * vignetting * dust * (1f + Scatter(x, y, 0.009f));
            }
        }
        if (withDroplets)
        {
            foreach (var (x0, y0) in Droplets)
            {
                foreach (var (x, y, factor) in DropletShape(x0, y0))
                {
                    data[y, x] *= factor;
                }
            }
        }
        return new Image([data], BitDepth.Float32, 2f, 0f, 0f, Meta(FrameType.Flat));

        // A shadow edge softened over about 5 px, as a speck off the focal plane casts it.
        static float Smooth(float t) => 1f / (1f + MathF.Exp(-t / 2.5f));
    }

    /// <summary>A light the flat divides: sky with about 3% noise and Gaussian stars, its flat WITHOUT droplets (the
    /// sensor was dry for the lights), divided by the flat WITH them.</summary>
    private static Image Calibrated(Image dropletFlat)
    {
        var clean = Flat(withDroplets: false).GetChannelArray(0);
        var flat = dropletFlat.GetChannelArray(0);
        var data = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var signal = 1000f * (1f + Scatter(x + 7, y + 13, 0.06f));
                foreach (var (sx, sy) in Stars)
                {
                    var r2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                    signal += 8000f * MathF.Exp(-r2 / (2f * 1.6f * 1.6f));
                }
                data[y, x] = signal * clean[y, x] / flat[y, x];
            }
        }
        return new Image([data], BitDepth.Float32, 20000f, 0f, 0f, Meta(FrameType.Light));
    }

    /// <summary>A deterministic per-photosite scatter, uniform in +-<paramref name="halfWidth"/>.</summary>
    private static float Scatter(int x, int y, float halfWidth)
    {
        var h = (((uint)x * 73856093u) ^ ((uint)y * 19349663u)) * 0x5bd1e995u;
        return ((h >> 8) / (float)(1u << 24) * 2f - 1f) * halfWidth;
    }

    private static ImageMeta Meta(FrameType type) => new("SVBONY SV605CC", DateTime.UnixEpoch, TimeSpan.FromSeconds(10),
        type, "", 3.76f, 3.76f, 120, 20, Filter.Unknown, 1, 1, -5f, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
}
