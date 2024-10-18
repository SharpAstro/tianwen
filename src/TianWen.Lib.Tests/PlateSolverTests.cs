using TianWen.Lib.Astrometry.PlateSolve;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

public class PlateSolverTests
{
    [SkippableTheory]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverUnix))]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstapPlateSolver))]
    public async Task GivenStarFieldTestFileWhenBlindPlateSolvingThenItIsSolved(string name, Type plateSolver, double accuracy = 0.01)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name);
        var cts = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromHours(10) : TimeSpan.FromSeconds(10));

        try
        {
            var solver = (Activator.CreateInstance(plateSolver) as IPlateSolver).ShouldNotBeNull();
            var platform = Environment.OSVersion.Platform;

            Skip.If(plateSolver.IsAssignableTo(typeof(AstrometryNetPlateSolver))
                && platform == PlatformID.Win32NT
                && !Debugger.IsAttached,
                $"Is multi-platform and running on Windows without debugger (Windows is skipped by default as WSL has a long cold start time)");

            Skip.IfNot(await solver.CheckSupportAsync(), $"Platform {platform} is not supported!");

            if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
            {
                // when
                var solution = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.ImageDim, cancellationToken: cts.Token);

                // then
                solution.HasValue.ShouldBe(true);
                var (ra, dec) = solution.Value;
                ra.ShouldBeInRange(dimAndCoords.WCS.CenterRA - accuracy, dimAndCoords.WCS.CenterRA + accuracy);
                dec.ShouldBeInRange(dimAndCoords.WCS.CenterDec - accuracy, dimAndCoords.WCS.CenterDec + accuracy);
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

    [SkippableTheory]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("PlateSolveTestFile", typeof(AstrometryNetPlateSolverUnix))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstrometryNetPlateSolverMultiPlatform))]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", typeof(AstapPlateSolver))]
    public async Task GivenStarFieldTestFileAndSearchOriginWhenPlateSolvingThenItIsSolved(string name, Type plateSolver, double accuracy = 0.01)
    {
        // given
        var extractedFitsFile = await SharedTestData.ExtractGZippedFitsFileAsync(name);
        var cts = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromHours(10) : TimeSpan.FromSeconds(10));

        try
        {
            var solver = (Activator.CreateInstance(plateSolver) as IPlateSolver).ShouldNotBeNull();
            var platform = Environment.OSVersion.Platform;

            Skip.If(plateSolver.IsAssignableTo(typeof(AstrometryNetPlateSolver))
                && platform == PlatformID.Win32NT
                && !Debugger.IsAttached,
                $"Is multi-platform and running on Windows without debugger (Windows is skipped by default as WSL has a long cold start time)");
            Skip.IfNot(await solver.CheckSupportAsync(), $"Platform {platform} is not supported!");

            if (SharedTestData.TestFileImageDimAndCoords.TryGetValue(name, out var dimAndCoords))
            {
                // when
                var solution = await solver.SolveFileAsync(extractedFitsFile, dimAndCoords.ImageDim, searchOrigin: dimAndCoords.WCS, searchRadius: 1d, cancellationToken: cts.Token);

                // then
                solution.HasValue.ShouldBe(true);
                var (ra, dec) = solution.Value;
                ra.ShouldBeInRange(dimAndCoords.WCS.CenterRA - accuracy, dimAndCoords.WCS.CenterRA + accuracy);
                dec.ShouldBeInRange(dimAndCoords.WCS.CenterDec - accuracy, dimAndCoords.WCS.CenterDec + accuracy);
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
