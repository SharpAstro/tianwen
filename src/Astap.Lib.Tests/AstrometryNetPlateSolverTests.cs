using Astap.Lib.Astrometry.PlateSolve;
using Shouldly;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class AstrometryNetPlateSolverTests
{
    static async Task<(string filePath, ImageDim imageDim, double ra, double dec)?> ExtractTestFitsFileAsync()
    {
        var assembly = typeof(AstrometryNetPlateSolver).Assembly;
        var gzippedTestFile = assembly.GetManifestResourceNames().FirstOrDefault(p => p.EndsWith(".PlateSolveTestFile.fits.gz"));

        if (gzippedTestFile is not null && assembly.GetManifestResourceStream(gzippedTestFile) is Stream inStream)
        {
            var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D") + ".fits");
            using var outStream = new FileStream(fileName, new FileStreamOptions
            {
                Options = FileOptions.Asynchronous,
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.None
            });
            using var gzipStream = new GZipStream(inStream, CompressionMode.Decompress, false);
            await gzipStream.CopyToAsync(outStream, 1024 * 10);

            return (fileName, new ImageDim(4.38934f, 1280, 960), 1.6955879753, -31.6142968611);
        }

        return default;
    }

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
        var fileAndDim = await ExtractTestFitsFileAsync();
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
        var fileAndDim = await ExtractTestFitsFileAsync();
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
