using Astap.Lib.Astrometry.PlateSolve;
using Shouldly;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class AstrometryNetPlateSolverTests
{
    [Fact]
    public async Task GivenPlateSolverWhenCheckSupportThenItIsTrue()
    {
        // given
        var solver = new AstrometryNetPlateSolver();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // when
        var actualSupported = await solver.CheckSupportAsync(cancellationToken: cts.Token);

        // then
        actualSupported.ShouldBeTrue();
    }

    [Theory]
    [InlineData("PlateSolveTestFile")]
    public async Task GivenStarFieldTestFileWhenBlindPlateSolvingThenItIsSolved(string name)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var solver = new AstrometryNetPlateSolver();

            if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
            {
                // when
                var solution = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.imageDim, cancellationToken: cts.Token);

                // then
                solution.HasValue.ShouldBe(true);
                solution.Value.ra.ShouldBeInRange(dimAndCoords.ra - double.Epsilon, dimAndCoords.ra + double.Epsilon);
                solution.Value.dec.ShouldBeInRange(dimAndCoords.dec - double.Epsilon, dimAndCoords.dec + double.Epsilon);
            }
            else
            {
                Assert.Fail($"Could not extract test image dimensions for {name}");
            }
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }

    [Theory]
    [InlineData("PlateSolveTestFile")]
    public async Task GivenStarFieldTestFileAndSearchOriginWhenPlateSolvingThenItIsSolved(string name)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var solver = new AstrometryNetPlateSolver();

            if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
            {
                // when
                var solution = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.imageDim, searchOrigin: (dimAndCoords.ra, dimAndCoords.dec), searchRadius: 1d, cancellationToken: cts.Token);

                // then
                solution.HasValue.ShouldBe(true);
                solution.Value.ra.ShouldBeInRange(dimAndCoords.ra - double.Epsilon, dimAndCoords.ra + double.Epsilon);
                solution.Value.dec.ShouldBeInRange(dimAndCoords.dec - double.Epsilon, dimAndCoords.dec + double.Epsilon);
            }
            else
            {
                Assert.Fail($"Could not extract test image dimensions for {name}");
            }
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }
}
