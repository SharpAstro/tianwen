using System;
using System.Collections.Immutable;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class FrameGraderTests
{
    private static ImmutableArray<FrameGrade> Grades(params (int Index, float Score)[] g)
        => [.. g.Select(t => new FrameGrade(t.Index, t.Score))];

    [Fact]
    public void SortByQuality_orders_best_first_ties_by_index()
    {
        var sorted = FrameGrader.SortByQuality(Grades((0, 1f), (1, 5f), (2, 5f), (3, 2f)));

        sorted.Select(g => g.Index).ShouldBe([1, 2, 3, 0]); // 5,5 (tie -> lower index first), then 2, then 1
    }

    [Fact]
    public void SelectBest_keeps_top_fraction_best_first()
    {
        var grades = Grades((0, 1f), (1, 9f), (2, 3f), (3, 7f), (4, 5f), (5, 2f), (6, 8f), (7, 4f));

        FrameGrader.SelectBest(grades, 0.25).ShouldBe([1, 6]);           // top 25% of 8 = 2 frames: scores 9, 8
        FrameGrader.SelectBest(grades, 0.5).ShouldBe([1, 6, 3, 4]);      // top 50% = 4 frames: 9, 8, 7, 5
    }

    [Fact]
    public void SelectBest_always_keeps_at_least_one()
    {
        FrameGrader.SelectBest(Grades((0, 1f), (1, 2f), (2, 3f)), 0.01).ShouldBe([2]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    public void SelectBest_rejects_out_of_range_fraction(double fraction)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => FrameGrader.SelectBest(Grades((0, 1f)), fraction));
    }

    [Fact]
    public void Reference_returns_single_best_index()
    {
        FrameGrader.Reference(Grades((0, 1f), (1, 9f), (2, 3f))).ShouldBe(1);
        FrameGrader.Reference(ImmutableArray<FrameGrade>.Empty).ShouldBe(-1);
    }

    [Fact]
    public async Task GradeAllAsync_ranks_sharpest_frame_as_reference()
    {
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            const int w = 32, h = 32;
            var sharp = PlanetarySerFixtures.Checker(w, h);
            var soft = PlanetarySerFixtures.Blur(sharp, w, h, passes: 1);
            var softest = PlanetarySerFixtures.Blur(sharp, w, h, passes: 4);

            // Deliberately out of quality order on disk (index 0 = softest) so the grader must re-rank.
            PlanetarySerFixtures.WriteSer(path, w, h, SerColorId.Mono, [softest, soft, sharp]);

            using var stream = SerFrameStream.Open(path);
            var grader = new FrameGrader(new LaplacianEnergyEstimator());

            var grades = await grader.GradeAllAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

            grades.Length.ShouldBe(3);
            FrameGrader.Reference(grades).ShouldBe(2);                 // the sharp checkerboard
            FrameGrader.SelectBest(grades, 1.0 / 3).ShouldBe([2]);      // best third = the sharp frame
            FrameGrader.SortByQuality(grades).Select(g => g.Index).ShouldBe([2, 1, 0]); // sharp, soft, softest
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
