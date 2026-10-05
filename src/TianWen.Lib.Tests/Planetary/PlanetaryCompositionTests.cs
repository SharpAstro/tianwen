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

    [Fact(Timeout = 300_000)]
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

    [Fact(Timeout = 300_000)]
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
