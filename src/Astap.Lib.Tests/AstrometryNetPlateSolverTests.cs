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

    [Fact]
    public async Task GivenStarFieldTestFileWhenBlindPlateSolvingThenItIsSolved()
    {
        // given
        var fileAndDim = await SharedTestData.ExtractTestFitsFileAsync("PlateSolveTestFile");
        if (!fileAndDim.HasValue)
        {
            Assert.Fail("Could not extract test image data");
        }
        var (extractedFitsFile, imageDim, expectedRa, expectedDec) = fileAndDim.Value;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {

            var solver = new AstrometryNetPlateSolver();

            // when
            var solution = await solver.SolveFileAsync(extractedFitsFile, imageDim, cancellationToken: cts.Token);

            // then
            solution.HasValue.ShouldBe(true);
            solution.Value.ra.ShouldBeInRange(expectedRa - double.Epsilon, expectedRa + double.Epsilon);
            solution.Value.dec.ShouldBeInRange(expectedDec - double.Epsilon, expectedDec + double.Epsilon);
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }

    [Fact]
    public async Task GivenStarFieldTestFileAndSearchOriginWhenPlateSolvingThenItIsSolved()
    {
        // given
        var fileAndDim = await SharedTestData.ExtractTestFitsFileAsync("PlateSolveTestFile");
        if (!fileAndDim.HasValue)
        {
            Assert.Fail("Could not extract test image data");
        }
        var (extractedFitsFile, imageDim, expectedRa, expectedDec) = fileAndDim.Value;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var solver = new AstrometryNetPlateSolver();

            // when
            var solution = await solver.SolveFileAsync(extractedFitsFile, imageDim, searchOrigin: (expectedRa, expectedDec), searchRadius: 1d, cancellationToken: cts.Token);

            // then
            solution.HasValue.ShouldBe(true);
            solution.Value.ra.ShouldBeInRange(expectedRa - double.Epsilon, expectedRa + double.Epsilon);
            solution.Value.dec.ShouldBeInRange(expectedDec - double.Epsilon, expectedDec + double.Epsilon);
        }
        finally
        {
            File.Delete(extractedFitsFile);
        }
    }
}
