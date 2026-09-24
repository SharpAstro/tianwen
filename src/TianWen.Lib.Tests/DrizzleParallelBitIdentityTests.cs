using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The fast drizzle is the old drizzle, bit for bit. A deposit split across canvas strips in parallel,
/// the SIMD slope plane and final divide, and a fused run building several integrations from one stream
/// must each produce EXACTLY the floats the serial code did, because the dataset bake that switched to
/// them had already baked a third of its sessions with the serial code and resumes on top of them.
/// </summary>
/// <remarks>
/// Sizes are deliberately awkward (203 x 157: not a multiple of any vector width or of the 64-row
/// strip), frames carry NaN photosites and masked ones, and the transforms include a meridian flip and
/// scales of one half and two, where a strip's source halo is easiest to get wrong. Equality is on the
/// bit pattern, so a NaN compares equal to itself and nothing is tolerated.
/// </remarks>
[Collection("Imaging")]
public class DrizzleParallelBitIdentityTests
{
    private const int W = 203;
    private const int H = 157;
    private const float HalfP = 0.5f;

    public static TheoryData<string> Transforms => ["shift", "rotation", "flip", "half", "double"];

    private static Matrix3x2 TransformFor(string name, int frame)
    {
        var dither = Matrix3x2.CreateTranslation(0.37f * frame, 0.23f * frame);
        var centre = new Vector2(W / 2f, H / 2f);
        return name switch
        {
            "shift" => Matrix3x2.CreateTranslation(5.3f, 3.7f) * dither,
            "rotation" => Matrix3x2.CreateRotation(0.0071f, centre) * Matrix3x2.CreateTranslation(6f, 6f) * dither,
            "flip" => Matrix3x2.CreateRotation(MathF.PI + 0.003f, centre) * Matrix3x2.CreateTranslation(6f, 6f) * dither,
            "half" => Matrix3x2.CreateScale(0.5f) * Matrix3x2.CreateTranslation(4f, 4f) * dither,
            "double" => Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(4f, 4f) * dither,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    /// <summary>A canvas that holds every transformed frame, plus a margin the deposit must clip at.</summary>
    private static (int CanvasW, int CanvasH) CanvasFor(string name, int frames)
    {
        var maxX = 0f;
        var maxY = 0f;
        for (var f = 0; f < frames; f++)
        {
            var t = TransformFor(name, f);
            foreach (var corner in new[] { new Vector2(0, 0), new Vector2(W, 0), new Vector2(0, H), new Vector2(W, H) })
            {
                var p = Vector2.Transform(corner, t);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
            }
        }
        // A few pixels short of the full extent on purpose, so drops fall off the canvas edge too.
        return ((int)maxX - 3, (int)maxY - 3);
    }

    private static Image Frame(int seed)
    {
        var rng = new Random(seed);
        var plane = new float[H, W];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                plane[y, x] = 800f + (float)(rng.NextDouble() * 400.0);
            }
        }
        // A bright outlier streak the clip has something to reject, and a few dead photosites.
        for (var x = 20; x < 180; x++)
        {
            plane[40 + (x / 9), x] += 5000f * (seed % 3 == 0 ? 1 : 0);
        }
        plane[10, 10] = float.NaN;
        plane[H - 1, W - 1] = float.NaN;
        plane[77, 3] = float.NaN;
        var meta = new ImageMeta { Instrument = "synth-drizzle-bits", SensorType = SensorType.RGGB };
        return new Image([plane], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private static BitMatrix Mask()
    {
        var mask = new BitMatrix(H, W);
        foreach (var (y, x) in new[] { (5, 5), (5, 64), (5, 127), (63, 0), (64, 202), (120, 100), (156, 150) })
        {
            mask[y, x] = true;
        }
        return mask;
    }

    private static int[,] Pattern => SensorType.RGGB.GetBayerPatternMatrix(0, 0);

    [Theory]
    [MemberData(nameof(Transforms))]
    public void TheParallelPlainDeposit_IsTheSerialOne(string transform)
    {
        var (cw, ch) = CanvasFor(transform, 3);
        var serial = (Planes(ch, cw), Planes(ch, cw));
        var parallel = (Planes(ch, cw), Planes(ch, cw));
        var mask = Mask();
        for (var f = 0; f < 3; f++)
        {
            var raw = Frame(f);
            var t = TransformFor(transform, f);
            DrizzleKernel.IterateAndDeposit(raw, t, Pattern, HalfP, serial.Item1, serial.Item2,
                0, cw, 0, ch, new PixelRect(0, 0, W, H), mask, true);
            DrizzleKernel.IterateAndDepositParallel(raw, t, Pattern, HalfP, parallel.Item1, parallel.Item2,
                cw, ch, mask, true);
        }

        ShouldBeBitIdentical(parallel.Item1, serial.Item1, "flux");
        ShouldBeBitIdentical(parallel.Item2, serial.Item2, "weight");
        serial.Item2.Sum(p => p.Cast<float>().Sum()).ShouldBeGreaterThan(0f, "the fixture deposits something");
    }

    [Theory]
    [MemberData(nameof(Transforms))]
    public void TheParallelMomentsSlopeAndClip_AreTheSerialOnes(string transform)
    {
        const int Frames = 12;
        var (cw, ch) = CanvasFor(transform, Frames);
        var mask = Mask();
        var serialMoments = new DrizzleMoments(3, ch, cw);
        var parallelMoments = new DrizzleMoments(3, ch, cw);
        var frames = Enumerable.Range(0, Frames).Select(Frame).ToArray();
        for (var f = 0; f < Frames; f++)
        {
            DrizzleKernel.IterateAndAccumulateMoments(frames[f], TransformFor(transform, f), Pattern, HalfP, serialMoments,
                0, cw, 0, ch, new PixelRect(0, 0, W, H), mask, true);
            DrizzleKernel.IterateAndAccumulateMomentsParallel(frames[f], TransformFor(transform, f), Pattern, HalfP, parallelMoments,
                cw, ch, mask, true);
        }

        ShouldBeBitIdentical(parallelMoments.Sum, serialMoments.Sum, "moment sums");
        ShouldBeBitIdentical(parallelMoments.Weight, serialMoments.Weight, "moment weights");
        ShouldBeBitIdentical(parallelMoments.Squares, serialMoments.Squares, "moment squares");

        // The SIMD, parallel slope against the scalar rule it replaced.
        parallelMoments.ComputeSlope();
        ReferenceSlope(serialMoments);
        ShouldBeBitIdentical(parallelMoments.Slope, serialMoments.Slope, "slope");

        var clip = new DrizzleClip(3f, 5f);
        var serial = (Planes(ch, cw), Planes(ch, cw));
        var parallel = (Planes(ch, cw), Planes(ch, cw));
        long serialRejected = 0, serialTotal = 0, parallelRejected = 0, parallelTotal = 0;
        for (var f = 0; f < Frames; f++)
        {
            var (r, t) = DrizzleKernel.IterateAndDepositClipped(frames[f], TransformFor(transform, f), Pattern, HalfP,
                serialMoments, clip, serial.Item1, serial.Item2, 0, cw, 0, ch, new PixelRect(0, 0, W, H), mask, true);
            serialRejected += r;
            serialTotal += t;
            var (pr, pt) = DrizzleKernel.IterateAndDepositClippedParallel(frames[f], TransformFor(transform, f), Pattern, HalfP,
                parallelMoments, clip, parallel.Item1, parallel.Item2, cw, ch, mask, true);
            parallelRejected += pr;
            parallelTotal += pt;
        }

        ShouldBeBitIdentical(parallel.Item1, serial.Item1, "clipped flux");
        ShouldBeBitIdentical(parallel.Item2, serial.Item2, "clipped weight");
        parallelTotal.ShouldBe(serialTotal);
        parallelRejected.ShouldBe(serialRejected);
        // At scale 2 a one-cell drop lands two canvas pixels from its neighbours, so few frames share a
        // cell and none reaches the weight the clip needs to judge it: nothing to reject, correctly.
        if (transform != "double")
        {
            serialRejected.ShouldBeGreaterThan(0, "the fixture's streak gives the clip something to reject");
        }
    }

    [Fact]
    public void TheSimdFinalDivide_IsTheScalarOne()
    {
        const int Rows = 67;
        const int Cols = 131;
        var rng = new Random(11);
        var flux = Planes(Rows, Cols);
        var weight = Planes(Rows, Cols);
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < Rows; y++)
            {
                for (var x = 0; x < Cols; x++)
                {
                    weight[c][y, x] = rng.Next(5) == 0 ? 0f : (float)rng.NextDouble() * 9f;
                    flux[c][y, x] = (float)rng.NextDouble() * 5000f;
                }
            }
        }

        var expected = flux.Select(p => (float[,])p.Clone()).ToArray();
        long expectedCovered = 0;
        const float InvMax = 1f / 65535f;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < Rows; y++)
            {
                for (var x = 0; x < Cols; x++)
                {
                    var wv = weight[c][y, x];
                    if (wv > 0f)
                    {
                        expected[c][y, x] = expected[c][y, x] / wv * InvMax;
                        expectedCovered++;
                    }
                    else
                    {
                        expected[c][y, x] = float.NaN;
                    }
                }
            }
        }

        DrizzleKernel.FinaliseDivide(flux, weight, InvMax, Rows, Cols).ShouldBe(expectedCovered);
        ShouldBeBitIdentical(flux, expected, "divided flux");
    }

    [Fact]
    public async Task AFusedRun_IsEachSeparateRunBitForBit()
    {
        // The dataset bake's shape: the master over every frame, and an interleaved pair of halves,
        // each with the rejector its own frame count earns (the thresholds differ between 60 and 30).
        const int Frames = 60;
        var ct = TestContext.Current.CancellationToken;
        var (cw, ch) = CanvasFor("rotation", Frames);
        var frames = Enumerable.Range(0, Frames)
            .Select(f => new RawBayerFrame(Frame(f), TransformFor("rotation", f)))
            .ToList();
        var all = Enumerable.Range(0, Frames).ToImmutableArray();
        var even = all.Where(i => i % 2 == 0).ToImmutableArray();
        var odd = all.Where(i => i % 2 == 1).ToImmutableArray();
        var subsets = new[]
        {
            new DrizzleSubset(all, StackingPipeline.BuildRejector(all.Length)),
            new DrizzleSubset(even, StackingPipeline.BuildRejector(even.Length)),
            new DrizzleSubset(odd, StackingPipeline.BuildRejector(odd.Length)),
        };

        var fused = await new DrizzleStrategy().RunSubsetsAsync(Job(frames, null, cw, ch), subsets, ct);

        fused.Length.ShouldBe(3);
        for (var t = 0; t < subsets.Length; t++)
        {
            var pick = subsets[t].Frames;
            var alone = await new DrizzleStrategy().RunAsync(
                Job([.. pick.Select(i => frames[i])], subsets[t].Rejector, cw, ch), ct);
            fused[t].FrameCount.ShouldBe(alone.FrameCount);
            fused[t].DrizzleTotalDeposits.ShouldBe(alone.DrizzleTotalDeposits);
            fused[t].DrizzleRejectedDeposits.ShouldBe(alone.DrizzleRejectedDeposits);
            fused[t].Master.Pedestal.ShouldBe(alone.Master.Pedestal);
            for (var c = 0; c < 3; c++)
            {
                ShouldBeBitIdentical(fused[t].Master.GetChannelArray(c), alone.Master.GetChannelArray(c), $"target {t} master channel {c}");
                ShouldBeBitIdentical(fused[t].Coverage.ShouldNotBeNull().GetChannelArray(c),
                    alone.Coverage.ShouldNotBeNull().GetChannelArray(c), $"target {t} coverage channel {c}");
            }
        }

        fused[0].DrizzleRejectedDeposits.ShouldBeGreaterThan(0, "the master's clip had something to reject");
    }

    [Fact]
    public async Task ATargetNamingAFramePastTheStream_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = Enumerable.Range(0, 4).Select(f => new RawBayerFrame(Frame(f), TransformFor("shift", f))).ToList();
        var (cw, ch) = CanvasFor("shift", 4);
        await Should.ThrowAsync<ArgumentException>(async () => await new DrizzleStrategy().RunSubsetsAsync(
            Job(frames, null, cw, ch), [new DrizzleSubset([0, 2, 9], null)], ct));
    }

    private static IntegrationJob Job(List<RawBayerFrame> frames, IPixelRejector? rejector, int cw, int ch)
    {
        async IAsyncEnumerable<RawBayerFrame> Producer([EnumeratorCancellation] CancellationToken token)
        {
            foreach (var frame in frames)
            {
                token.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        static async IAsyncEnumerable<Image> NoWarped([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }

        return new IntegrationJob(
            WarpedFrames: NoWarped,
            ExpectedFrameCount: frames.Count,
            Options: new IntegrationOptions(Rejector: rejector, ApplyNormalization: false),
            StagingDir: Path.GetTempPath(),
            StatsRect: PixelRect.Empty,
            RawBayerFrames: Producer,
            DrizzleOptions: new DrizzleOptions(),
            CanvasWidth: cw,
            CanvasHeight: ch,
            BadPixelMask: [Mask()]);
    }

    private static float[][,] Planes(int h, int w) => [new float[h, w], new float[h, w], new float[h, w]];

    /// <summary>The slope rule as the scalar code wrote it before the SIMD rewrite.</summary>
    private static void ReferenceSlope(DrizzleMoments moments)
    {
        for (var c = 0; c < moments.Sum.Length; c++)
        {
            var sum = moments.Sum[c];
            var weight = moments.Weight[c];
            var slope = moments.Slope[c];
            var h = sum.GetLength(0);
            var w = sum.GetLength(1);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var wc = weight[y, x];
                    if (wc <= 0f)
                    {
                        continue;
                    }

                    var m = sum[y, x] / wc;
                    var s = 0f;
                    if (x > 0 && weight[y, x - 1] > 0f)
                    {
                        s = MathF.Max(s, MathF.Abs(sum[y, x - 1] / weight[y, x - 1] - m));
                    }

                    if (x < w - 1 && weight[y, x + 1] > 0f)
                    {
                        s = MathF.Max(s, MathF.Abs(sum[y, x + 1] / weight[y, x + 1] - m));
                    }

                    if (y > 0 && weight[y - 1, x] > 0f)
                    {
                        s = MathF.Max(s, MathF.Abs(sum[y - 1, x] / weight[y - 1, x] - m));
                    }

                    if (y < h - 1 && weight[y + 1, x] > 0f)
                    {
                        s = MathF.Max(s, MathF.Abs(sum[y + 1, x] / weight[y + 1, x] - m));
                    }

                    slope[y, x] = s;
                }
            }
        }
    }

    private static void ShouldBeBitIdentical(float[][,] actual, float[][,] expected, string what)
    {
        actual.Length.ShouldBe(expected.Length);
        for (var c = 0; c < actual.Length; c++)
        {
            ShouldBeBitIdentical(actual[c], expected[c], $"{what} channel {c}");
        }
    }

    private static void ShouldBeBitIdentical(float[,] actual, float[,] expected, string what)
    {
        actual.GetLength(0).ShouldBe(expected.GetLength(0));
        actual.GetLength(1).ShouldBe(expected.GetLength(1));
        for (var y = 0; y < actual.GetLength(0); y++)
        {
            for (var x = 0; x < actual.GetLength(1); x++)
            {
                if (BitConverter.SingleToInt32Bits(actual[y, x]) != BitConverter.SingleToInt32Bits(expected[y, x]))
                {
                    throw new ShouldAssertException(
                        $"{what} differs at ({x}, {y}): {actual[y, x]:R} against {expected[y, x]:R}");
                }
            }
        }
    }
}
