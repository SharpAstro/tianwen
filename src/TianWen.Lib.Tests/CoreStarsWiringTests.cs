using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Cli;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Stacking;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The catalogued stars of a frame on their way to <see cref="CoreStars"/> (#2257): through the star split, through the
/// recipe, and through the two verbs that read them for a plate-solved master. <see cref="CoreStarsTests"/> holds what is
/// done with them; these hold that they arrive.
/// </summary>
[Collection("Imaging")]
public sealed class CoreStarsWiringTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    /// <summary>Returns a fixed plate on whatever scale it is handed the input on.</summary>
    private sealed class FixedRemover(float[,] model, float inputPeak) : IStarRemover
    {
        public string Name => "Test/Fixed";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var scale = input.GetChannelSpan(0).ToArray().Where(float.IsFinite).Max() / inputPeak;
            var plane = new float[input.Height, input.Width];
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    plane[y, x] = model[y, x] * scale;
                }
            }
            return Task.FromResult(new Image([plane], BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>Takes every pixel to the mean of its 3 by 3 neighbourhood, as a denoiser flattens a star.</summary>
    private sealed class MeanDenoiser : IDenoiseEnhancer
    {
        public string Name => "Test/Mean";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => EnhanceAsync(input, EnhanceOptions.Default, null, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            var (channels, width, height) = input.Shape;
            var planes = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var src = input.GetChannelSpan(c);
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var sum = 0f;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                sum += src[(Math.Clamp(y + dy, 0, height - 1) * width) + Math.Clamp(x + dx, 0, width - 1)];
                            }
                        }
                        plane[y, x] = sum / 9f;
                    }
                }
                planes[c] = plane;
            }
            return Task.FromResult(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, input.ImageMeta));
        }
    }

    private static float PeakOf(float[,] frame) => frame.Cast<float>().Max();

    [Fact]
    public async Task TheSplitLeavesACataloguedStarInsideACoreInTheStars()
    {
        var ct = TestContext.Current.CancellationToken;
        var (frame, model, catalogue) = CoreStarsTests.Scene();
        var input = CoreStarsTests.ImageOf(frame);
        var remover = new FixedRemover(model, PeakOf(frame));
        var (sx, sy, _) = CoreStarsTests.CoreStarsAt[0];
        var taken = frame[sy, sx] - model[sy, sx];

        var (_, without, _, _) = await NarrowbandCombination.SplitStarsAsync([input], remover, ExtendedCoreMask.Options.Default, fillDigs: true, ct);
        var (starless, with, cores, _) = await NarrowbandCombination.SplitStarsAsync(
            [input], remover, ExtendedCoreMask.Options.Default, fillDigs: true, catalogue, ct);

        (without[0][0, sy, sx] / taken).ShouldBe(0f, 1e-3f, "without a catalogue the star inside the core stays in the starless plate with it");
        (with[0][0, sy, sx] / taken).ShouldBe(1f, 1e-3f, "with one the star is in the stars");
        with[0][0, CoreStarsTests.Cy, CoreStarsTests.Cx].ShouldBe(0f, 1e-6f, "and the nucleus is not");
        CoreStarsTests.Restored(starless[0], frame, model, CoreStarsTests.Cx, CoreStarsTests.Cy).ShouldBe(1.0, 1e-3);
        cores.ShouldNotBeNull();
        cores[0].StarsLeftOut.ShouldNotBeNull().Stars.ShouldBe(CoreStarsTests.CoreStarsAt.Length);
    }

    [Fact]
    public async Task TheRecipeKeepsACataloguedStarInsideACoreOutOfTheDenoise()
    {
        // The recipe puts the stars back last, so where a star went shows only in what happened to it meanwhile: a star in
        // the stars plate is never denoised, one left in the starless plate with its core is.
        var ct = TestContext.Current.CancellationToken;
        var (frame, model, catalogue) = CoreStarsTests.Scene();
        Image[] masters = [CoreStarsTests.ImageOf(frame), CoreStarsTests.ImageOf(frame), CoreStarsTests.ImageOf(frame)];
        var roles = new ColourCompositionRoles(new FixedRemover(model, PeakOf(frame)), null, new MeanDenoiser());
        var options = new ColourCompositionOptions { LinearFitTo = null, Starless = true, Denoise = true };
        var (sx, sy, _) = CoreStarsTests.CoreStarsAt[0];
        var taken = frame[sy, sx] - model[sy, sx];

        var without = await ColourComposition.RunAsync(masters, options, roles, output.WriteLine, ct);
        var with = await ColourComposition.RunAsync(masters, options with { CatalogStars = catalogue }, roles, output.WriteLine, ct);

        foreach (var (name, a, b) in new[] { ("red", without.Red, with.Red), ("green", without.Green, with.Green), ("blue", without.Blue, with.Blue) })
        {
            var (flattened, whole) = ((frame[sy, sx] - a[0, sy, sx]) / taken, (frame[sy, sx] - b[0, sy, sx]) / taken);
            output.WriteLine($"{name}: the star's peak lost {flattened:P1} of its height without the catalogue, {whole:P1} with it");
            flattened.ShouldBeGreaterThan(0.1f, $"{name}: without a catalogue the star is denoised with the core it stayed in");
            whole.ShouldBe(0f, 0.01f, $"{name}: with one it went to the stars and came back as it was");
        }
    }

    // A frame of real sky as the embedded catalogue has it, 1.5 degrees a side about the Lagoon at 6 arcseconds a pixel:
    // every catalogued star drawn at its place with a peak that follows its magnitude, a galaxy with a nucleus as narrow as
    // a star drawn beside one of them, the plate of a remover that took every compact thing, where the two stand, and the
    // line a verb says when the catalogue reached it.
    private const int SkySide = 900;
    private const float SkyLevel = 0.002f;
    private const float SkyNoise = 1e-5f;

    private async Task<(float[,] Frame, float[,] Model, WCS Wcs, (int X, int Y) Nucleus, (int X, int Y) Star, string Found)> SkySceneAsync(ICelestialObjectDB db, CancellationToken ct)
    {
        const double scale = 6.0 / 3600;
        var wcs = new WCS(18.06, -24.38) { CRPix1 = (SkySide - 1) / 2.0, CRPix2 = (SkySide - 1) / 2.0, CD1_1 = -scale, CD1_2 = 0, CD2_1 = 0, CD2_2 = scale };
        var (read, why) = await CatalogFrameStars.ReadAsync(wcs, SkySide, SkySide, installed: null, embedded: db, ct);
        var stars = read.ShouldNotBeNull(why).Stars;

        // The star the galaxy is drawn beside: the brightest of magnitude 9.5 to 11 well inside the frame with no other
        // catalogued star within 40 px, so the core holds this one star and its nucleus has no star on it.
        var star = stars
            .Where(s => s.Magnitude is >= 9.5f and <= 11f && s.X > 200 && s.Y > 200 && s.X < SkySide - 200 && s.Y < SkySide - 200)
            .Where(s => !stars.Any(o => o != s && Math.Pow(o.X - s.X, 2) + Math.Pow(o.Y - s.Y, 2) < 40 * 40))
            .OrderBy(s => s.Magnitude).ThenBy(s => s.X)
            .First();
        var (starX, starY) = ((int)MathF.Round(star.X), (int)MathF.Round(star.Y));
        (int X, int Y) nucleus = (starX - 10, starY + 6);
        output.WriteLine($"{stars.Count} catalogued stars on the frame; the galaxy beside the one of magnitude {star.Magnitude:F2} at ({star.X:F1}, {star.Y:F1})");

        var rng = new Random(23);
        var frame = new float[SkySide, SkySide];
        var model = new float[SkySide, SkySide];
        for (var y = 0; y < SkySide; y++)
        {
            for (var x = 0; x < SkySide; x++)
            {
                var r2 = ((x - nucleus.X) * (x - nucleus.X)) + ((y - nucleus.Y) * (y - nucleus.Y));
                var smooth = SkyLevel + (float)(400 * SkyNoise * Math.Exp(-r2 / (2.0 * 60 * 60))) + (float)(SkyNoise * StarInjection.Gaussian(rng));
                frame[y, x] = smooth;
                model[y, x] = smooth;
            }
        }
        // 300 sigmas for the star beside the nucleus, and a magnitude per factor 2.512 from there; the nucleus 600.
        var blobs = new List<(double X, double Y, double Peak)> { (nucleus.X, nucleus.Y, 600) };
        foreach (var s in stars)
        {
            blobs.Add((s.X, s.Y, 300 * Math.Pow(10, -0.4 * (s.Magnitude - star.Magnitude))));
        }
        foreach (var (bx, by, peak) in blobs)
        {
            for (var y = Math.Max(0, (int)by - 10); y <= Math.Min(SkySide - 1, (int)by + 10); y++)
            {
                for (var x = Math.Max(0, (int)bx - 10); x <= Math.Min(SkySide - 1, (int)bx + 10); x++)
                {
                    var d2 = ((x - bx) * (x - bx)) + ((y - by) * (y - by));
                    frame[y, x] += (float)(peak * SkyNoise * Math.Exp(-d2 / (2.0 * 1.2 * 1.2)));
                }
            }
        }
        PeakOf(frame).ShouldBeLessThan(1f, "the brightest star stays under the file's ceiling");
        // What the stars handed over directly give on this frame, for the verbs to be held against: the scene's premise is
        // that the star is taken as one and the nucleus is not (the core may hold another catalogued star as well).
        var peaks = new List<CoreStars.CorePeak>();
        var input = CoreStarsTests.ImageOf(frame);
        var (_, direct) = CoreStars.Find(input, ExtendedCoreMask.Find(input), stars, options: null, peaks);
        foreach (var p in peaks)
        {
            output.WriteLine($"  peak ({p.X}, {p.Y}) {p.Height / SkyNoise:F0} sigma, {(p.IsStar ? "a catalogued star" : "kept")}, radius {p.Radius} px");
        }
        peaks.ShouldContain(p => p.IsStar && Math.Abs(p.X - starX) <= 1 && Math.Abs(p.Y - starY) <= 1);
        peaks.ShouldContain(p => !p.IsStar && Math.Abs(p.X - nucleus.X) <= 1 && Math.Abs(p.Y - nucleus.Y) <= 1);
        return (frame, model, wcs, nucleus, (starX, starY), $"{direct.Stars} of {direct.Peaks} compact peaks inside the cores are catalogued stars and stay removed");
    }

    private static string Write(float[,] plane, string dir, string name, WCS? wcs)
    {
        var path = Path.Combine(dir, name + ".fits");
        CoreStarsTests.ImageOf(plane).WriteToFitsFile(path, wcs);
        return path;
    }

    private static Image Read(string path)
    {
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue($"{path} was written");
        return image;
    }

    // The real image command, its remover the given one and its catalogue the embedded one, with what it said.
    private static (System.CommandLine.Command Command, List<string> Said) ImageCommand(IStarRemover remover, ICelestialObjectDB? db)
    {
        var said = new List<string>();
        var host = Substitute.For<IConsoleHost>();
        host.When(h => h.WriteScrollable(Arg.Any<string>(), Arg.Any<bool>())).Do(call => said.Add(call.Arg<string>()));
        host.When(h => h.WriteError(Arg.Any<string>())).Do(call => said.Add("error: " + call.Arg<string>()));
        var command = new ImageSubCommand(
            host,
            new SharpenPipeline(),
            remover,
            Substitute.For<IGradientCorrector>(),
            Substitute.For<IBackgroundExtractor>(),
            new MasterPreviewRenderer(catalogDb: null, NullLogger.Instance),
            objectDb: db).Build();
        return (command, said);
    }

    [Fact(Timeout = 300_000)]
    public async Task ProtectCoresReadsTheCatalogueForASolvedMasterAndLeavesItsStarRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var (frame, model, wcs, nucleus, star, found) = await SkySceneAsync(db, ct);
        var dir = _folders.Create("core-stars-verb-").FullName;
        var (inputPath, starlessPath) = (Write(frame, dir, "master", wcs), Write(model, dir, "master_starless", wcs: null));
        var (command, said) = ImageCommand(new FixedRemover(model, PeakOf(frame)), db);

        async Task<Image> RunAsync(string name, params string[] more)
        {
            said.Clear();
            var outPath = Path.Combine(dir, name + ".fits");
            var exit = await command.Parse(["protect-cores", "--input", inputPath, "--starless", starlessPath, "-o", outPath, .. more]).InvokeAsync(cancellationToken: ct);
            foreach (var line in said)
            {
                output.WriteLine(line);
            }
            exit.ShouldBe(0);
            return Read(outPath);
        }

        var embedded = await RunAsync("embedded", "--core-catalogue", "embedded");
        said.ShouldContain(line => line.Contains("embedded catalogue (Tycho-2)"));
        said.ShouldContain(line => line.Contains(found));
        CoreStarsTests.Restored(embedded, frame, model, nucleus.X, nucleus.Y).ShouldBe(1.0, 1e-3, "the nucleus takes the input back");
        CoreStarsTests.Restored(embedded, frame, model, star.X, star.Y).ShouldBe(0.0, 1e-3, "the catalogued star beside it stays removed");

        var none = await RunAsync("none", "--core-catalogue", "none");
        said.ShouldNotContain(line => line.Contains("catalogued stars"));
        CoreStarsTests.Restored(none, frame, model, star.X, star.Y).ShouldBe(1.0, 1e-3, "with no catalogue asked the star comes back with the core");

        // The same master with its solution taken away: said, and the star stays with the core.
        var unsolved = Write(frame, dir, "unsolved", wcs: null);
        said.Clear();
        var unsolvedOut = Path.Combine(dir, "unsolved_cores.fits");
        (await command.Parse(["protect-cores", "--input", unsolved, "--starless", starlessPath, "-o", unsolvedOut, "--core-catalogue", "embedded"])
            .InvokeAsync(cancellationToken: ct)).ShouldBe(0);
        foreach (var line in said)
        {
            output.WriteLine(line);
        }
        said.ShouldContain(line => line.Contains("carries no plate solution"));
        CoreStarsTests.Restored(Read(unsolvedOut), frame, model, star.X, star.Y).ShouldBe(1.0, 1e-3);
    }

    [Fact(Timeout = 300_000)]
    public async Task RemoveStarsHandsTheCatalogueToItsSplit()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await SharedCatalogDB.InitAsync(ct);
        var (frame, model, wcs, nucleus, star, found) = await SkySceneAsync(db, ct);
        var dir = _folders.Create("core-stars-verb-").FullName;
        var inputPath = Write(frame, dir, "master", wcs);
        var (command, said) = ImageCommand(new FixedRemover(model, PeakOf(frame)), db);
        var outPath = Path.Combine(dir, "out_starless.fits");

        var exit = await command.Parse(["remove-stars", inputPath, "-o", outPath, "--core-catalogue", "embedded"]).InvokeAsync(cancellationToken: ct);

        foreach (var line in said)
        {
            output.WriteLine(line);
        }
        exit.ShouldBe(0);
        said.ShouldContain(line => line.Contains(found));
        var starless = Read(outPath);
        CoreStarsTests.Restored(starless, frame, model, nucleus.X, nucleus.Y).ShouldBe(1.0, 1e-3, "the nucleus is in the starless plate");
        CoreStarsTests.Restored(starless, frame, model, star.X, star.Y).ShouldBe(0.0, 1e-3, "the catalogued star beside it is not");
        var stars = Read(ImageSubCommand.StarsOutput(outPath));
        (stars[0, star.Y, star.X] / (frame[star.Y, star.X] - model[star.Y, star.X])).ShouldBe(1f, 1e-3f, "it is in the stars");
    }

    [Fact]
    public async Task AHeadersPointingOrAMadeUpMatrixIsNoSolutionToPlaceStarsWith()
    {
        // A FITS header gives a WCS without a plate solution too: the pointing alone, or a matrix made up from the pixel
        // scale and an angle. Neither places a star to the pixel a peak is matched within, so neither is read with. No
        // catalogue is handed over here, so the one reason that is not about the solution marks having got past it.
        var ct = TestContext.Current.CancellationToken;
        const double scale = 6.0 / 3600;
        var solved = new WCS(18.06, -24.38) { CRPix1 = 299.5, CRPix2 = 299.5, CD1_1 = -scale, CD1_2 = 0, CD2_1 = 0, CD2_2 = scale };

        var pointing = await CatalogFrameStars.ReadAsync(new WCS(18.06, -24.38), 600, 600, installed: null, embedded: null, ct);
        var madeUp = await CatalogFrameStars.ReadAsync(solved with { IsApproximate = true }, 600, 600, installed: null, embedded: null, ct);
        var real = await CatalogFrameStars.ReadAsync(solved, 600, 600, installed: null, embedded: null, ct);

        CatalogFrameStars.IsSolved(solved).ShouldBeTrue();
        pointing.Stars.ShouldBeNull();
        pointing.Why.ShouldBe("the frame carries no plate solution");
        madeUp.Stars.ShouldBeNull();
        madeUp.Why.ShouldBe("the frame carries no plate solution");
        real.Stars.ShouldBeNull();
        real.Why.ShouldNotBeNull().ShouldContain("no star catalogue to ask");
    }

    [Fact]
    public async Task AskedForTheEmbeddedCatalogueWithNoneAtHandTheVerbSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (frame, model, _) = CoreStarsTests.Scene();
        var dir = _folders.Create("core-stars-verb-").FullName;
        var wcs = new WCS(18.06, -24.38) { CRPix1 = 255.5, CRPix2 = 255.5, CD1_1 = -6.0 / 3600, CD1_2 = 0, CD2_1 = 0, CD2_2 = 6.0 / 3600 };
        var (inputPath, starlessPath) = (Write(frame, dir, "master", wcs), Write(model, dir, "master_starless", wcs: null));
        var (command, said) = ImageCommand(new FixedRemover(model, PeakOf(frame)), db: null);

        var exit = await command.Parse(["protect-cores", "--input", inputPath, "--starless", starlessPath, "-o", Path.Combine(dir, "out.fits"), "--core-catalogue", "embedded"])
            .InvokeAsync(cancellationToken: ct);

        exit.ShouldBe(0);
        said.ShouldContain(line => line.Contains("the embedded catalogue is not at hand here"));
        said.ShouldNotContain(line => line.Contains("ASTAP"), "nothing is said about a database that was never looked for");
    }
}
