using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class PlanetaryApStackTests
{
    private const int N = 80;

    private static float[,] TexturedDisk(int n, double cx, double cy, double radius)
    {
        var a = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                a[y, x] = (dx * dx) + (dy * dy) < radius * radius
                    ? (float)(0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3)))
                    : 0.03f;
            }
        }

        return a;
    }

    private static float SampleBilinear(float[,] src, double x, double y)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1)
        {
            return 0f;
        }

        var fx = x - x0;
        var fy = y - y0;
        return (float)((src[y0, x0] * (1 - fx) * (1 - fy)) + (src[y0, x0 + 1] * fx * (1 - fy))
            + (src[y0 + 1, x0] * (1 - fx) * fy) + (src[y0 + 1, x0 + 1] * fx * fy));
    }

    private static float[,] BoxBlur(float[,] src, int passes)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var cur = (float[,])src.Clone();
        for (var p = 0; p < passes; p++)
        {
            var next = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    float sum = 0;
                    var c = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            sum += cur[yy, xx];
                            c++;
                        }
                    }

                    next[y, x] = sum / c;
                }
            }

            cur = next;
        }

        return cur;
    }

    private static ushort[] ToU16(float[,] a)
    {
        int h = a.GetLength(0), w = a.GetLength(1);
        var f = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                f[(y * w) + x] = (ushort)(Math.Clamp(a[y, x], 0f, 1f) * 60000);
            }
        }

        return f;
    }

    private static double MeanAbsDiffToBase(Image master, float[,] baseDisk, Rectangle region)
    {
        double sum = 0;
        var n = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var v = master[0, y, x];
                if (float.IsNaN(v))
                {
                    continue;
                }

                sum += Math.Abs(v - baseDisk[y, x]);
                n++;
            }
        }

        return n > 0 ? sum / n : double.MaxValue;
    }

    [Fact]
    public async Task Ap_stack_reconstructs_reference_better_than_global()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            var baseDisk = TexturedDisk(N, 40, 40, 30);
            // Frame 0 is the undistorted base (so it grades sharpest -> becomes the reference); frames 1..7
            // are the base under a strong per-frame drift + smooth gradient distortion a single translation
            // cannot undo. The AP mesh corrects the gradient, so the AP master lands closer to the base.
            var rng = new Random(5);
            var frames = new ushort[8][];
            frames[0] = ToU16(baseDisk);
            for (var i = 1; i < 8; i++)
            {
                var ax = (rng.NextDouble() * 4) - 2;
                var ay = (rng.NextDouble() * 4) - 2;
                var bx = (rng.NextDouble() * 6) - 3;
                var cy = (rng.NextDouble() * 6) - 3;
                var distorted = new float[N, N];
                for (var y = 0; y < N; y++)
                {
                    for (var x = 0; x < N; x++)
                    {
                        var dx = ax + (bx * (x - 40) / N);
                        var dy = ay + (cy * (y - 40) / N);
                        distorted[y, x] = SampleBilinear(baseDisk, x - dx, y - dy);
                    }
                }

                frames[i] = ToU16(distorted);
            }

            PlanetarySerFixtures.WriteSer(path, N, N, SerColorId.Mono, frames);

            var options = new PlanetaryStackOptions { KeepFraction = 1.0 };
            PlanetaryStackResult global, ap;
            using (var s = SerFrameStream.Open(path))
            {
                global = await new LuckyImagingStacker().StackGlobalAsync(s, options, TestContext.Current.CancellationToken);
            }

            using (var s = SerFrameStream.Open(path))
            {
                ap = await new LuckyImagingStacker().StackAsync(s, options, TestContext.Current.CancellationToken);
            }

            ap.ReferenceIndex.ShouldBe(0); // the undistorted base
            var region = new Rectangle(22, 22, 36, 36);
            var apErr = MeanAbsDiffToBase(ap.Master, baseDisk, region);
            var globalErr = MeanAbsDiffToBase(global.Master, baseDisk, region);

            apErr.ShouldBeLessThan(globalErr); // the mesh recovers the base better than a single translation
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Split_cfa_stack_demosaics_to_full_resolution_rgb()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            // 32x32 RGGB frames: a grey textured disk in every CFA photosite (demosaic yields grey RGB).
            const int n = 32;
            var rng = new Random(11);
            var frames = new ushort[6][];
            for (var i = 0; i < 6; i++)
            {
                var a = new float[n, n];
                var cx = 16 + ((rng.NextDouble() * 2) - 1);
                var cy = 16 + ((rng.NextDouble() * 2) - 1);
                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                        a[y, x] = (float)(0.2 + (0.7 * Math.Exp(-d2 / (2 * 6.0 * 6.0))));
                    }
                }

                frames[i] = ToU16(a);
            }

            PlanetarySerFixtures.WriteSer(path, n, n, SerColorId.BayerRGGB, frames);

            using var stream = SerFrameStream.Open(path);
            stream.Layout.ShouldBe(PlanetaryFrameLayout.SplitCfa);

            var result = await new LuckyImagingStacker()
                .StackAsync(stream, new PlanetaryStackOptions { KeepFraction = 1.0, AlignmentPatchSize = 16 }, TestContext.Current.CancellationToken);

            result.Master.ChannelCount.ShouldBe(3);  // merged CFA -> MHC demosaic -> RGB
            result.Master.Width.ShouldBe(n);          // full resolution restored
            result.Master.Height.ShouldBe(n);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Per_point_weighting_reconstructs_locally_sharp_regions_better()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            const int n = 64;
            var baseDisk = TexturedDisk(n, 32, 32, 28);
            var blurred = BoxBlur(baseDisk, 4);

            // Smooth focus gradient (NO hard seam, which would create spurious AP features): frames 0-3
            // are sharp at the left and smoothly soften to the right; frames 4-7 the mirror. No drift.
            var frames = new ushort[8][];
            for (var i = 0; i < 8; i++)
            {
                var sharpLeft = i < 4;
                var f = new float[n, n];
                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var t = (double)x / (n - 1);
                        var alpha = sharpLeft ? t : 1 - t; // 0 = base (sharp), 1 = blurred
                        f[y, x] = (float)((baseDisk[y, x] * (1 - alpha)) + (blurred[y, x] * alpha));
                    }
                }

                frames[i] = ToU16(f);
            }

            PlanetarySerFixtures.WriteSer(path, n, n, SerColorId.Mono, frames);

            Image perPoint, flat;
            using (var s = SerFrameStream.Open(path))
            {
                perPoint = (await new LuckyImagingStacker().StackAsync(s,
                    new PlanetaryStackOptions { KeepFraction = 1.0, PerPointQualityWeighting = true }, TestContext.Current.CancellationToken)).Master;
            }

            using (var s = SerFrameStream.Open(path))
            {
                flat = (await new LuckyImagingStacker().StackAsync(s,
                    new PlanetaryStackOptions { KeepFraction = 1.0, PerPointQualityWeighting = false }, TestContext.Current.CancellationToken)).Master;
            }

            var left = new Rectangle(8, 22, 18, 20);
            var right = new Rectangle(38, 22, 18, 20);

            // Per-AP best-of pulls each half from the frames that were sharp there, so the master lands
            // closer to the sharp base on both halves than the flat global-weight average does.
            MeanAbsDiffToBase(perPoint, baseDisk, left).ShouldBeLessThan(MeanAbsDiffToBase(flat, baseDisk, left));
            MeanAbsDiffToBase(perPoint, baseDisk, right).ShouldBeLessThan(MeanAbsDiffToBase(flat, baseDisk, right));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Weighted_accumulate_favors_high_quality_source()
    {
        const int n = 48;
        var baseDisk = TexturedDisk(n, 24, 24, 20);
        var blurred = BoxBlur(baseDisk, 4);
        var sharpImg = Image.FromChannel(baseDisk);
        var blurImg = Image.FromChannel(blurred);
        var identity = DisplacementMesh.Build(n, n, 0, 0, ReadOnlySpan<AlignmentPointShift>.Empty);

        var qualityHigh = Filled(n, 2f);
        var qualityLow = Filled(n, 0.5f);
        var qualityFlat = Filled(n, 1f);

        var weighted = Stack(sharpImg, blurImg, identity, qualityHigh, qualityLow, n);
        var equal = Stack(sharpImg, blurImg, identity, qualityFlat, qualityFlat, n);

        var region = new Rectangle(8, 8, 32, 32);
        // Weighting the sharp frame 4x the blurred one lands closer to the sharp base than an equal blend.
        MeanAbsDiff(weighted, baseDisk, region).ShouldBeLessThan(MeanAbsDiff(equal, baseDisk, region));
    }

    [Fact]
    public void Signal_gate_makes_a_faint_region_an_unbiased_mean()
    {
        // A faint region (signal confidence -> 0) where the "brighter" frame also carries the higher local
        // quality weight -- exactly the correlation that makes the un-gated best-of mean drift bright.
        const int n = 16;
        var bright = Image.FromChannel(Filled(n, 0.3f));
        var dark = Image.FromChannel(Filled(n, 0.1f));
        var qBright = Filled(n, 2f);
        var qDark = Filled(n, 0.5f);
        var identity = DisplacementMesh.Build(n, n, 0, 0, ReadOnlySpan<AlignmentPointShift>.Empty);

        var ungated = StackCenter(bright, dark, qBright, qDark, identity, conf: null, n);
        var gated = StackCenter(bright, dark, qBright, qDark, identity, conf: Filled(n, 0f), n);   // faint -> uniform
        var onDisk = StackCenter(bright, dark, qBright, qDark, identity, conf: Filled(n, 1f), n);  // bright body

        ungated.ShouldBe(0.26f, 0.001f); // (2*0.3 + 0.5*0.1)/2.5 -- biased toward the bright frame
        gated.ShouldBe(0.20f, 0.001f);   // (0.3 + 0.1)/2 -- the true unbiased mean
        onDisk.ShouldBe(ungated, 0.001f); // confidence 1 -> gate is a no-op (full best-of on the disk body)
    }

    [Fact]
    public void Signal_confidence_is_high_on_the_disk_and_low_in_the_sky()
    {
        const int n = 64;
        var conf = PlanetaryDisk.SignalConfidence(Image.FromChannel(TexturedDisk(n, 32, 32, 22)));

        conf[32, 32].ShouldBeGreaterThan(0.9f); // disk centre
        conf[2, 2].ShouldBeLessThan(0.1f);       // corner sky
    }

    private static float StackCenter(Image bright, Image dark, float[,] qB, float[,] qD, DisplacementMesh mesh, float[,]? conf, int n)
    {
        var acc = Image.CreateChannelData(1, n, n);
        var wacc = new float[n, n];
        bright.AccumulateByMeshWeightedInto(acc, wacc, mesh, qB, 1f, conf);
        dark.AccumulateByMeshWeightedInto(acc, wacc, mesh, qD, 1f, conf);
        var c = n / 2;
        return wacc[c, c] > 0 ? acc[0][c, c] / wacc[c, c] : 0f;
    }

    private static float[,] Filled(int n, float v)
    {
        var a = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                a[y, x] = v;
            }
        }

        return a;
    }

    private static float[,] Stack(Image a, Image b, DisplacementMesh mesh, float[,] qa, float[,] qb, int n)
    {
        var acc = Image.CreateChannelData(1, n, n);
        var wacc = new float[n, n];
        a.AccumulateByMeshWeightedInto(acc, wacc, mesh, qa, 1f);
        b.AccumulateByMeshWeightedInto(acc, wacc, mesh, qb, 1f);
        var plane = acc[0];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                plane[y, x] = wacc[y, x] > 0 ? plane[y, x] / wacc[y, x] : 0f;
            }
        }

        return plane;
    }

    private static double MeanAbsDiff(float[,] plane, float[,] baseDisk, Rectangle region)
    {
        double sum = 0;
        var cnt = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                sum += Math.Abs(plane[y, x] - baseDisk[y, x]);
                cnt++;
            }
        }

        return sum / cnt;
    }
}
