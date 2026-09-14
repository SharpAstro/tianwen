using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Sources;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The background map and the source segmentation on synthetic frames whose truth is known: a sky with
/// a gradient and noise, Gaussian stars, a Gaussian nebula, a close pair, a canvas ring of exact zero.
/// </summary>
[Collection("Imaging")]
public class SourceSegmentationTests(ITestOutputHelper output)
{
    private const int W = 512;
    private const int H = 384;
    private const float Sky = 0.10f;
    private const float Noise = 0.002f;

    [Fact]
    public void TheBackgroundMapRecoversAGradientUnderStarsWithinTheNoise()
    {
        var rng = new Random(11);
        var plane = new float[H * W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y * W + x] = Truth(x, y) + Noise * Gaussian(rng);
            }
        }

        for (var i = 0; i < 60; i++)
        {
            AddStar(plane, rng.Next(8, W - 8), rng.Next(8, H - 8), 0.05f + 0.4f * rng.NextSingle(), 1.5f);
        }

        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 32));

        var err = 0.0;
        var n = 0;
        for (var y = 0; y < H; y += 7)
        {
            for (var x = 0; x < W; x += 7)
            {
                var d = map.BackgroundAt(x, y) - Truth(x, y);
                err += d * d;
                n++;
            }
        }

        var rms = Math.Sqrt(err / n);
        output.WriteLine($"background RMS error {rms:E2} against noise {Noise:E2}; global rms {map.GlobalRms:E2}");
        rms.ShouldBeLessThan(0.5 * Noise, "the mesh background should follow the gradient to well under the pixel noise");
        map.GlobalRms.ShouldBeInRange(0.85f * Noise, 1.15f * Noise, "the MAD-based cell RMS should read the injected noise");
        map.RmsAt(W / 2, H / 2).ShouldBeInRange(0.8f * Noise, 1.2f * Noise);
    }

    [Fact]
    public void ACanvasRingOfExactZeroDoesNotPullTheSkyDown()
    {
        var rng = new Random(5);
        var plane = new float[H * W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y * W + x] = x < 40 || y < 40 ? 0f : Sky + Noise * Gaussian(rng);
            }
        }

        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 32));
        map.BackgroundAt(45, 45).ShouldBeInRange(Sky - 3 * Noise, Sky + 3 * Noise, "the first covered pixels read the sky, not the ring");
        map.BackgroundAt(0, 0).ShouldBeInRange(Sky - 3 * Noise, Sky + 3 * Noise, "the ring's own cells are filled from their neighbours");
    }

    [Fact]
    public void StarsAreCompactSegmentsAndANebulaIsStructure()
    {
        var rng = new Random(23);
        var plane = new float[H * W];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = Sky + Noise * Gaussian(rng);
        }

        var stars = new (int X, int Y)[] { (60, 60), (200, 90), (400, 300), (100, 320), (450, 60) };
        foreach (var (x, y) in stars)
        {
            AddStar(plane, x, y, 0.2f, 1.6f);
        }

        // A soft nebula: 40 px sigma, 15 noise sigmas at its centre.
        AddStar(plane, 300, 200, 15f * Noise, 40f);

        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 64));
        var seg = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(ThresholdSigma: 3f, MinPixels: 5));

        output.WriteLine($"{seg.Segments.Length} segments");
        foreach (var s in seg.Segments)
        {
            output.WriteLine($"  #{s.Label} area {s.Area} at ({s.XCentroid:F1},{s.YCentroid:F1}) peak {s.Peak:F3} core {s.CoreFraction:F2} elong {s.Elongation:F2} compact {s.IsCompact}");
        }

        // A nebula's wing sits at the threshold, so a few islands of a handful of pixels are expected there
        // (photutils reports them too); the sources of the frame are the segments with some area.
        var compact = seg.Segments.Where(s => s.IsCompact && s.Area >= 20).ToArray();
        var extended = seg.Segments.Where(s => !s.IsCompact).ToArray();
        compact.Length.ShouldBe(stars.Length, "every star is one compact segment");
        extended.Length.ShouldBe(1, "the nebula is one extended segment, not cut at its own level");
        extended[0].Area.ShouldBeGreaterThan(10000, "a 15 sigma, 40 px nebula's 3 sigma contour is about 70 px out");
        seg.Segments.Count(s => s.Area < 20).ShouldBeLessThan(6, "islands at the wing's threshold stay few");
        extended[0].XCentroid.ShouldBeInRange(296f, 304f);
        extended[0].YCentroid.ShouldBeInRange(196f, 204f);
        foreach (var (x, y) in stars)
        {
            var star = compact.Single(s => Math.Abs(s.XCentroid - x) < 1f && Math.Abs(s.YCentroid - y) < 1f);
            seg.LabelAt(x, y).ShouldBe(star.Label);
        }

        // Masks agree with the flags.
        var starMask = seg.StarMask(marginPx: 2);
        var structure = seg.StructureMask();
        var sky = seg.SkyMask(marginPx: 3);
        starMask[60, 60].ShouldBeTrue();
        starMask[200, 300].ShouldBeFalse("the nebula's centre is not in the star mask");
        structure[200, 300].ShouldBeTrue("the nebula's centre is structure");
        structure[60, 60].ShouldBeFalse();
        sky[60, 60].ShouldBeFalse();
        sky[200, 300].ShouldBeFalse();
        sky[20, 20].ShouldBeTrue();
        // The margin grows the star mask past the segment's own pixels.
        var bare = seg.StarMask(marginPx: 0);
        CountTrue(starMask).ShouldBeGreaterThan(CountTrue(bare));
    }

    [Fact]
    public void ACloseBrightPairIsDeblendedAndAnUnsplitNebulaIsNot()
    {
        var rng = new Random(7);
        var plane = new float[H * W];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = Sky + Noise * Gaussian(rng);
        }

        // Two stars 6 px apart, sharing threshold pixels; a third alone.
        AddStar(plane, 100, 100, 0.3f, 1.5f);
        AddStar(plane, 106, 100, 0.2f, 1.5f);
        AddStar(plane, 300, 300, 0.3f, 1.5f);
        // A nebula with a mild internal brightness variation that must NOT split (no saddle under half).
        AddStar(plane, 400, 120, 20f * Noise, 30f);
        AddStar(plane, 410, 125, 4f * Noise, 12f);

        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 64));
        var deblended = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(Deblend: true));
        var merged = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(Deblend: false));

        foreach (var s in deblended.Segments)
        {
            output.WriteLine($"  #{s.Label} area {s.Area} at ({s.XCentroid:F1},{s.YCentroid:F1}) peak {s.Peak:F3} core {s.CoreFraction:F2} compact {s.IsCompact}");
        }

        output.WriteLine($"deblended {deblended.Segments.Length}, merged {merged.Segments.Length}");
        merged.Segments.Count(s => s.Area >= 20).ShouldBe(3, "without deblending the pair is one segment");
        deblended.Segments.Count(s => s.Area >= 20).ShouldBe(4, "the pair splits, the nebula's knot does not");
        var pair = deblended.Segments.Where(s => s.YCentroid is > 95 and < 105 && s.XCentroid is > 90 and < 115).OrderBy(s => s.XCentroid).ToArray();
        pair.Length.ShouldBe(2);
        pair[0].XCentroid.ShouldBeInRange(99f, 101.5f);
        pair[1].XCentroid.ShouldBeInRange(104.5f, 107f);
        pair[0].IsCompact.ShouldBeTrue();
        pair[1].IsCompact.ShouldBeTrue();
        deblended.Segments.Count(s => !s.IsCompact && s.Area >= 20).ShouldBe(1, "one extended segment, unsplit");
    }

    [Fact]
    public void LabellingIsEightConnectedAndDropsIslandsUnderTheMinimum()
    {
        const int w = 8;
        const int h = 6;
        var above = new bool[w * h];
        // A diagonal chain (8-connected) of 4 pixels, and an isolated single pixel.
        above[0 * w + 0] = true;
        above[1 * w + 1] = true;
        above[2 * w + 2] = true;
        above[3 * w + 3] = true;
        above[5 * w + 7] = true;
        var labels = new int[w * h];
        var count = SourceSegmentation.LabelConnected(above, w, h, labels);
        count.ShouldBe(2);
        labels[0].ShouldBe(labels[3 * w + 3], "the diagonal chain is one component under 8-connectivity");
        labels[5 * w + 7].ShouldNotBe(labels[0]);
        SourceSegmentation.DropSmall(labels, count, minPixels: 2).ShouldBe(1);
        labels[5 * w + 7].ShouldBe(0, "the single pixel became sky");
        labels[0].ShouldBe(1, "the survivor is relabelled 1");
    }

    [Fact]
    public void ACrowdedFieldReThresholdsUntilTheBlobsComeApart()
    {
        // The Statue master's mechanism in miniature: a region of unresolved glow a few sigma high (a
        // Milky Way field's faint population, which the second background pass masks and cannot follow)
        // with a few dozen resolved stars on it. At 3 sigma the glow carries every star into one segment
        // of tens of thousands of pixels with dozens of maxima; a sigma or two higher the glow is under
        // the threshold and the segment is its stars. (A uniform star field does not do this: one star
        // every 6 px never separates at any threshold and one every 8 px never joins.)
        var rng = new Random(41);
        var plane = new float[H * W];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = Sky + Noise * Gaussian(rng);
        }

        const int gx0 = 120, gy0 = 80, gside = 240;
        for (var y = gy0; y < gy0 + gside; y++)
        {
            for (var x = gx0; x < gx0 + gside; x++)
            {
                plane[y * W + x] += 4.2f * Noise;
            }
        }

        for (var i = 0; i < 60; i++)
        {
            AddStar(plane, rng.Next(gx0 + 6, gx0 + gside - 6), rng.Next(gy0 + 6, gy0 + gside - 6), 0.006f + 0.02f * rng.NextSingle(), 1.5f);
        }

        AddStar(plane, W / 2, H / 2, 0.5f, 1.8f);

        // Cells of 128 px: a 240 px glow spans two, which the mesh's 3x3 median filter removes from the
        // sky, so the first pass sees the glow and the second pass masks it (with 32 px cells the mesh
        // absorbs it outright and the field reads as sky, the case the extractor is for).
        var map = BackgroundMap.Estimate(plane, W, H, new BackgroundMapOptions(BlockSize: 128));
        var stuck = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(CrowdedRetries: 0, CrowdedArea: 1500));
        var freed = SourceSegmentation.Detect(plane, W, H, map, new SourceDetectionOptions(CrowdedRetries: 2, CrowdedArea: 1500));
        var largestStuck = stuck.Segments.MaxBy(s => s.Area);
        var largestFreed = freed.Segments.MaxBy(s => s.Area);
        output.WriteLine($"no retry: {stuck.Segments.Length} segments, largest {largestStuck.Area} px with {largestStuck.PeakCount} maxima; "
            + $"with retries: {freed.Segments.Length} segments, largest {largestFreed.Area} px with {largestFreed.PeakCount} maxima");
        largestStuck.Area.ShouldBeGreaterThan(1500, "at 3 sigma the glow carries the stars into one segment");
        largestStuck.PeakCount.ShouldBeGreaterThanOrEqualTo(32, "a segment of a star field holds many maxima");
        largestFreed.Area.ShouldBeLessThan(largestStuck.Area, "a sigma or two higher the segment has shed its glow");
        freed.Segments.Length.ShouldBeGreaterThan(stuck.Segments.Length, "the count rises as the segment comes apart");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void TheWordLevelDilationMatchesTheBooleanOne(int radius)
    {
        // 150 columns so the mask spans three words with padding in the last; bits on word edges and at the frame's edges.
        const int w = 150;
        const int h = 40;
        var rng = new Random(radius);
        var mask = new BitMatrix(h, w);
        var flags = new bool[w * h];
        for (var k = 0; k < 60; k++)
        {
            var x = rng.Next(w);
            var y = rng.Next(h);
            mask[y, x] = true;
            flags[y * w + x] = true;
        }

        foreach (var (x, y) in new[] { (0, 0), (63, 5), (64, 5), (127, 9), (128, 9), (149, 39), (149, 0) })
        {
            mask[y, x] = true;
            flags[y * w + x] = true;
        }

        mask.DilateSquare(radius);
        MaskOps.DilateSquare(flags, w, h, radius);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                mask[y, x].ShouldBe(flags[y * w + x], $"({x},{y}) at radius {radius}");
            }
        }

        // Padding bits past the last column stay clear: the last word carries no bit at or above column 150.
        for (var y = 0; y < h; y++)
        {
            (mask.GetWord(y, 2) >> (150 - 128)).ShouldBe(0ul, $"row {y} padding");
        }
    }

    private static float Truth(int x, int y) => Sky + 0.02f * x / W - 0.01f * y / H;

    private static void AddStar(float[] plane, int cx, int cy, float amplitude, float sigma)
        => SourceTestFrames.AddStar(plane, W, H, cx, cy, amplitude, sigma);

    private static float Gaussian(Random rng) => SourceTestFrames.Gaussian(rng);

    private static int CountTrue(BitMatrix m)
    {
        var n = 0;
        for (var y = 0; y < m.GetLength(0); y++)
        {
            for (var x = 0; x < m.GetLength(1); x++)
            {
                if (m[y, x])
                {
                    n++;
                }
            }
        }

        return n;
    }
}
