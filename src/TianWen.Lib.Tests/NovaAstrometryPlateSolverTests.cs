using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The blind solve of last resort through nova.astrometry.net (<see cref="NovaAstrometryPlateSolver"/>): what it uploads (the
/// frame median-binned, so a hot pixel is no star), how its answer on the binned frame becomes the full frame's, and that it
/// is offered only once a key is stored. The live round trip is a probe, run by hand with a key.
/// </summary>
public class NovaAstrometryPlateSolverTests(ITestOutputHelper output)
{
    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = [];

        public string? Get(string key) => _secrets.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string? value)
        {
            if (value is null)
            {
                _secrets.Remove(key);
            }
            else
            {
                _secrets[key] = value;
            }
        }
    }

    private static NovaAstrometryPlateSolver Solver(ICredentialStore? store)
        => new(store, SystemTimeProvider.Instance, NullLogger<NovaAstrometryPlateSolver>.Instance);

    [Fact]
    public async Task ItIsOfferedOnlyOnceAKeyIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryCredentialStore();

        (await Solver(null).CheckSupportAsync(ct)).ShouldBeFalse("a host with no credential store");
        (await Solver(store).CheckSupportAsync(ct)).ShouldBeFalse("no key stored");
        store.Set(NovaAstrometryPlateSolver.CredentialKey, "0123456789abcdef");
        (await Solver(store).CheckSupportAsync(ct)).ShouldBeTrue();
        Solver(store).Priority.ShouldBeLessThan(0.1f, "after every local solver");
    }

    [Theory]
    [InlineData(3840, 2160, 4)]
    [InlineData(1024, 768, 1)]
    [InlineData(1025, 768, 2)]
    [InlineData(6248, 4176, 7)]
    public void TheUploadKeepsItsLongerSideNearAThousandPixels(int width, int height, int expected)
        => NovaAstrometryPlateSolver.BinFactorFor(width, height).ShouldBe(expected);

    [Fact]
    public void AMedianBinDropsAHotPixelAndKeepsAStar()
    {
        // At 1780 mm a mean bin of a frame with one hot pixel at full scale lifted its block by thousands: a star to any detector.
        var plane = new float[16, 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                plane[y, x] = 100f;
            }
        }
        plane[1, 2] = 65520f;
        // A star several pixels across, in the block at (8..11, 8..11).
        for (var y = 7; y < 13; y++)
        {
            for (var x = 7; x < 13; x++)
            {
                plane[y, x] = 3000f;
            }
        }
        var image = new Image([plane], BitDepth.Float32, 65520f, 100f, 0f, default);

        var binned = NovaAstrometryPlateSolver.MedianBin(image, 4);

        (binned.GetLength(0), binned.GetLength(1)).ShouldBe((4, 4));
        binned[0, 0].ShouldBe(100f, "the hot pixel is one of sixteen and does not move the median");
        binned[2, 2].ShouldBe(3000f, "the star fills its block");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public void ASolutionOfTheBinnedFrameNamesTheSameSkyAtTheFullFramesPixel(int bin)
    {
        // A binned pixel's centre is the centre of the full pixels it covers; the sky there is the same in both solutions.
        var binned = new WCS(0.2138, -2.4153) { CRPix1 = 479.5, CRPix2 = 269.5, CD1_1 = 0, CD1_2 = 0.2316 * bin / 3600, CD2_1 = 0.2316 * bin / 3600, CD2_2 = 0 };
        var full = NovaAstrometryPlateSolver.UnbinFrom(binned, bin);

        foreach (var (bx, by) in new[] { (0.0, 0.0), (479.5, 269.5), (900.0, 30.0) })
        {
            var (fx, fy) = (bx * bin + (bin - 1) / 2.0, by * bin + (bin - 1) / 2.0);
            var a = binned.PixelToSky(bx, by).ShouldNotBeNull();
            var b = full.PixelToSky(fx, fy).ShouldNotBeNull();
            a.RA.ShouldBe(b.RA, 1e-9, $"RA at binned ({bx}, {by})");
            a.Dec.ShouldBe(b.Dec, 1e-9, $"Dec at binned ({bx}, {by})");
        }
        full.PixelScaleArcsec.ShouldBe(0.2316, 1e-9);
    }

    /// <summary>
    /// The live round trip, by hand: <c>TIANWEN_NOVA_PROBE_FITS</c> names a frame and <c>TIANWEN_NOVA_PROBE_KEY_FILE</c> a file
    /// holding the key. Sends the frame to nova.astrometry.net, so it never runs on CI.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task Probe_SolvesAFrameBlindOnNova()
    {
        var fits = Environment.GetEnvironmentVariable("TIANWEN_NOVA_PROBE_FITS");
        var keyFile = Environment.GetEnvironmentVariable("TIANWEN_NOVA_PROBE_KEY_FILE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(fits) || string.IsNullOrWhiteSpace(keyFile), "TIANWEN_NOVA_PROBE_FITS and TIANWEN_NOVA_PROBE_KEY_FILE not set");
        var store = new MemoryCredentialStore();
        store.Set(NovaAstrometryPlateSolver.CredentialKey, (await File.ReadAllTextAsync(keyFile!, TestContext.Current.CancellationToken)).Trim());

        var result = await Solver(store).SolveFileAsync(fits!, cancellationToken: TestContext.Current.CancellationToken);

        var solution = result.Solution.ShouldNotBeNull("nova found no solution");
        output.WriteLine($"{Path.GetFileName(fits)}: {solution.CenterRA:F5}h {solution.CenterDec:F4} deg, {solution.PixelScaleArcsec:F4}\"/px, "
            + $"in {result.Elapsed.TotalSeconds:F0} s");
        var centre = solution.PixelToSky(1919.5, 1079.5).ShouldNotBeNull();
        output.WriteLine($"frame centre {centre.RA:F5}h {centre.Dec:F4} deg");
    }
}
