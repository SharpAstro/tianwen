using System;
using System.IO;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Each alignment point keeping its own best frames (<see cref="PlanetaryStackOptions.PointKeep"/>, #1350, Strata's Warp+): a capture
/// whose left half is sharp in some frames and its right half in the others, never both, so a whole frame's keep takes blurred halves
/// wherever it keeps a frame and each point's own keep need not.
/// </summary>
public class PlanetaryPointKeepTests
{
    private const int N = 128;
    private const int Frames = 24;

    private static float[,] TexturedDisk()
    {
        var a = new float[N, N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var (dx, dy) = (x - (N / 2.0), y - (N / 2.0));
                a[y, x] = (dx * dx) + (dy * dy) < 50 * 50
                    ? (float)(0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3)))
                    : 0.03f;
            }
        }
        return a;
    }

    // A 3x3 box blur, `passes` times, over the columns from `x0` to `x1` only.
    private static float[,] BlurColumns(float[,] src, int x0, int x1, int passes)
    {
        var cur = (float[,])src.Clone();
        for (var p = 0; p < passes; p++)
        {
            var next = (float[,])cur.Clone();
            for (var y = 1; y < N - 1; y++)
            {
                for (var x = Math.Max(1, x0); x < Math.Min(N - 1, x1); x++)
                {
                    float sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            sum += cur[y + dy, x + dx];
                        }
                    }
                    next[y, x] = sum / 9;
                }
            }
            cur = next;
        }
        return cur;
    }

    private static ushort[] ToU16(float[,] a)
    {
        var f = new ushort[N * N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                f[(y * N) + x] = (ushort)(Math.Clamp(a[y, x], 0f, 1f) * 60000);
            }
        }
        return f;
    }

    // The first half of the frames blurred on the right, the second on the left: no frame is sharp on both sides.
    private static float[,] WriteHalfSharpCapture(string path)
    {
        var truth = TexturedDisk();
        var frames = new ushort[Frames][];
        for (var i = 0; i < Frames; i++)
        {
            var blurred = i < Frames / 2 ? BlurColumns(truth, N / 2, N, 3) : BlurColumns(truth, 0, N / 2, 3);
            frames[i] = ToU16(blurred);
        }
        PlanetarySerFixtures.WriteSer(path, N, N, SerColorId.Mono, frames);
        return truth;
    }

    private static double MeanAbsDiff(Image master, float[,] truth, PixelRect region)
    {
        double sum = 0;
        var n = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var v = master[0, y, x];
                if (!float.IsNaN(v))
                {
                    sum += Math.Abs(v - truth[y, x]);
                    n++;
                }
            }
        }
        return n > 0 ? sum / n : double.MaxValue;
    }

    private static readonly PlanetaryStackOptions Options = new() { CropToCoverage = false, KeepFraction = 1.0, PerPointQualityWeighting = false };

    [Fact(Timeout = 120_000)]
    public async Task APointKeepOfEveryFrameStacksAsNoPointKeepDoes()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            WriteHalfSharpCapture(path);
            PlanetaryStackResult none, all;
            using (var s = SerFrameStream.Open(path))
            {
                none = await new LuckyImagingStacker().StackAsync(s, Options, TestContext.Current.CancellationToken);
            }
            using (var s = SerFrameStream.Open(path))
            {
                all = await new LuckyImagingStacker().StackAsync(s, Options with { PointKeep = 1.0 }, TestContext.Current.CancellationToken);
            }

            all.PointKeepCandidates.ShouldBe(Frames);
            all.PointKeptEach.ShouldBe(Frames);
            all.FramesUsed.ShouldBe(none.FramesUsed);
            var a = none.Master.GetChannelSpan(0);
            var b = all.Master.GetChannelSpan(0);
            var worst = 0f;
            for (var i = 0; i < a.Length; i++)
            {
                worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
            }
            worst.ShouldBeLessThan(1e-4f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task EachPointKeepsTheFramesSharpWhereItIs()
    {
        // Half the frames, by the whole frame or by each point: the whole frame's half holds blurred halves on both sides, the points'
        // take each side from the frames sharp there.
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            var truth = WriteHalfSharpCapture(path);
            PlanetaryStackResult framed, pointed;
            using (var s = SerFrameStream.Open(path))
            {
                framed = await new LuckyImagingStacker().StackAsync(s, Options with { KeepFraction = 0.5 }, TestContext.Current.CancellationToken);
            }
            using (var s = SerFrameStream.Open(path))
            {
                pointed = await new LuckyImagingStacker().StackAsync(s, Options with { PointKeep = 0.5 }, TestContext.Current.CancellationToken);
            }

            pointed.PointKeptEach.ShouldBe(Frames / 2);
            var (left, right) = (new PixelRect(24, 44, 28, 40), new PixelRect(76, 44, 28, 40));
            var (framedLeft, framedRight) = (MeanAbsDiff(framed.Master, truth, left), MeanAbsDiff(framed.Master, truth, right));
            var (pointedLeft, pointedRight) = (MeanAbsDiff(pointed.Master, truth, left), MeanAbsDiff(pointed.Master, truth, right));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"mean |master - truth|: the whole frame's half left {framedLeft:0.00000} right {framedRight:0.00000}; each point's half left {pointedLeft:0.00000} right {pointedRight:0.00000}");
            // The whole frame's half favours one side's sharp frames (whichever its grades rank higher), and leaves the other blurred;
            // the points' half takes both sides to the sharp frames' floor.
            Math.Max(pointedLeft, pointedRight).ShouldBeLessThan(Math.Max(framedLeft, framedRight) * 0.8);
            Math.Max(pointedLeft, pointedRight).ShouldBeLessThan(Math.Min(framedLeft, framedRight) * 1.05);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
