using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stacker writes the saturation sidecar SPCC's clip gate reads (#1991): per master pixel, how many lights had a
/// photosite at the ADC ceiling there, found on the RAW decode and carried through the registration warp.
/// </summary>
[Collection("Imaging")]
public class SaturationStackingTests(ITestOutputHelper output)
{
    private const float Ceiling = 4500f;
    /// <summary>Every photosite but the clipped star's is held at or under this, so the star is also the brightest thing
    /// in the master and can be found there without the saturation count.</summary>
    private const float OtherCap = 3000f;
    private const int BlobX = 200;
    private const int BlobY = 150;

    /// <summary>
    /// A wide dither, so the master's canvas is offset from the reference frame by about 13 px on both axes. Frame 0, the
    /// pinned reference, sits at the origin; one frame reaches +12.4 px in x and another +12.7 px in y, and no frame holds
    /// both maxima. A count that left the canvas origin shift out of its transform would mark the star about 18 px from
    /// where the master has it, against a 2 px tolerance.
    /// </summary>
    private static readonly (double DX, double DY)[] Dither =
    [
        (0.0, 0.0),
        (12.4, 0.6),
        (0.3, 12.7),
        (1.6, 1.3),
        (-1.1, 2.2),
        (2.4, -1.6),
        (11.6, -0.8),
        (-0.7, 11.9),
    ];

