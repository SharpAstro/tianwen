using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="CoreStars"/> (#2242): inside a protected core the catalogued stars stay removed, and the nucleus, which the
/// catalogue has no star for, takes the input back as before.
/// </summary>
[Collection("Imaging")]
public class CoreStarsTests(ITestOutputHelper output)
{
    internal const int Size = 512;
    private const float Sky = 0.02f;
    private const float Noise = 1e-4f;
    internal const int Cx = 256, Cy = 240;
    private const double StarWidth = 1.2;
    private const double NucleusPeak = 600;
    internal static readonly (int X, int Y, double Peak)[] CoreStarsAt = [(Cx + 10, Cy - 6, 300), (Cx - 12, Cy + 8, 150)];

    // The catalogue's magnitude of a star whose peak stands `peak` noise sigmas high: 15 at 100, a magnitude per factor 2.512.
    private static float MagnitudeOf(double peak) => (float)(15 - (2.5 * Math.Log10(peak / 100)));

    // A galaxy (a Gaussian of 60 px, 400 sigmas high) with a nucleus as narrow as a star, two stars inside its core and a
    // grid of field stars of four brightnesses; the plate of a remover that took every compact thing, nucleus included;
    // and the catalogue: every star, and nothing for the nucleus.
    internal static (float[,] Frame, float[,] Model, List<FrameStar> Catalogue) Scene()
    {
        var rng = new Random(11);
        var blobs = new List<(double X, double Y, double Peak)> { (Cx, Cy, NucleusPeak) };
        var catalogue = new List<FrameStar>();
        foreach (var (x, y, peak) in CoreStarsAt)
        {
            blobs.Add((x, y, peak));
            catalogue.Add(new FrameStar(x, y, MagnitudeOf(peak)));
        }
        double[] peaks = [100, 250, 630, 1600];
        var k = 0;
        for (var y = 60; y <= 460; y += 50)
        {
            for (var x = 60; x <= 460; x += 50)
            {
                if (Math.Abs(x - Cx) < 70 && Math.Abs(y - Cy) < 70)
                {
                    continue;
                }
                var peak = peaks[k++ % peaks.Length];
                blobs.Add((x, y, peak));
                catalogue.Add(new FrameStar(x, y, MagnitudeOf(peak)));
            }
        }
        var frame = new float[Size, Size];
        var model = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                var smooth = Sky + (float)(400 * Noise * Math.Exp(-r2 / (2.0 * 60 * 60))) + (float)(Noise * StarInjection.Gaussian(rng));
                var compact = 0.0;
                foreach (var (bx, by, peak) in blobs)
                {
                    var d2 = ((x - bx) * (x - bx)) + ((y - by) * (y - by));
                    if (d2 < 100)
                    {
                        compact += peak * Noise * Math.Exp(-d2 / (2.0 * StarWidth * StarWidth));
                    }
                }
                frame[y, x] = smooth + (float)compact;
                model[y, x] = smooth;
            }
        }
        return (frame, model, catalogue);
    }

    internal static Image ImageOf(float[,] plane) => new([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());

    // How much of what the remover took at (x, y) the plate holds again: 1 is the input's pixel, 0 the remover's.
    internal static double Restored(Image plate, float[,] frame, float[,] model, int x, int y)
        => (plate[0, y, x] - model[y, x]) / (frame[y, x] - model[y, x]);

    // The galaxy and the grid of field stars of Scene(), every star with wings (a Moffat profile of width 2 and index 1.5),
    // so a brighter one reaches further as a real one does; a nucleus of the given height, and one star 11.7 px from it.
    private static (float[,] Frame, List<FrameStar> Catalogue, (int X, int Y) Star) WingedScene(double nucleusPeak, double starPeak)
    {
        var rng = new Random(11);
        (int X, int Y) star = (Cx + 10, Cy - 6);
        var blobs = new List<(int X, int Y, double Peak)> { (Cx, Cy, nucleusPeak), (star.X, star.Y, starPeak) };
        var catalogue = new List<FrameStar> { new(star.X, star.Y, MagnitudeOf(starPeak)) };
        double[] peaks = [100, 250, 630, 1600];
        var k = 0;
        for (var y = 60; y <= 460; y += 50)
        {
            for (var x = 60; x <= 460; x += 50)
            {
                if (Math.Abs(x - Cx) < 70 && Math.Abs(y - Cy) < 70)
                {
                    continue;
                }
                var peak = peaks[k++ % peaks.Length];
                blobs.Add((x, y, peak));
                catalogue.Add(new FrameStar(x, y, MagnitudeOf(peak)));
            }
        }
        var frame = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r2 = ((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy));
                frame[y, x] = Sky + (float)(400 * Noise * Math.Exp(-r2 / (2.0 * 60 * 60))) + (float)(Noise * StarInjection.Gaussian(rng));
            }
        }
        const int reach = 55;
        foreach (var (bx, by, peak) in blobs)
        {
            for (var y = Math.Max(0, by - reach); y <= Math.Min(Size - 1, by + reach); y++)
            {
                for (var x = Math.Max(0, bx - reach); x <= Math.Min(Size - 1, bx + reach); x++)
                {
                    var d2 = ((x - bx) * (x - bx)) + ((y - by) * (y - by));
                    frame[y, x] += (float)(peak * Noise * Math.Pow(1 + (d2 / 4.0), -1.5));
                }
            }
        }
        return (frame, catalogue, star);
    }

    // What the rival rule leaves a star 11.7 px from a nucleus of the given height: the two radii, how much of the star's
    // disc is its own, and the footprint at the star's centre and at the nucleus.
    private (int StarRadius, int NucleusRadius, double ShareKept, float AtStar, float AtNucleus) BesideANucleus(double nucleusPeak, double starPeak)
    {
        var (frame, catalogue, star) = WingedScene(nucleusPeak, starPeak);
        var input = ImageOf(frame);
        var mask = ExtendedCoreMask.Find(input);
        mask.Cores.Length.ShouldBe(1);
        var peaks = new List<CoreStars.CorePeak>();

        var (footprint, report) = CoreStars.Find(input, mask, catalogue, options: null, peaks);

        output.WriteLine($"nucleus {nucleusPeak} sigma, star {starPeak} sigma: {report}");
        var own = footprint.ShouldNotBeNull();
        var starPeakFound = peaks.Single(p => p.IsStar);
        (Math.Abs(starPeakFound.X - star.X) <= 1 && Math.Abs(starPeakFound.Y - star.Y) <= 1).ShouldBeTrue($"the star's peak is found where it was drawn, not at ({starPeakFound.X}, {starPeakFound.Y})");
        var nucleus = peaks.Where(p => !p.IsStar).MaxBy(p => p.Height);
        (Math.Abs(nucleus.X - Cx) <= 1 && Math.Abs(nucleus.Y - Cy) <= 1).ShouldBeTrue($"the highest kept peak is the nucleus, not ({nucleus.X}, {nucleus.Y})");
        var (disc, kept) = (0, 0);
        var radius = starPeakFound.Radius;
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    disc++;
                    kept += own[((starPeakFound.Y + dy) * Size) + starPeakFound.X + dx] > 0.5f ? 1 : 0;
                }
            }
        }
        output.WriteLine($"  the star holds {radius} px and the nucleus {nucleus.Radius} px; {kept} of the {disc} px of the star's disc are its own; "
            + $"the footprint is {own[(starPeakFound.Y * Size) + starPeakFound.X]:F3} at the star and {own[(nucleus.Y * Size) + nucleus.X]:G3} at the nucleus");
        return (radius, nucleus.Radius, (double)kept / disc, own[(starPeakFound.Y * Size) + starPeakFound.X], own[(nucleus.Y * Size) + nucleus.X]);
    }

    [Fact]
    public void AStarCloseBesideAPeakThatReachesFarKeepsItsCentreOnly()
    {
        // The rival rule's limit, stated so it is not mistaken for a guarantee (#2257). A star's disc gives way to a kept
        // peak in units of each one's reach, so beside a nucleus that reaches three times as far a faint star keeps its
        // centre and loses its outskirts to the core, where the input comes back as it did before there was a catalogue.
        // The masters read stay far from this: M31's nucleus holds 14 px beside stars of 5 to 12.
        var far = BesideANucleus(nucleusPeak: 2500, starPeak: 30);
        var near = BesideANucleus(nucleusPeak: 60, starPeak: 30);

        ((double)far.NucleusRadius / far.StarRadius).ShouldBeGreaterThan(2.5, "the scene's premise: the nucleus reaches about three times as far as the star");
        // A thousandth: the feather's tail may touch it, nothing more.
        far.AtNucleus.ShouldBeLessThan(1e-3f, "whatever stands beside it, the kept peak itself is not in a star's footprint");
        far.AtStar.ShouldBeGreaterThan(0.8f, "the star's own centre stays removed");
        far.ShareKept.ShouldBeLessThan(0.4, "most of its disc goes to the nucleus");

        ((double)near.NucleusRadius / near.StarRadius).ShouldBeLessThan(1.5);
        near.AtNucleus.ShouldBeLessThan(1e-3f);
        near.ShareKept.ShouldBeGreaterThan(0.75, "beside a peak that reaches little further than itself a star keeps most of its disc");
    }

    [Fact]
    public void CataloguedStarsInsideACoreStayRemovedAndTheNucleusComesBack()
    {
        var (frame, model, catalogue) = Scene();
        var input = ImageOf(frame);
        var starless = ImageOf(model);

        // The premise: protection alone puts the stars inside the core back with the nucleus.
        var plain = ExtendedCoreMask.Find(input);
        plain.Cores.Length.ShouldBe(1);
        plain.StarsLeftOut.ShouldBeNull();
        var withStars = plain.Apply(input, starless);
        Restored(withStars, frame, model, Cx, Cy).ShouldBe(1.0, 1e-3);
        foreach (var (x, y, _) in CoreStarsAt)
        {
            Restored(withStars, frame, model, x, y).ShouldBe(1.0, 1e-3, "without a catalogue a star inside the core comes back whole");
        }

        var mask = ExtendedCoreMask.Find(input, ExtendedCoreMask.Options.Default, catalogue);

        mask.StarsLeftOut.ShouldNotBeNull();
        var found = mask.StarsLeftOut.Value;
        output.WriteLine($"{found}");
        found.CalibrationStars.ShouldBeGreaterThanOrEqualTo(CoreStars.Options.Default.MinCalibrationStars);
        found.Stars.ShouldBe(CoreStarsAt.Length);
        found.Peaks.ShouldBeGreaterThan(found.Stars, "the nucleus is a peak too, and no star");
        found.ZeroPointScatter.ShouldBeLessThan(0.1, "every star of the scene has one width, so one zero point fits them");
        found.ReachPerMagnitude.ShouldBeLessThanOrEqualTo(0.0, "a brighter star reaches further");

        var plate = mask.Apply(input, starless);
        Restored(plate, frame, model, Cx, Cy).ShouldBe(1.0, 1e-3, "the nucleus has no catalogued star on it and is the input's");
        foreach (var (x, y, _) in CoreStarsAt)
        {
            Restored(plate, frame, model, x, y).ShouldBe(0.0, 1e-3, "a catalogued star inside the core stays as the remover left it");
        }
    }

    [Fact]
    public void AFaintCataloguedStarOnTheNucleusDoesNotMakeItAStar()
    {
        var (frame, model, catalogue) = Scene();
        // Five magnitudes fainter than the nucleus reads: it carries a hundredth of that peak's light.
        catalogue.Add(new FrameStar(Cx + 0.4f, Cy - 0.3f, MagnitudeOf(NucleusPeak) + 5f));
        var input = ImageOf(frame);

        var mask = ExtendedCoreMask.Find(input, ExtendedCoreMask.Options.Default, catalogue);

        mask.StarsLeftOut.ShouldNotBeNull();
        mask.StarsLeftOut.Value.Stars.ShouldBe(CoreStarsAt.Length);
        Restored(mask.Apply(input, ImageOf(model)), frame, model, Cx, Cy).ShouldBe(1.0, 1e-3, "a star that faint cannot be the peak");
    }

    [Fact]
    public void ACataloguedStarAsBrightAsThePeakTakesIt()
    {
        // The rule's other side, stated so it is not mistaken for a guarantee: a peak the catalogue has a star for that can
        // carry its light is a star, whatever it is in truth.
        var (frame, model, catalogue) = Scene();
        catalogue.Add(new FrameStar(Cx, Cy, MagnitudeOf(NucleusPeak)));
        var input = ImageOf(frame);

        var mask = ExtendedCoreMask.Find(input, ExtendedCoreMask.Options.Default, catalogue);

        mask.StarsLeftOut.ShouldNotBeNull();
        mask.StarsLeftOut.Value.Stars.ShouldBe(CoreStarsAt.Length + 1);
        Restored(mask.Apply(input, ImageOf(model)), frame, model, Cx, Cy).ShouldBe(0.0, 1e-3);
    }

    [Fact]
    public void WithTooFewStarsToReadAZeroPointTheMaskIsLeftAsItWas()
    {
        var (frame, model, _) = Scene();
        var input = ImageOf(frame);
        // Only the two stars inside the core: nothing away from it to read the frame's zero point from.
        List<FrameStar> catalogue = [];
        foreach (var (x, y, peak) in CoreStarsAt)
        {
            catalogue.Add(new FrameStar(x, y, MagnitudeOf(peak)));
        }

        var mask = ExtendedCoreMask.Find(input, ExtendedCoreMask.Options.Default, catalogue);

        mask.StarsLeftOut.ShouldNotBeNull();
        mask.StarsLeftOut.Value.CalibrationStars.ShouldBe(0);
        mask.Weight.ShouldBe(ExtendedCoreMask.Find(input).Weight);
        Restored(mask.Apply(input, ImageOf(model)), frame, model, CoreStarsAt[0].X, CoreStarsAt[0].Y).ShouldBe(1.0, 1e-3);
    }

    [Fact]
    public void AFrameWithNoCoreHasNothingToLeaveOut()
    {
        var rng = new Random(5);
        var flat = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                flat[y, x] = Sky + (float)(Noise * StarInjection.Gaussian(rng));
            }
        }

        var mask = ExtendedCoreMask.Find(ImageOf(flat), ExtendedCoreMask.Options.Default, [new FrameStar(100, 100, 10f)]);

        mask.Cores.ShouldBeEmpty();
        mask.StarsLeftOut.ShouldBeNull();
    }

    [Fact(Timeout = 300_000)]
    public async Task TheEmbeddedCatalogueGivesTheLagoonsTwoBrightStars()
    {
        // With no ASTAP database installed the embedded Tycho-2 is asked. It holds the two bright stars a protected Lagoon
        // core put back (#2242): 9 Sagittarii and HD 164816.
        var ct = TestContext.Current.CancellationToken;
        // A catalogue of its own, never initialised, and not the shared loaded one (SharedCatalogDB): the read has to load
        // it itself, as it does for a host that has not asked the catalogue anything yet.
        var db = new CelestialObjectDB();
        const int side = 600;
        const double scale = 6.0 / 3600;
        var wcs = new WCS(18.06, -24.38) { CRPix1 = (side - 1) / 2.0, CRPix2 = (side - 1) / 2.0, CD1_1 = -scale, CD1_2 = 0, CD2_1 = 0, CD2_2 = scale };

        var (stars, why) = await CatalogFrameStars.ReadAsync(wcs, side, side, installed: null, embedded: db, ct);

        var found = stars.ShouldNotBeNull(why);
        output.WriteLine(found.Source);
        found.Stars.Count.ShouldBeGreaterThan(20);
        foreach (var (name, raHours, decDegrees, magnitude) in new[] { ("9 Sgr", 18.06457, -24.36072, 5.97), ("HD 164816", 18.06579, -24.31253, 7.09) })
        {
            var at = wcs.SkyToPixel(raHours, decDegrees).ShouldNotBeNull();
            var nearest = found.Stars.OrderBy(s => Math.Pow(s.X - at.X, 2) + Math.Pow(s.Y - at.Y, 2)).First();
            Math.Sqrt(Math.Pow(nearest.X - at.X, 2) + Math.Pow(nearest.Y - at.Y, 2)).ShouldBeLessThan(1.5, $"{name} is where the frame has it");
            ((double)nearest.Magnitude).ShouldBe(magnitude, 0.3, $"{name} at its catalogued magnitude");
        }

        var none = await CatalogFrameStars.ReadAsync(wcs, side, side, installed: null, embedded: null, ct);
        none.Stars.ShouldBeNull();
        none.Why.ShouldNotBeNull();
    }
}
