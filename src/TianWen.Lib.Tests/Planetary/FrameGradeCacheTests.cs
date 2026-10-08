using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A capture file's frame grades kept between stacks (<see cref="FrameGradeCache"/>, #1351): a warm cache gives the grades a cold one
/// gave, bit for bit; a file that changed is graded again; a session reads its files' grades and decides its own run's flags.
/// </summary>
public sealed class FrameGradeCacheTests : IDisposable
{
    private const int N = 64;
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // A textured disk a little blurred by frame, so the grades differ from frame to frame.
    private static ushort[][] Frames(int count, int seed)
    {
        var rng = new Random(seed);
        var frames = new ushort[count][];
        for (var f = 0; f < count; f++)
        {
            var blur = rng.NextDouble();
            var frame = new ushort[N * N];
            for (var y = 0; y < N; y++)
            {
                for (var x = 0; x < N; x++)
                {
                    var (dx, dy) = (x - (N / 2.0), y - (N / 2.0));
                    var inside = (dx * dx) + (dy * dy) < 22 * 22;
                    var texture = 0.5 + (0.25 * (1 - blur) * Math.Sin(x * 0.7) * Math.Cos(y * 0.6));
                    frame[(y * N) + x] = (ushort)((inside ? texture : 0.03) * 60000);
                }
            }
            frames[f] = frame;
        }
        return frames;
    }

    private string Capture(string folder, string name, int count, int seed)
    {
        var path = Path.Combine(folder, name);
        PlanetarySerFixtures.WriteSer(path, N, N, SerColorId.Mono, Frames(count, seed));
        return path;
    }

    [Fact(Timeout = 60_000)]
    public async Task AWarmCacheGivesTheColdGradesBitForBit()
    {
        var folder = _folders.Create("grade-cache").FullName;
        var capture = Capture(folder, "a.ser", 12, 3);
        var cache = new FrameGradeCache(Path.Combine(folder, "cache"));
        var grader = new FrameGrader(new GradientEnergyEstimator()) { Cache = cache };
        var ct = TestContext.Current.CancellationToken;

        FrameGrade[] fresh, cold, warm;
        using (var s = SerFrameStream.Open(capture))
        {
            fresh = [.. await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(s, cancellationToken: ct)];
        }
        using (var s = SerFrameStream.Open(capture))
        {
            cold = [.. await grader.GradeAllAsync(s, cancellationToken: ct)];
        }
        Directory.GetFiles(cache.Directory, "*.grades").Length.ShouldBe(1);
        using (var s = SerFrameStream.Open(capture))
        {
            warm = [.. await grader.GradeAllAsync(s, cancellationToken: ct)];
        }

        cold.ShouldBe(fresh);
        warm.ShouldBe(fresh);
        fresh.Select(g => g.Score).Distinct().Count().ShouldBeGreaterThan(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task AChangedCaptureOrAnotherEstimatorIsGradedAgain()
    {
        var folder = _folders.Create("grade-cache").FullName;
        var capture = Capture(folder, "a.ser", 8, 3);
        var cache = new FrameGradeCache(Path.Combine(folder, "cache"));
        var ct = TestContext.Current.CancellationToken;
        using (var s = SerFrameStream.Open(capture))
        {
            await new FrameGrader(new GradientEnergyEstimator()) { Cache = cache }.GradeAllAsync(s, cancellationToken: ct);
        }

        // Another estimator keeps its own grades.
        using (var s = SerFrameStream.Open(capture))
        {
            var laplacian = await new FrameGrader(new LaplacianEnergyEstimator()) { Cache = cache }.GradeAllAsync(s, cancellationToken: ct);
            using var again = SerFrameStream.Open(capture);
            laplacian.ShouldBe(await new FrameGrader(new LaplacianEnergyEstimator()).GradeAllAsync(again, cancellationToken: ct));
        }
        Directory.GetFiles(cache.Directory, "*.grades").Length.ShouldBe(2);

        // The same file name with other frames is graded again, never read back.
        File.Delete(capture);
        Capture(folder, "a.ser", 8, 11);
        File.SetLastWriteTimeUtc(capture, DateTime.UtcNow.AddMinutes(1));
        using (var s = SerFrameStream.Open(capture))
        {
            var regraded = await new FrameGrader(new GradientEnergyEstimator()) { Cache = cache }.GradeAllAsync(s, cancellationToken: ct);
            using var fresh = SerFrameStream.Open(capture);
            regraded.ShouldBe(await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(fresh, cancellationToken: ct));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ASessionReadsItsFilesGradesAndGradesOnlyTheNewOne()
    {
        var folder = _folders.Create("grade-cache").FullName;
        var night = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        string Timed(string name, int count, int seed, double minutes)
        {
            var path = Path.Combine(folder, name);
            var times = Enumerable.Range(0, count).Select(i => night.AddMinutes(minutes).AddSeconds(i)).ToArray();
            PlanetarySerFixtures.WriteSer(path, N, N, SerColorId.Mono, Frames(count, seed), times);
            return path;
        }
        var (first, second) = (Timed("first.ser", 6, 3, 0), Timed("second.ser", 6, 5, 2));
        var cache = new FrameGradeCache(Path.Combine(folder, "cache"));
        var ct = TestContext.Current.CancellationToken;

        // The first file alone, cached; then the session of both reads it and grades the second.
        using (var s = SerFrameStream.Open(first))
        {
            await new FrameGrader(new GradientEnergyEstimator()) { Cache = cache }.GradeAllAsync(s, cancellationToken: ct);
        }
        FrameGrade[] session, fresh;
        using (var s = PlanetaryFrameSequence.OpenSer([first, second]))
        {
            session = [.. await new FrameGrader(new GradientEnergyEstimator()) { Cache = cache }.GradeAllAsync(s, cancellationToken: ct)];
        }
        using (var s = PlanetaryFrameSequence.OpenSer([first, second]))
        {
            fresh = [.. await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(s, cancellationToken: ct)];
        }

        session.ShouldBe(fresh);
        Directory.GetFiles(cache.Directory, "*.grades").Length.ShouldBe(2);
    }
}
