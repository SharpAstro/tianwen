using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Planetary compose (#1278, <see cref="PlanetaryComposition"/>): mono stacks of one planet, taken through red, green, blue and IR
/// minutes apart and each framed a little differently, made one colour master on one disk at one instant; and the steps run one by
/// one through FITS files give the recipe's master to the bit, as <see cref="ColourCompositionTests"/> pins for the deep-sky steps.
/// </summary>
public sealed class PlanetaryCompositionTests : IDisposable
{
    private const int Size = 128;
    private const double Radius = 40;
    private const double TrueNorth = 30;
    private static readonly DateTimeOffset Start = new(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // Two runs of red, green and blue a minute and a half apart, then an IR, each with its filter's level and its own framing.
    private static readonly (string Filter, double Minutes, double Level, double Dx, double Dy)[] Set =
    [
        ("R", 0, 0.9, 0.0, 0.0),
        ("G", 1.5, 0.8, 2.3, -1.4),
        ("B", 3, 0.6, -1.7, 2.1),
        ("R", 4.5, 0.9, 1.2, 1.8),
        ("G", 6, 0.8, -2.6, -0.7),
        ("B", 7.5, 0.6, 0.4, -2.4),
        ("IR", 9, 0.7, 1.9, 0.6),
    ];

    [Fact(Timeout = 300_000)]
    public void TheStepsRunOneByOneThroughFilesGiveTheRecipesMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingested = Ingested();
        var (recipe, _, refusal) = PlanetaryComposition.Run(ingested);
        recipe.ShouldNotBeNull(refusal);
        recipe.Luminance.ShouldNotBeNull("the IR stack is the luminance");

        // The same routines one by one, every result through a FITS file and read back, as the verbs run them.
        var folder = _folders.Create("compose").FullName;
        var read = RoundTrip(ingested, folder, "ingested");
        var (registration, registerRefusal) = PlanetaryComposition.Register(read);
        registration.ShouldNotBeNull(registerRefusal);
        ct.ThrowIfCancellationRequested();
        read = RoundTrip(registration.Stacks, folder, "registered");
        var (derotation, derotateRefusal) = PlanetaryComposition.Derotate(read);
        derotation.ShouldNotBeNull(derotateRefusal);
        read = RoundTrip(derotation.Stacks, folder, "derotated");
        var (joined, joinRefusal) = PlanetaryComposition.Join(read);
        joined.ShouldNotBeNull(joinRefusal);

        Differing(recipe.Master, joined.Master).ShouldBe(0, "the steps through files give the recipe's master to the bit");
        joined.Luminance.ShouldNotBeNull();
        Differing(recipe.Luminance, joined.Luminance).ShouldBe(0);
    }

    [Fact]
    public void TheMasterLiesOnTheReferencesDiskTurnedToItsInstantWithTheTrueNorth()
    {
        var ingested = Ingested();
        var (composed, derotation, refusal) = PlanetaryComposition.Run(ingested);
        composed.ShouldNotBeNull(refusal);
        derotation.ShouldNotBeNull();

        // The reference is the green nearest the colours' middle (3.75 minutes): the first green, its instant the master's.
        var reference = PlanetaryComposition.ReferenceIndex(ingested);
        ingested[reference].Filter.ShouldBe(Filter.Green);
        derotation.Instant.ShouldBe(ingested[reference].Instant);
        var disk = DiskOf(Set[reference]);
        derotation.Placement.CenterX.ShouldBe(disk.CenterX, 0.2);
        derotation.Placement.CenterY.ShouldBe(disk.CenterY, 0.2);
        Math.IEEERemainder(derotation.Placement.NorthAngleDeg - TrueNorth, 360).ShouldBe(0, 2, "north decided by agreement is the true north");
        derotation.DecidedOn.ShouldNotBeNull("two reds 4.5 minutes apart turn the planet 2.7 degrees, enough to tell");

        // Its green is the planet at the reference's instant, where the greens moved onto the disk but not turned are not.
        var truth = Render(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, derotation.Instant), disk, 0.8);
        var (registration, _) = PlanetaryComposition.Register(ingested);
        registration.ShouldNotBeNull();
        var greens = registration.Stacks.Where(s => s.Filter == Filter.Green).Select(s => s.Image.GetChannelSpan(0).ToArray()).ToArray();
        var unturned = new float[Size * Size];
        for (var i = 0; i < unturned.Length; i++)
        {
            unturned[i] = (greens[0][i] + greens[1][i]) / 2;
        }
        var (turned, notTurned) = (InsideRms(composed.Master.GetChannelSpan(1), truth, disk), InsideRms(unturned, truth, disk));
        TestContext.Current.TestOutputHelper?.WriteLine($"green against the truth inside 0.8 radii: de-rotated {turned:0.00000}, only moved {notTurned:0.00000}");
        turned.ShouldBeLessThan(notTurned * 0.5);
    }

    [Fact]
    public void TheLuminanceStepThroughFilesGivesTheRecipesMasterAndKeepsItsColour()
    {
        var ingested = Ingested();
        var (recipe, derotation, refusal) = PlanetaryComposition.Run(ingested, withLuminance: true);
        recipe.ShouldNotBeNull(refusal);
        derotation.ShouldNotBeNull();
        var (plain, _, _) = PlanetaryComposition.Run(ingested);
        plain.ShouldNotBeNull();
        plain.Luminance.ShouldNotBeNull();

        // The step's verb: the join's master and luminance through their files, then the step.
        var folder = _folders.Create("lrgb").FullName;
        var (masterPath, luminancePath) = (Path.Combine(folder, "master.fits"), Path.Combine(folder, "luminance.fits"));
        plain.Master.WriteToFitsFile(masterPath);
        plain.Luminance.WriteToFitsFile(luminancePath);
        Image.TryReadFitsFile(masterPath, out var master).ShouldBeTrue();
        Image.TryReadFitsFile(luminancePath, out var luminance).ShouldBeTrue();
        var (detailed, scales, stepRefusal) = PlanetaryComposition.WithLuminance(master, luminance);
        detailed.ShouldNotBeNull(stepRefusal);
        Differing(recipe.Master, detailed).ShouldBe(0, "the step through files gives the recipe's master to the bit");

        // The rendered IR is the same planet at its own level, so each scale is the disk's level ratio and the colour stays put.
        var disk = new MetricDisk(derotation.Placement.CenterX, derotation.Placement.CenterY, derotation.Placement.EquatorialRadius);
        var (ratios, chromaShift, correlations, _, _) = PlanetaryComposition.ReadLuminance(master, luminance, detailed, disk);
        for (var c = 0; c < 3; c++)
        {
            (scales[c] / ratios[c]).ShouldBe(1, 0.1, $"channel {c}'s scale is its disk's level ratio");
        }
        chromaShift.ShouldBeLessThan(0.002);
        correlations[1].ShouldBeGreaterThan(0.9);
        correlations[2].ShouldBeGreaterThan(0.9);
    }

    [Fact]
    public void StacksCroppedToWhereTheirFramesReachedAreMovedOntoTheReferencesGrid()
    {
        // Each stack is cropped to where its own frames reached (#1300), so one night's stacks differ in size: a stack cut five columns
        // and three rows from its corner registers as the whole one does, onto the reference's grid.
        var ingested = Ingested();
        var reference = PlanetaryComposition.ReferenceIndex(ingested);
        var other = reference == 0 ? 1 : 0;
        var cropped = new List<PlanetaryMonoStack>(ingested);
        cropped[other] = ingested[other] with { Image = ingested[other].Image.Crop(Geometry.PixelRect.FromLTRB(5, 3, Size, Size - 2)) };

        var (whole, wholeRefusal) = PlanetaryComposition.Register(ingested);
        var (cut, cutRefusal) = PlanetaryComposition.Register(cropped);
        whole.ShouldNotBeNull(wholeRefusal);
        cut.ShouldNotBeNull(cutRefusal);

        cut.Stacks.ShouldAllBe(s => s.Image.Width == Size && s.Image.Height == Size, "every stack on the reference's grid");
        var disk = DiskOf(Set[reference]);
        var a = whole.Stacks[other].Image.GetChannelSpan(0);
        var b = cut.Stacks[other].Image.GetChannelSpan(0);
        var worst = 0.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (Math.Sqrt(((x - disk.CenterX) * (x - disk.CenterX)) + ((y - disk.CenterY) * (y - disk.CenterY))) < 0.8 * Radius)
                {
                    worst = Math.Max(worst, Math.Abs(a[(y * Size) + x] - b[(y * Size) + x]));
                }
            }
        }
        worst.ShouldBeLessThan(0.01, "the cropped stack lands where the whole one does");
    }

    [Fact(Timeout = 300_000)]
    public void ALuminanceFromAnotherStackIsScaledByTheDiskWeightedByNoiseAndPlacedOnTheOthersDisk()
    {
        // Two colour masters of one capture (#1330): the "demosaic" to place on, and the "drizzle" the luminance is made from, framed
        // 1.7 and -0.9 px away, its red, green and blue at their own levels and with their own noise.
        var ct = TestContext.Current.CancellationToken;
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Start);
        double[] levels = [0.9, 0.8, 0.6];
        double[] sigmas = [0.004, 0.002, 0.008];
        var onto = Colour(aspect, new DiskPlacement(63.6, 64.3, Radius, TrueNorth), levels, null);
        var drizzle = Colour(aspect, new DiskPlacement(65.3, 63.4, Radius, TrueNorth), levels, sigmas);

        var (luminance, parts, noise, wavelength, refusal) = PlanetaryComposition.Luminance(drizzle, CatalogIndex.Jupiter, Start, [610, 530, 460], onto);
        luminance.ShouldNotBeNull(refusal);
        ct.ThrowIfCancellationRequested();

        for (var c = 0; c < 3; c++)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"plane {c}: scale {parts[c].Scale:0.0000} (levels say {levels[1] / levels[c]:0.0000}), noise {parts[c].ScaledNoise:G4} on green's scale, weight {parts[c].Weight:P1}");
            parts[c].Scale.ShouldBe(levels[1] / levels[c], 0.02 * levels[1] / levels[c], "each plane on green's scale by the disk's level");
            // Read on the sky: over the whole plane the noisiest plane took in the disk's structure (blue read 0.0117 for 0.008).
            parts[c].Noise.ShouldBe(sigmas[c], 0.15 * sigmas[c], "each plane's noise is its own, not its planet's structure");
        }
        parts[1].Weight.ShouldBeGreaterThan(parts[0].Weight, "green is the least noisy on green's scale");
        parts[0].Weight.ShouldBeGreaterThan(parts[2].Weight, "blue the noisiest");
        wavelength.ShouldBeInRange(500, 560, "the weights' mean leans to green's 530 nm");
        noise.ShouldBeLessThan(parts.Min(p => p.ScaledNoise), "the luminance is cleaner than its best plane, read on the sky as they are");

        // Placed on the other master's disk: the luminance's limb lies where the demosaic's green does.
        (luminance.Width, luminance.Height).ShouldBe((onto.Width, onto.Height));
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var placed = PlanetaryLimbFit.Fit(luminance, options).ShouldNotBeNull();
        var target = PlanetaryLimbFit.Fit(onto, options).ShouldNotBeNull();
        placed.CenterX.ShouldBe(target.CenterX, 0.1);
        placed.CenterY.ShouldBe(target.CenterY, 0.1);
    }

    [Fact]
    public void AStackWithoutItsLabelsSaysWhichOneIsMissing()
    {
        var plane = new float[8, 8];
        var image = Image.FromChannel(plane);
        PlanetaryComposition.Ingest("stack.fits", image).Refusal.ShouldNotBeNull().ShouldContain("no planet");
        PlanetaryComposition.Ingest("Jupiter-R.fits", image).Refusal.ShouldNotBeNull().ShouldContain("no instant");
        PlanetaryComposition.Ingest("2024-12-15-1256_7-Jup.fits", image).Refusal.ShouldNotBeNull().ShouldContain("no filter");
        var (stack, refusal) = PlanetaryComposition.Ingest("2024-12-15-1256_7-Jup-IR.fits", image);
        stack.ShouldNotBeNull(refusal);
        (stack.Planet, stack.Instant, stack.Filter).ShouldBe((CatalogIndex.Jupiter, new DateTimeOffset(2024, 12, 15, 12, 56, 42, TimeSpan.Zero), Filter.Luminance));
    }

    // The set rendered and ingested from WinJUPOS-style names.
    private static List<PlanetaryMonoStack> Ingested()
    {
        var stacks = new List<PlanetaryMonoStack>();
        foreach (var s in Set)
        {
            var at = Start.AddMinutes(s.Minutes);
            var plane = Render(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, at), DiskOf(s), s.Level);
            var name = $"{at:yyyy-MM-dd-HHmm}_{at.Second / 6}-Test-{s.Filter}-Jup.fits";
            var (stack, refusal) = PlanetaryComposition.Ingest(name, Image.FromChannel(ToPlane(plane)), instant: at);
            stack.ShouldNotBeNull(refusal);
            stacks.Add(stack);
        }
        return stacks;
    }

    private static DiskPlacement DiskOf((string Filter, double Minutes, double Level, double Dx, double Dy) s)
        => new(63.6 + s.Dx, 64.3 + s.Dy, Radius, TrueNorth);

    private static float[] Render(in PlanetAspect aspect, in DiskPlacement disk, double level)
    {
        var plane = PlanetaryRender.Render(PlanetaryDerotationTests.SpottedMap(), aspect, disk, Size, Size, 0.95, supersample: 2);
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = (float)(plane[i] * level);
        }
        return plane;
    }

    // A colour master rendered on a disk, each plane at its level, with Gaussian noise of the given spreads when given.
    private static Image Colour(in PlanetAspect aspect, in DiskPlacement disk, double[] levels, double[]? sigmas)
    {
        var random = new Random(1330);
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = Render(aspect, disk, levels[c]);
            if (sigmas is not null)
            {
                for (var i = 0; i < plane.Length; i++)
                {
                    // Box-Muller.
                    var (u1, u2) = (1.0 - random.NextDouble(), random.NextDouble());
                    plane[i] += (float)(sigmas[c] * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
                }
            }
            planes[c] = ToPlane(plane);
        }
        var meta = new ImageMeta { ObjectName = "Jupiter", ExposureStartTime = Start, SensorType = SensorType.Color };
        return new Image(planes, BitDepth.Float32, 1, 0, 0, meta);
    }

    // Each stack written to its own FITS and read back as a compose step's verb reads it.
    private static List<PlanetaryMonoStack> RoundTrip(IEnumerable<PlanetaryMonoStack> stacks, string folder, string step)
    {
        var read = new List<PlanetaryMonoStack>();
        foreach (var stack in stacks)
        {
            var path = Path.Combine(folder, $"{stack.Name}.{step}.fits");
            stack.Image.WriteToFitsFile(path);
            Image.TryReadFitsFile(path, out var image).ShouldBeTrue();
            read.Add(PlanetaryComposition.FromIngested(stack.Name, image).ShouldNotBeNull($"{path} reads back as a stack"));
        }
        return read;
    }

    private static int Differing(Image a, Image b)
    {
        a.ChannelCount.ShouldBe(b.ChannelCount);
        var differing = 0;
        for (var c = 0; c < a.ChannelCount; c++)
        {
            var x = a.GetChannelSpan(c);
            var y = b.GetChannelSpan(c);
            for (var i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i])
                {
                    differing++;
                }
            }
        }
        return differing;
    }

    // The RMS difference inside 0.8 radii of the disk.
    private static double InsideRms(ReadOnlySpan<float> a, ReadOnlySpan<float> b, in DiskPlacement disk)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (dx, dy) = (x - disk.CenterX, y - disk.CenterY);
                if ((dx * dx) + (dy * dy) < 0.64 * disk.EquatorialRadius * disk.EquatorialRadius)
                {
                    var d = a[(y * Size) + x] - b[(y * Size) + x];
                    sum += d * d;
                    count++;
                }
            }
        }
        return Math.Sqrt(sum / count);
    }

    private static float[,] ToPlane(float[] flat)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = flat[(y * Size) + x];
            }
        }
        return plane;
    }
}
