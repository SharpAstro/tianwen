using System;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// R9's synthetic training pairs (<see cref="PlanetaryTrainingPairs"/>, #1056, docs/plans/planetary-restoration.md, "R9 revisited"): a
/// twin's PSF file read back binned to the detector and sharpest first, and a pair whose truth is the render through the diffraction
/// alone with no sky, whose stack is the same light through a broader PSF on the sky, drawn the same from the same seed.
/// </summary>
public sealed class PlanetaryTrainingPairsTests : IDisposable
{
    private const int Grid = 32;
    private const int Oversample = 2;
    private const int Detector = Grid / Oversample;

    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // A unit-sum Gaussian of sigmaPx detector pixels on the fine grid, centred on the detector pixel ConvolveCentred takes as the
    // kernel's centre (Detector / 2, whose fine cells are Grid / 2 - 1 and Grid / 2 ... here 16 and 17, so 16.5).
    private static double[] Gaussian(double sigmaPx)
    {
        var sigma = sigmaPx * Oversample;
        var centre = (Oversample * (Detector / 2)) + ((Oversample - 1) / 2.0);
        var kernel = new double[Grid * Grid];
        for (var y = 0; y < Grid; y++)
        {
            for (var x = 0; x < Grid; x++)
            {
                kernel[(y * Grid) + x] = Math.Exp(-(((x - centre) * (x - centre)) + ((y - centre) * (y - centre))) / (2 * sigma * sigma));
            }
        }
        var sum = kernel.Sum();
        return [.. kernel.Select(v => v / sum)];
    }

    // A twin's PSF file: the diffraction a sharp core, the frames broader, the broadest written first.
    private string WritePsfFile()
    {
        var path = Path.Combine(_folders.Create("pairs").FullName, "twin.psf");
        var header = new SyntheticPsfHeader(Grid, Oversample, 0.5, 650e-9, 0, 5, 0, 0, 1, Gaussian(0.6));
        using var writer = new SyntheticPsfFile.Writer(path, header);
        writer.Append(new SyntheticFrameOptics(Gaussian(2.0), 0, 0, 1));
        writer.Append(new SyntheticFrameOptics(Gaussian(1.5), 0, 0, 1));
        return path;
    }

    // Belts and a fine texture, as the sharpening's own tests draw Jupiter, so the gains are derived from a disk with detail in it.
    private static PlanetMap TexturedMap()
    {
        var random = new Random(7);
        var values = new float[180 * 90];
        for (var row = 0; row < 90; row++)
        {
            var latitude = (89 - (2 * row)) * Math.PI / 180;
            for (var column = 0; column < 180; column++)
            {
                values[(row * 180) + column] = (float)(1 - (0.3 * Math.Pow(Math.Sin(4 * latitude), 2)) + (0.15 * (random.NextDouble() - 0.5)));
            }
        }
        return new PlanetMap(values, 180, 90);
    }

    [Fact]
    public void ATwinsPsfFileIsReadBinnedToTheDetectorSharpestFirst()
    {
        var psf = PlanetaryTrainingPairs.Load(WritePsfFile()).ShouldNotBeNull();

        psf.Name.ShouldBe("twin");
        psf.Size.ShouldBe(Detector);
        psf.Frames.Length.ShouldBe(2);
        foreach (var frame in psf.Frames)
        {
            frame.Length.ShouldBe(Detector * Detector);
            frame.Sum().ShouldBe(1f, 1e-4);
        }
        psf.Diffraction.Sum().ShouldBe(1, 1e-6);
        // The 1.5 px frame was written second and is read first: its peak, on the detector's centre, is the higher.
        psf.Frames[0].Max().ShouldBeGreaterThan(psf.Frames[1].Max());
        Array.IndexOf(psf.Frames[0], psf.Frames[0].Max()).ShouldBe(((Detector / 2) * Detector) + (Detector / 2));
    }

    [Fact]
    public void APairsTruthIsTheDiffractionsLightWithNoSkyAndItsStackTheSameLightOnTheSky()
    {
        var psf = PlanetaryTrainingPairs.Load(WritePsfFile()).ShouldNotBeNull();
        var map = TexturedMap();

        var pair = PlanetaryTrainingPairs.Make(map, 0.95, psf, new Random(5), (40, 40), (0.5, 0.5), 0, (1000, 1000)).ShouldNotBeNull();

        pair.Draw.RadiusPx.ShouldBe(40);
        pair.Draw.KeepFraction.ShouldBe(0.5, 1e-12);
        pair.Draw.FinestBandSnr.ShouldBe(1000, 1e-9);
        pair.WavelengthNm.ShouldBe(650, 1e-9);
        var side = pair.Draw.Size;
        (side % 2).ShouldBe(0);
        pair.Stack.Width.ShouldBe(side);
        pair.Truth.Width.ShouldBe(side);
        pair.Sharpened.Width.ShouldBe(side);
        Math.Abs(pair.Placement.CenterX - (side / 2.0)).ShouldBeLessThanOrEqualTo(0.5);

        // Far from the disk the truth is black and the stack its sky; over the frame both carry the disk's whole light, since both PSFs
        // are unit-sum and the frame reaches past the disk by the kernel's half width.
        var stack = pair.Stack.GetChannelSpan(0);
        var truth = pair.Truth.GetChannelSpan(0);
        double stackSky = 0, truthSky = 0, stackLight = 0, truthLight = 0;
        var far = 0;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var i = (y * side) + x;
                stackLight += stack[i] - PlanetaryTrainingPairs.Sky;
                truthLight += truth[i];
                if (double.Hypot(x - pair.Placement.CenterX, y - pair.Placement.CenterY) > 1.4 * 40)
                {
                    stackSky += stack[i];
                    truthSky += truth[i];
                    far++;
                }
            }
        }
        far.ShouldBeGreaterThan(100);
        (truthSky / far).ShouldBe(0, 1e-4);
        (stackSky / far).ShouldBe(PlanetaryTrainingPairs.Sky, 1e-3);
        (stackLight / truthLight).ShouldBe(1, 0.005);

        // The sharpening ran on the stack and changed it.
        pair.Sharpened.GetChannelSpan(0).SequenceEqual(stack).ShouldBeFalse();

        // Pair i draws from its seed alone, so a set can be made again.
        var again = PlanetaryTrainingPairs.Make(map, 0.95, psf, new Random(5), (40, 40), (0.5, 0.5), 0, (1000, 1000)).ShouldNotBeNull();
        again.Stack.GetChannelSpan(0).SequenceEqual(stack).ShouldBeTrue();
        again.Draw.ShouldBe(pair.Draw);
    }
}
