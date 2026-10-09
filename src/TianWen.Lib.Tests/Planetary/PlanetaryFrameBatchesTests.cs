using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The batch stack's frames taken a batch at a time (<see cref="PlanetaryFrameBatches"/>): prepared side by side, folded in the order
/// given. A stack's sum gives the same bits only in its own order, so the order is what is pinned, with each frame's preparation its
/// own, every frame released, and the aligner and matcher twins a slot runs on estimating as the originals do.
/// </summary>
public class PlanetaryFrameBatchesTests
{
    private const int N = 96;

    [Fact(Timeout = 30_000)]
    public async Task EveryFrameIsFoldedInTheOrderGivenWithItsOwnPreparationAndReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        // 37 frames, a prime, so the last batch is a short one whatever the core count; each frame's first pixel is its index.
        var stream = new RecordingStream(Enumerable.Range(0, 37).Select(Marked).ToArray());
        var order = Enumerable.Range(0, 37).Reverse().Where(i => i % 5 != 2).ToImmutableArray();
        var folded = new List<(int Index, float Prepared)>();
        var slots = new ConcurrentBag<int>();

        await PlanetaryFrameBatches.RunAsync(stream, order,
            (frame, _, slot) =>
            {
                slots.Add(slot);
                return frame.GetChannelSpan(0)[0];
            },
            (_, index, prepared) => folded.Add((index, prepared)),
            ct);

        folded.Select(f => f.Index).ShouldBe(order, "folded in the order given");
        folded.ShouldAllBe(f => f.Prepared == f.Index, "each fold given its own frame's preparation");
        slots.ShouldAllBe(s => s >= 0 && s < PlanetaryFrameBatches.MaxSlots);
        stream.Loaded.Count.ShouldBe(order.Length, "each frame loaded once");
        stream.Loaded.Count(image => image.TryLease(out _)).ShouldBe(0, "every frame released once folded");
    }

    [Fact(Timeout = 30_000)]
    public async Task ACancelledRunReleasesTheFramesItHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new RecordingStream(Enumerable.Range(0, 40).Select(Marked).ToArray());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var folds = 0;

        await Should.ThrowAsync<OperationCanceledException>(PlanetaryFrameBatches.RunAsync(stream, Enumerable.Range(0, 40).ToImmutableArray(),
            (frame, _, _) => frame.GetChannelSpan(0)[0],
            (_, _, _) =>
            {
                if (++folds == 3)
                {
                    cancel.Cancel();
                }
            },
            cancel.Token));

        stream.Loaded.Count.ShouldBeGreaterThan(0);
        stream.Loaded.Count(image => image.TryLease(out _)).ShouldBe(0, "a run that stops gives back what it loaded");
    }

    [Fact]
    public void ABatchHoldsAFrameACoreUntilItsFramesWouldPassTheBudget()
    {
        PlanetaryFrameBatches.BatchSize(800, 600, 1).ShouldBe(Math.Min(PlanetaryFrameBatches.MaxSlots, 69));
        // A 24-megapixel colour frame is 288 MB as floats: one at a time.
        PlanetaryFrameBatches.BatchSize(6000, 4000, 3).ShouldBe(1);
    }

    [Fact]
    public void AnAlignerTwinEstimatesExactlyAsItsOriginal()
    {
        var reference = Image.FromChannel(Disk(48, 48));
        var aligner = GlobalAligner.FromReference(reference, PlanetaryDisk.BoundingBox(reference), 64);
        var twin = aligner.Twin();
        foreach (var (dx, dy) in new[] { (0.3, -1.2), (2.6, 0.4), (-1.7, 1.9) })
        {
            var frame = Image.FromChannel(Disk(48 + dx, 48 + dy));
            var box = PlanetaryDisk.BoundingBox(frame);
            var (a, b) = (aligner.Estimate(frame, box), twin.Estimate(frame, box));
            (b.Dx, b.Dy, b.PeakValue).ShouldBe((a.Dx, a.Dy, a.PeakValue));
        }
    }

    [Fact]
    public void AMatcherTwinBuildsTheSameMeshAsItsOriginal()
    {
        var reference = Image.FromChannel(Disk(48, 48));
        var box = PlanetaryDisk.BoundingBox(reference);
        var matcher = AlignmentPointMatcher.FromReference(reference, FeatureDetector.DetectAlignmentPoints(reference, box, 16, 16), 16, whiten: false);
        var twin = matcher.Twin();
        var frame = Image.FromChannel(Disk(48.6, 47.3));
        var (a, b) = (new AlignmentPointShift[matcher.AlignmentPoints.Length], new AlignmentPointShift[matcher.AlignmentPoints.Length]);

        matcher.Match(frame, 0.6f, -0.7f, a);
        twin.Match(frame, 0.6f, -0.7f, b);

        a.Length.ShouldBeGreaterThan(0);
        b.ShouldBe(a);
    }

    [Fact(Timeout = 30_000)]
    public async Task GradingInBatchesGivesEachFrameItsOwnGrade()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = Enumerable.Range(0, 23).Select(i => Disk(48 + (0.1 * i), 48, blur: i % 4)).ToArray();
        var estimator = new GradientEnergyEstimator();

        var grades = await new FrameGrader(estimator).GradeAllAsync(new InMemoryFrameStream(frames), cancellationToken: ct);

        grades.Select(g => g.Index).ShouldBe(Enumerable.Range(0, 23));
        for (var i = 0; i < frames.Length; i++)
        {
            grades[i].Score.ShouldBe(FrameGrader.Grade(estimator, Image.FromChannel(frames[i])), $"frame {i}");
        }
    }

    // A frame whose first pixel says which it is.
    private static float[,] Marked(int index)
    {
        var plane = new float[8, 8];
        plane[0, 0] = index;
        return plane;
    }

    // A textured disk, softened by a box blur of the given radius (0 for none).
    private static float[,] Disk(double cx, double cy, int blur = 0)
    {
        var a = new float[N, N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var (dx, dy) = (x - cx, y - cy);
                a[y, x] = (dx * dx) + (dy * dy) < 30 * 30
                    ? (float)(0.5 + (0.25 * Math.Sin(x * 0.45) * Math.Cos(y * 0.40)) + (0.12 * Math.Sin((x + y) * 0.22)))
                    : 0.02f;
            }
        }
        if (blur == 0)
        {
            return a;
        }

        var b = new float[N, N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var (sum, count) = (0f, 0);
                for (var yy = Math.Max(0, y - blur); yy <= Math.Min(N - 1, y + blur); yy++)
                {
                    for (var xx = Math.Max(0, x - blur); xx <= Math.Min(N - 1, x + blur); xx++)
                    {
                        sum += a[yy, xx];
                        count++;
                    }
                }
                b[y, x] = sum / count;
            }
        }
        return b;
    }

    // An in-memory stream that keeps every image it hands out, to ask afterwards whether each was released.
    private sealed class RecordingStream(float[][,] frames) : IPlanetaryFrameStream
    {
        public ConcurrentBag<Image> Loaded { get; } = [];

        public int FrameCount => frames.Length;

        public int Width => frames[0].GetLength(1);

        public int Height => frames[0].GetLength(0);

        public PlanetaryFrameLayout Layout => PlanetaryFrameLayout.Mono;

        public bool HasTimestamps => false;

        public DateTimeOffset? TimestampOf(int index) => null;

        public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
        {
            var image = Image.FromChannel(frames[index].Copy(), 1f, 0f);
            Loaded.Add(image);
            return ValueTask.FromResult(image);
        }

        public void Dispose()
        {
        }
    }
}