    /// <summary>The lights, with a star at (BlobX, BlobY) (moved by each frame's dither) clipped at the ceiling when
    /// <paramref name="clip"/>. Every other photosite is held at <see cref="OtherCap"/>, so only the blob reaches the
    /// ceiling.</summary>
    private static void WriteLights(string lightsDir, bool clip)
    {
        for (var i = 0; i < Dither.Length; i++)
        {
            var (dx, dy) = Dither[i];
            var mono = SyntheticStarFieldRenderer.Render(
                width: RgbBayerSyntheticFixture.FrameSize, height: RgbBayerSyntheticFixture.FrameSize, defocusSteps: 0,
                offsetX: dx, offsetY: dy, exposureSeconds: 10.0, skyBackground: 100.0, readNoise: 5.0,
                starCount: RgbBayerSyntheticFixture.StarCount, seed: 1337, hotPixelCount: 0, maxADU: 4096.0,
                noiseSeed: 1337 + i);
            var bayered = RgbBayerSyntheticFixture.BuildBayerMosaic(mono, RgbBayerSyntheticFixture.DarkLevel);
            // The renderer does not clamp star peaks (maxADU only sets hot pixels), so bring every photosite
            // well under the ceiling: only the blob below may reach it.
            for (var y = 0; y < bayered.GetLength(0); y++)
            {
                for (var x = 0; x < bayered.GetLength(1); x++)
                {
                    bayered[y, x] = Math.Min(bayered[y, x], OtherCap);
                }
            }
            if (clip)
            {
                var cx = (int)Math.Round(BlobX + dx);
                var cy = (int)Math.Round(BlobY + dy);
                for (var y = cy - 2; y <= cy + 2; y++)
                {
                    for (var x = cx - 2; x <= cx + 2; x++)
                    {
                        if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= 4)
                        {
                            bayered[y, x] = Ceiling;
                        }
                    }
                }
            }
            var meta = new ImageMeta
            {
                Instrument = "SynthBayer",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero).AddSeconds(i),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = FrameType.Light,
                PixelSizeX = 3.76f,
                PixelSizeY = 3.76f,
                FocalLength = 1000,
                BinX = 1,
                BinY = 1,
                CCDTemperature = -5f,
                SensorType = SensorType.RGGB,
                ObjectName = "SynthRgb",
                Gain = 100,
                Offset = 25,
                SensorFullScaleAdu = Ceiling,
            };
            new Image([bayered], BitDepth.Int16, Ceiling, 0f, 0f, meta)
                .WriteToFitsFile(Path.Combine(lightsDir, $"light_{i:D2}.fits"));
        }
    }

    private async Task<(GroupResult Result, Image? Saturation, (double X, double Y) StarInMaster)> StackAsync(
        bool clip, IntegrationStrategyKind? forced, DrizzleOptions? drizzle)
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        WriteLights(workspace.LightsDir, clip);
        var options = new StackingOptions(
            DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir,
            ForcedStrategy: forced, DrizzleOptions: drizzle,
            // Frame 0 is the reference, so the canvas shift is the dither's own spread (about 13 px on each axis).
            ReferenceFrameHint: "light_00");
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }
        results.Count.ShouldBe(1);
        var result = results[0];
        result.SkipReason.ShouldBeEmpty();
        result.MasterFitsPath.ShouldNotBeNull();
        // Both are read while the workspace is alive.
        IntegrationFitsWriter.TryReadSaturationMap(result.MasterFitsPath, out var map);
        Image.TryReadFitsFile(result.MasterFitsPath, out var master).ShouldBeTrue();
        var star = clip ? BrightestSpot(master) : (double.NaN, double.NaN);
        master.Release();
        return (result, map, star);
    }

    /// <summary>Where the clipped star IS in the integrated master, from the master's own pixels: the centroid of the
    /// brightest spot (summed over the channels) above half its height over the frame's median. It is the only spot that
    /// reached the ceiling, so it is by far the brightest; nothing here reads the saturation count.</summary>
    private static (double X, double Y) BrightestSpot(Image master)
    {
        var w = master.Width;
        var h = master.Height;
        var sum = new float[h, w];
        var sample = new List<float>();
        var peak = float.NegativeInfinity;
        int px = -1, py = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = 0f;
                for (var c = 0; c < master.ChannelCount; c++)
                {
                    v += master[c, y, x];
                }
                sum[y, x] = v;
                if (!float.IsFinite(v))
                {
                    continue;
                }
                if ((x % 7) == 0 && (y % 7) == 0)
                {
                    sample.Add(v);
                }
                if (v > peak)
                {
                    peak = v;
                    px = x;
                    py = y;
                }
            }
        }
        sample.Sort();
        var threshold = 0.5f * (peak + sample[sample.Count / 2]);
        double sw = 0, sx = 0, sy = 0;
        for (var y = Math.Max(0, py - 5); y <= Math.Min(h - 1, py + 5); y++)
        {
            for (var x = Math.Max(0, px - 5); x <= Math.Min(w - 1, px + 5); x++)
            {
                var v = sum[y, x];
                if (float.IsFinite(v) && v > threshold)
                {
                    sw += v - threshold;
                    sx += (v - threshold) * x;
                    sy += (v - threshold) * y;
                }
            }
        }
        return (sx / sw, sy / sw);
    }

    private void ShouldCoverOnlyTheBlob(Image map, int frames, (double X, double Y) star)
    {
        var plane = map.GetChannelArray(0);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, nonzero = 0;
        double sumW = 0, sumX = 0, sumY = 0;
        float peak = 0;
        for (var y = 0; y < plane.GetLength(0); y++)
        {
            for (var x = 0; x < plane.GetLength(1); x++)
            {
                if (plane[y, x] > 0)
                {
                    nonzero++;
                    sumW += plane[y, x]; sumX += plane[y, x] * x; sumY += plane[y, x] * y;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    peak = Math.Max(peak, plane[y, x]);
                }
            }
        }
        nonzero.ShouldBeGreaterThan(0, "the clipped star must be counted");
        (maxX - minX).ShouldBeLessThan(14, "only the clipped star's neighbourhood (dither included) may be marked");
        (maxY - minY).ShouldBeLessThan(14);
        peak.ShouldBeLessThanOrEqualTo(frames);
        peak.ShouldBeGreaterThan(1f, "the star is clipped in every sub, so overlapping subs add up");

        // WHERE it is marked: where the star is in the master, found from the master's own pixels. The fixture must be able
        // to catch a count that left the canvas origin shift out: the star's place on the reference frame, (BlobX, BlobY),
        // is where such a count would mark it, so it has to lie well outside the tolerance.
        var centroidX = sumX / sumW;
        var centroidY = sumY / sumW;
        output.WriteLine(FormattableString.Invariant(
            $"marked centroid ({centroidX:F2}, {centroidY:F2}); star in the master ({star.X:F2}, {star.Y:F2}); on the reference frame ({BlobX}, {BlobY}); canvas {plane.GetLength(1)}x{plane.GetLength(0)}"));
        Math.Max(Math.Abs(star.X - BlobX), Math.Abs(star.Y - BlobY)).ShouldBeGreaterThan(6.0,
            "the canvas origin shift must be large enough that an unshifted count would miss the star");
        centroidX.ShouldBe(star.X, 2.0, "the marks must sit on the clipped star where the master has it");
        centroidY.ShouldBe(star.Y, 2.0, "the marks must sit on the clipped star where the master has it");
        // And nothing else is marked far from it.
        (maxX - centroidX).ShouldBeLessThan(10);
        (centroidX - minX).ShouldBeLessThan(10);
    }

    [Fact(Timeout = 300_000)]
    public async Task DefaultAhdPath_WritesASaturationSidecarOverTheClippedStar()
    {
        var (result, map, star) = await StackAsync(clip: true, forced: null, drizzle: null);
        map.ShouldNotBeNull("a master with clipped subs carries a saturation sidecar");
        ShouldCoverOnlyTheBlob(map, result.FramesMatched, star);
    }

    [Fact(Timeout = 300_000)]
    public async Task BayerDrizzlePath_WritesASaturationSidecarOverTheClippedStar()
    {
        var (result, map, star) = await StackAsync(clip: true, IntegrationStrategyKind.BayerDrizzle, new DrizzleOptions(MinFrameCount: 6));
        map.ShouldNotBeNull();
        ShouldCoverOnlyTheBlob(map, result.FramesMatched, star);
    }

    private static Image ClippedAt(float clip, ImageMeta meta, out Matrix3x2 identity)
    {
        identity = Matrix3x2.Identity;
        var plane = new float[16, 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                plane[y, x] = 1000f;
            }
        }
        plane[8, 8] = clip;
        return new Image([plane], BitDepth.Int16, clip, 0f, 0f, meta);
    }

    [Theory]
    [InlineData(65535f)]
    [InlineData(65532f)] // N.I.N.A. 14-bit left-aligned (the SV605CC fixture's clip)
    [InlineData(65520f)] // 12-bit left-aligned
    public void ContainerCeiling_CountsLeftAlignedClipsWithNoSaturateCard(float clip)
    {
        var raw = ClippedAt(clip, new ImageMeta { SensorType = SensorType.RGGB }, out var toCanvas);
        var ceiling = SaturationCounter.CeilingOf(raw).ShouldNotBeNull();
        var mark = new byte[16 * 16];
        SaturationCounter.MarkFrame(raw.GetChannelArray(0), ceiling, toCanvas, 16, 16, mark).ShouldBeTrue();
        mark[(8 * 16) + 8].ShouldBe((byte)1);
    }

    [Fact]
    public void ContainerCeiling_IgnoresAnOrdinaryBrightPhotosite()
    {
        var raw = ClippedAt(40000f, new ImageMeta { SensorType = SensorType.RGGB }, out var toCanvas);
        var mark = new byte[16 * 16];
        SaturationCounter.MarkFrame(raw.GetChannelArray(0), SaturationCounter.CeilingOf(raw).ShouldNotBeNull(), toCanvas, 16, 16, mark).ShouldBeFalse();
    }

    [Fact(Timeout = 300_000)]
    public async Task NoClippedPhotosite_WritesNoSaturationSidecar()
    {
        // Stated choice: a set with nothing at the ceiling writes NO sidecar (absence means "peak gate alone").
        var (_, map, _) = await StackAsync(clip: false, forced: null, drizzle: null);
        map.ShouldBeNull();
    }
}
