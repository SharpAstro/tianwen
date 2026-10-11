using System;
using System.Collections.Generic;
using System.Globalization;
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
    [Trait("Category", "Heavy")]
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
    [Trait("Category", "Heavy")]
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
    [Trait("Category", "Heavy")]
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
    [Trait("Category", "Heavy")]
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
    public void TheJoinAveragesOnlyTheStacksThatReachAPixelThroughTheStepFilesAsInTheRecipe()
    {
        // #1361: two greens, a red and a blue at one instant, the second green and the red cut six rows from the top (#1300), so moved
        // onto the reference's grid their top rows are reached by nothing of theirs. Averaged in as zero, those rows read half the sky.
        var ct = TestContext.Current.CancellationToken;
        var stacks = SameInstant([("R", 1.2, -0.8), ("G", 0.0, 0.0), ("G", -0.9, 1.1), ("B", 0.6, 0.4)]);
        var reference = PlanetaryComposition.ReferenceIndex(stacks);
        reference.ShouldBe(1, "the first green");
        foreach (var cut in new[] { 0, 2 })
        {
            stacks[cut] = stacks[cut] with { Image = stacks[cut].Image.Crop(Geometry.PixelRect.FromLTRB(0, 6, Size, Size)) };
        }

        var (registration, registerRefusal) = PlanetaryComposition.Register(stacks);
        registration.ShouldNotBeNull(registerRefusal);
        var (derotation, derotateRefusal) = PlanetaryComposition.Derotate(registration.Stacks);
        derotation.ShouldNotBeNull(derotateRefusal);
        var (joined, joinRefusal) = PlanetaryComposition.Join(derotation.Stacks);
        joined.ShouldNotBeNull(joinRefusal);
        ct.ThrowIfCancellationRequested();

        var red = registration.Stacks[0].Image.GetChannelSpan(0);
        var referenceGreen = registration.Stacks[1].Image.GetChannelSpan(0);
        var cutGreen = registration.Stacks[2].Image.GetChannelSpan(0);
        var joinedRed = joined.Master.GetChannelSpan(0);
        var joinedGreen = joined.Master.GetChannelSpan(1);
        var (unreachedGreen, unreachedRed) = (0, 0);
        for (var i = 0; i < cutGreen.Length; i++)
        {
            if (float.IsNaN(cutGreen[i]))
            {
                unreachedGreen++;
                // The two greens at one setting, so the reference's put on the other's scale is itself (#1337); half would be 0.025.
                joinedGreen[i].ShouldBe(referenceGreen[i], 1e-4f, $"pixel {i}: the green only the reference reaches reads the reference's, never half of it");
            }
            if (float.IsNaN(red[i]))
            {
                unreachedRed++;
                joinedRed[i].ShouldBe(0f, $"pixel {i}: a red no stack reaches is left empty");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"unreached: {unreachedGreen} px of the cut green, {unreachedRed} px of the red");
        unreachedGreen.ShouldBeGreaterThan(Size, "the cut green reaches none of the top row");
        unreachedRed.ShouldBeGreaterThan(Size, "nor does the red");
        float.IsNaN(referenceGreen[0]).ShouldBeFalse("the reference reaches its whole grid");

        // The same steps through their FITS files, as the verbs run them: the unreached pixels travel as NaN, and the master is the same
        // to the bit.
        var folder = _folders.Create("compose-reach").FullName;
        var read = RoundTrip(registration.Stacks, folder, "registered");
        float.IsNaN(read[2].Image.GetChannelSpan(0)[0]).ShouldBeTrue("an unreached pixel reads back as unreached");
        var (fromFiles, filesRefusal) = PlanetaryComposition.Derotate(read);
        fromFiles.ShouldNotBeNull(filesRefusal);
        var (joinedFromFiles, filesJoinRefusal) = PlanetaryComposition.Join(RoundTrip(fromFiles.Stacks, folder, "derotated"));
        joinedFromFiles.ShouldNotBeNull(filesJoinRefusal);
        Differing(joined.Master, joinedFromFiles.Master).ShouldBe(0, "the steps through files give the recipe's master to the bit");
        joined.Master.GetChannelSpan(0).ToArray().ShouldAllBe(v => float.IsFinite(v), "the master holds no unreached marker");
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public void AJoinWeighedByViewKeepsAStacksUnturnedRimOutLeavesTheLimbToTheStacksThatKeptItAndTheStepsGiveItToTheBit()
    {
        // #1349 fix C: two greens 30 minutes apart (the planet turns 18 degrees between them), a red and a blue between. Carried to the
        // reference's instant, a green keeps its own unturned pixel wherever its source lies past the de-rotation's 0.9-radius limit; joined
        // at full weight, that rim enters where the other green saw the planet squarely. Weighed by view, it stays out.
        var ct = TestContext.Current.CancellationToken;
        (string Filter, double Minutes, double Level, double Dx, double Dy)[] set =
            [("G", 0, 0.8, 0.0, 0.0), ("R", 10, 0.9, 1.2, -0.8), ("B", 20, 0.6, -0.9, 1.1), ("G", 30, 0.8, 0.7, 0.5)];
        var ingested = new List<PlanetaryMonoStack>();
        foreach (var s in set)
        {
            var at = Start.AddMinutes(s.Minutes);
            var name = $"{at:yyyy-MM-dd-HHmm}_{at.Second / 6}-Test-{s.Filter}-Jup.fits";
            var (stack, refusal) = PlanetaryComposition.Ingest(name, Image.FromChannel(ToPlane(Render(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, at), DiskOf(s), s.Level))), instant: at);
            ingested.Add(stack.ShouldNotBeNull(refusal));
        }
        var (registration, registerRefusal) = PlanetaryComposition.Register(ingested);
        registration.ShouldNotBeNull(registerRefusal);
        var (viewed, viewedRefusal) = PlanetaryComposition.Derotate(registration.Stacks, viewingWeights: PlanetaryViewingWeights.Source);
        viewed.ShouldNotBeNull(viewedRefusal);
        viewed.Stacks.ShouldAllBe(stack => stack.JoinWeight != null, "every stack weighed, the reference at its own instant included");
        var (plain, plainRefusal) = PlanetaryComposition.Derotate(registration.Stacks);
        plain.ShouldNotBeNull(plainRefusal);
        var (joined, joinRefusal) = PlanetaryComposition.Join(viewed.Stacks);
        var (joinedPlain, plainJoinRefusal) = PlanetaryComposition.Join(plain.Stacks);
        joined.ShouldNotBeNull(joinRefusal);
        joinedPlain.ShouldNotBeNull(plainJoinRefusal);
        ct.ThrowIfCancellationRequested();

        // Where one green saw the pixel squarely and the other not at all, the joined green against the green that saw it: weighed by
        // view it is that green's, to the floor's share; plain, it is half the other's rim.
        var greens = viewed.Stacks.Where(stack => stack.Filter == Filter.Green).ToArray();
        greens.Length.ShouldBe(2);
        var viewedGreen = joined.Master.GetChannelSpan(1);
        var plainGreen = joinedPlain.Master.GetChannelSpan(1);
        var (strip, offViewed, offPlain) = (0, 0.0, 0.0);
        for (var g = 0; g < 2; g++)
        {
            var seen = greens[g].Image.GetChannelSpan(0);
            var (weight, other) = (greens[g].JoinWeight.ShouldNotBeNull(), greens[1 - g].JoinWeight.ShouldNotBeNull());
            for (var i = 0; i < seen.Length; i++)
            {
                if (weight[i] > 0.1f && other[i] == 0)
                {
                    strip++;
                    offViewed += Math.Abs(viewedGreen[i] - seen[i]);
                    offPlain += Math.Abs(plainGreen[i] - seen[i]);
                }
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{strip} px one green alone saw squarely: off the green that saw them by {offViewed / strip:G4} weighed by view, {offPlain / strip:G4} plain");
        strip.ShouldBeGreaterThan(50, "18 degrees of turn leaves a strip each green alone saw squarely");
        offViewed.ShouldBeLessThan(0.1 * offPlain, "the other green's unturned rim stays out");

        // The limb left to the stacks that saw it there (#1349): inside 0.8 of the target's radius the weights are fix C's to the bit; from
        // 0.9 out every green's weight is its own unturned pixel's (one) or nothing (a turned sample the turn carried there from further in),
        // and the joined green there is the mean of the greens that kept their own.
        var (limbed, limbRefusal) = PlanetaryComposition.Derotate(registration.Stacks, viewingWeights: PlanetaryViewingWeights.Limb);
        limbed.ShouldNotBeNull(limbRefusal);
        var (joinedLimb, limbJoinRefusal) = PlanetaryComposition.Join(limbed.Stacks);
        joinedLimb.ShouldNotBeNull(limbJoinRefusal);
        var place = limbed.Placement;
        var limbGreens = limbed.Stacks.Where(stack => stack.Filter == Filter.Green).ToArray();
        var limbGreen = joinedLimb.Master.GetChannelSpan(1);
        var (inner, ring, partly, offOwn) = (0, 0, 0, 0.0);
        for (var i = 0; i < Size * Size; i++)
        {
            var (dx, dy) = ((i % Size) - place.CenterX, (i / Size) - place.CenterY);
            var rho = Math.Sqrt((dx * dx) + (dy * dy)) / place.EquatorialRadius;
            if (rho < 0.78)
            {
                for (var g = 0; g < 2; g++)
                {
                    limbGreens[g].JoinWeight.ShouldNotBeNull()[i].ShouldBe(greens[g].JoinWeight.ShouldNotBeNull()[i], "inside the disk the weights are fix C's");
                }
                inner++;
            }
            else if (rho is >= 0.92 and <= 0.99)
            {
                float[] weights = [.. limbGreens.Select(green => green.JoinWeight.ShouldNotBeNull()[i])];
                weights.ShouldAllBe(w => w == 0 || w == 1, "at the limb a green's own pixel or nothing");
                var kept = Enumerable.Range(0, 2).Where(g => weights[g] == 1).ToArray();
                kept.Length.ShouldBeGreaterThan(0, "the green at the reference's instant keeps its own limb");
                partly += kept.Length < 2 ? 1 : 0;
                offOwn += Math.Abs(limbGreen[i] - kept.Average(g => limbGreens[g].Image.GetChannelSpan(0)[i]));
                ring++;
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{inner} px inside 0.78 radii as fix C weighs them; {ring} px of the limb's ring, {partly} of them turned in one green, off the greens that kept their own by {offOwn / ring:G4}");
        inner.ShouldBeGreaterThan(1000);
        partly.ShouldBeGreaterThan(20, "18 degrees of turn carries the disk's inside to part of the limb");
        (offOwn / ring).ShouldBeLessThan(0.002, "the joined limb is the greens' own, to the floor's share and their scales");

        // The same steps through their files: each weight beside its stack, and the master the same to the bit, as the recipe gives it.
        var folder = _folders.Create("compose-viewed").FullName;
        var (fromFiles, filesRefusal) = PlanetaryComposition.Derotate(RoundTrip(registration.Stacks, folder, "registered"), viewingWeights: PlanetaryViewingWeights.Source);
        fromFiles.ShouldNotBeNull(filesRefusal);
        var read = RoundTrip(fromFiles.Stacks, folder, "derotated");
        File.Exists(PlanetaryComposition.JoinWeightPath(Path.Combine(folder, $"{greens[0].Name}.derotated.fits"))).ShouldBeTrue("the weight written beside its stack");
        read.ShouldAllBe(stack => stack.JoinWeight != null, "and read back with it");
        var (joinedFromFiles, filesJoinRefusal) = PlanetaryComposition.Join(read);
        joinedFromFiles.ShouldNotBeNull(filesJoinRefusal);
        Differing(joined.Master, joinedFromFiles.Master).ShouldBe(0, "the steps through files give the weighed master to the bit");
        var (recipe, _, recipeRefusal) = PlanetaryComposition.Run(ingested, viewingWeights: PlanetaryViewingWeights.Source);
        recipe.ShouldNotBeNull(recipeRefusal);
        Differing(recipe.Master, joined.Master).ShouldBe(0, "and so does the recipe");

        // A stack written again without its weight leaves no stale one beside it.
        PlanetaryComposition.WriteStack(read[0] with { JoinWeight = null }, Path.Combine(folder, $"{read[0].Name}.derotated.fits"));
        PlanetaryComposition.ReadStack(read[0].Name, Path.Combine(folder, $"{read[0].Name}.derotated.fits")).Stack.ShouldNotBeNull().JoinWeight.ShouldBeNull();
    }

    [Fact]
    public void AJoinWeighedByBandTakesTheFineDetailFromTheSharpStackAndTheCoarseFromAll()
    {
        // #1349 arm B: three greens of one instant on one disk, one sharp and two blurred by a Gaussian of 2 px, each with noise of 0.002
        // over a sky of 0.05; the planet carries a fine texture, since the spotted map has nearly nothing in the finest bands. Averaged by
        // noise alone, the master's band 1 holds about a third of the sharp stack's; weighed band by band by each stack's transfer against
        // the sharpest over its noise, nearly all of it, while band 5, which the three hold alike, is averaged over all three either way.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Start);
        var placement = DiskOf(("G", 0, 0, 0, 0));
        var random = new Random(1349);
        var white = new float[Size * Size];
        for (var i = 0; i < white.Length; i++)
        {
            var (u1, u2) = (1.0 - random.NextDouble(), random.NextDouble());
            white[i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        var texture = PlanetaryInverse.Apply(white, Size, Size, f => Math.Exp(-2 * Math.PI * Math.PI * 0.49 * f * f));
        var sharp = Render(aspect, placement, 0.8);
        for (var i = 0; i < sharp.Length; i++)
        {
            sharp[i] *= 1 + (0.3f * texture[i]);
        }
        var blurred = PlanetaryInverse.Apply(sharp, Size, Size, f => Math.Exp(-2 * Math.PI * Math.PI * 4 * f * f));
        var stacks = new List<PlanetaryMonoStack>();
        foreach (var source in new[] { blurred, sharp, blurred })
        {
            var plane = new float[source.Length];
            for (var i = 0; i < plane.Length; i++)
            {
                var (u1, u2) = (1.0 - random.NextDouble(), random.NextDouble());
                plane[i] = (float)(0.05 + source[i] + (0.002 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
            }
            var meta = new ImageMeta { ObjectName = "Jupiter", ExposureStartTime = Start, Filter = Filter.Green };
            stacks.Add(new PlanetaryMonoStack($"green{stacks.Count}", new Image([ToPlane(plane)], BitDepth.Float32, 1f, 0f, 0, meta), CatalogIndex.Jupiter, Start, Filter.Green));
        }
        // How sharp each is, read on its limb as a registration reads it, which picks the sharpest (#1349). Not registered: the 2 px blur
        // moves the fitted radius 3.3 %, past what a registration takes as one camera at one scale.
        stacks = [.. PlanetaryComposition.WithLimbEdges(stacks).ShouldNotBeNull("every limb fits")];
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius);
        // A join band by band refuses a stack no registration read, rather than pick its sharpest some other way.
        var unread = stacks[2] with
        {
            Image = new Image([stacks[2].Image.GetChannelArray(0)], BitDepth.Float32, 1f, 0f, 0, stacks[2].Image.ImageMeta with { LimbEdge = null }),
        };
        var (_, unreadRefusal) = PlanetaryComposition.JoinFilter([stacks[0], stacks[1], unread], Filter.Green, disk, bandWeights: PlanetaryBandWeights.Matched);
        unreadRefusal.ShouldNotBeNull().ShouldContain("no limb edge");
        var said = new List<string>();
        var (byBand, bandRefusal) = PlanetaryComposition.JoinFilter(stacks, Filter.Green, disk, said.Add, PlanetaryBandWeights.Matched);
        var (byWiener, wienerRefusal) = PlanetaryComposition.JoinFilter(stacks, Filter.Green, disk, bandWeights: PlanetaryBandWeights.Wiener);
        var (byNoise, noiseRefusal) = PlanetaryComposition.JoinFilter(stacks, Filter.Green, disk);
        byBand.ShouldNotBeNull(bandRefusal);
        byWiener.ShouldNotBeNull(wienerRefusal);
        byNoise.ShouldNotBeNull(noiseRefusal);
        foreach (var line in said)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }
        said.ShouldContain(line => line.Contains("the sharpest green1", StringComparison.Ordinal), "the sharp stack found as the sharpest");

        // Each master's band against the sharp truth's own inside 0.9 radii: the share of the truth's detail it holds.
        var truth = ATrousWaveletTransform.Decompose(sharp, Size, Size, PlanetaryComposition.BandWeightScales);
        double Held(float[,] joined, int band)
        {
            var flat = new float[Size * Size];
            for (var i = 0; i < flat.Length; i++)
            {
                flat[i] = joined[i / Size, i % Size];
            }
            var detail = ATrousWaveletTransform.Decompose(flat, Size, Size, PlanetaryComposition.BandWeightScales).Detail(band);
            var reference = truth.Detail(band);
            double shared = 0, own = 0;
            for (var i = 0; i < flat.Length; i++)
            {
                if (disk.RadiiAt(i % Size, i / Size) < 0.9)
                {
                    (shared, own) = (shared + ((double)detail[i] * reference[i]), own + ((double)reference[i] * reference[i]));
                }
            }
            return shared / own;
        }
        var (finestByBand, finestByNoise) = (Held(byBand, 0), Held(byNoise, 0));
        var (secondByBand, secondByNoise) = (Held(byBand, 1), Held(byNoise, 1));
        var (coarseByBand, coarseByNoise) = (Held(byBand, 4), Held(byNoise, 4));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"held by band / by noise: band 1 {finestByBand:0.000} / {finestByNoise:0.000}, band 2 {secondByBand:0.000} / {secondByNoise:0.000}, band 5 {coarseByBand:0.000} / {coarseByNoise:0.000}");
        // Band 1 takes 93 % of the sharp stack, yet holds 0.86 of it read back: a joined band leaks into its neighbours when the master is
        // decomposed again, the a trous bands not being orthogonal.
        finestByBand.ShouldBeGreaterThan(0.8, "the sharp stack's finest detail kept");
        finestByNoise.ShouldBeLessThan(0.5, "averaged by noise alone, the two blurred stacks take most of it away");
        secondByBand.ShouldBeGreaterThan(secondByNoise + 0.2, "and of band 2");
        coarseByBand.ShouldBe(coarseByNoise, 0.03, "the coarse band averaged over all three either way");

        // The Wiener form rescales each band to the sharpest stack's own transfer: more of the finest detail than the matched mean, which a
        // blurred stack's share pulls below the sharpest's, and the coarse band, which all three hold alike, as it was.
        var (finestWiener, secondWiener, coarseWiener) = (Held(byWiener, 0), Held(byWiener, 1), Held(byWiener, 4));
        TestContext.Current.TestOutputHelper?.WriteLine($"held by the Wiener form: band 1 {finestWiener:0.000}, band 2 {secondWiener:0.000}, band 5 {coarseWiener:0.000}");
        finestWiener.ShouldBeGreaterThan(finestByBand, "the matched mean's shortfall in band 1 given back");
        secondWiener.ShouldBeGreaterThan(secondByBand, "and in band 2");
        coarseWiener.ShouldBe(coarseByNoise, 0.03, "the coarse band as the plain join has it");
    }

    [Fact]
    public void AStacksLimbEdgeIsReadUnturnedAtItsRegistrationAndNoGainOrSkyMovesIt()
    {
        // #1349: a join band by band picks a filter's sharpest stack by the limb's edge each stack showed before any turn, read at its
        // registration; band 2's energy above its noise picked 2026-10-07's first burst, a gain scaled 1.40 times onto the others. A sharp
        // and a blurred green read apart; the sharp one at another gain on another sky reads as it did; the registration stamps every stack
        // with it, and the steps' files carry it.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Start);
        var placement = DiskOf(("G", 0, 0, 0, 0));
        var sharp = PlanetaryInverse.Apply(Render(aspect, placement, 1.0), Size, Size, f => Math.Exp(-2 * Math.PI * Math.PI * 0.36 * f * f));
        var blurred = PlanetaryInverse.Apply(sharp, Size, Size, f => Math.Exp(-2 * Math.PI * Math.PI * 2.25 * f * f));
        PlanetaryMonoStack Green(string name, float[] truth, double gain, double sky)
        {
            float[] plane = [.. truth.Select(v => (float)(sky + (gain * v)))];
            var (stack, refusal) = PlanetaryComposition.Ingest($"{name}.fits", Image.FromChannel(ToPlane(plane)), CatalogIndex.Jupiter, Start, Filter.Green);
            return stack.ShouldNotBeNull(refusal);
        }
        var (registration, refusal) = PlanetaryComposition.Register([Green("sharp", sharp, 0.8, 0.05), Green("blurred", blurred, 0.8, 0.05), Green("gained", sharp, 0.56, 0.08)]);
        registration.ShouldNotBeNull(refusal);
        var edges = registration.Stacks.Select(stack => stack.Image.ImageMeta.LimbEdge.ShouldNotBeNull("every stack's edge read")).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"the limb's edge at 0.1 cycles a pixel: sharp {edges[0]:0.0000}, blurred {edges[1]:0.0000}, the sharp one at 0.7 the gain on another sky {edges[2]:0.0000}");
        edges[0].ShouldBeGreaterThan(edges[1] + 0.05, "the sharper stack reads sharper");
        edges[2].ShouldBe(edges[0], 1e-3, "a gain and a sky move nothing");
        var read = RoundTrip(registration.Stacks, _folders.Create("compose-edge").FullName, "registered");
        for (var i = 0; i < read.Count; i++)
        {
            read[i].Image.ImageMeta.LimbEdge.ShouldNotBeNull().ShouldBe(registration.Stacks[i].Image.ImageMeta.LimbEdge.ShouldNotBeNull(), 1e-12, "the step's file carries it");
        }
    }

    [Fact]
    public void AFiltersStacksAtOtherSettingsJoinOnItsMedianStacksScaleWeightedByTheirNoise()
    {
        // #1337: three greens on one disk, two at one setting (0.8 of the planet over a sky of 0.05) and one at another (0.6 of it over a
        // sky of 0.08, a lower gain on another black level), each with noise of 0.002 in its own units. Averaged as they were, the master
        // sat at 0.733 of the planet on a sky of 0.06; put on the median stack's scale it sits at 0.8 on 0.05, the odd stack's noise on
        // that scale 0.8 / 0.6 of the others' and its weight 0.5625 of theirs.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Start);
        var placement = DiskOf(("G", 0, 0, 0, 0));
        var truth = Render(aspect, placement, 1.0);
        var random = new Random(1337);
        (double Level, double Sky)[] settings = [(0.8, 0.05), (0.6, 0.08), (0.8, 0.05)];
        var stacks = new List<PlanetaryMonoStack>();
        foreach (var (level, sky) in settings)
        {
            var plane = new float[truth.Length];
            for (var i = 0; i < plane.Length; i++)
            {
                var (u1, u2) = (1.0 - random.NextDouble(), random.NextDouble());
                plane[i] = (float)(sky + (level * truth[i]) + (0.002 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
            }
            var meta = new ImageMeta { ObjectName = "Jupiter", ExposureStartTime = Start, Filter = Filter.Green };
            stacks.Add(new PlanetaryMonoStack($"green{stacks.Count}", new Image([ToPlane(plane)], BitDepth.Float32, 1f, 0f, 0, meta), CatalogIndex.Jupiter, Start, Filter.Green));
        }
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius);
        var said = new List<string>();

        var (joined, refusal) = PlanetaryComposition.JoinFilter(stacks, Filter.Green, disk, said.Add);
        refusal.ShouldBeNull();
        joined.ShouldNotBeNull();

        var flat = new float[truth.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                flat[(y * Size) + x] = joined[y, x];
            }
        }
        var joinedSky = PlanetaryMetrics.SkyLevel(flat, Size, Size, disk).ShouldNotBeNull();
        double sum = 0, truthSum = 0;
        var count = 0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (disk.RadiiAt(x, y) < 0.9)
                {
                    (sum, truthSum, count) = (sum + flat[(y * Size) + x], truthSum + truth[(y * Size) + x], count + 1);
                }
            }
        }
        foreach (var line in said)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
        }
        var levelRatio = ((sum / count) - joinedSky) / (truthSum / count);
        TestContext.Current.TestOutputHelper?.WriteLine($"joined: sky {joinedSky:0.00000}, the disk at {levelRatio:0.0000} of the planet");
        joinedSky.ShouldBe(0.05, 0.0005, "the median stack's sky, not a blend of the skies");
        levelRatio.ShouldBe(0.8, 0.004, "the median stack's level, not 0.733");

        // Each stack's scale and weight said: the odd one on 0.8 / 0.6 and weighted 0.5625 of the others, 22 % of the three.
        said.Count.ShouldBe(3);
        var odd = said.Single(l => l.StartsWith("green1 ", StringComparison.Ordinal));
        double.Parse(System.Text.RegularExpressions.Regex.Match(odd, @"scale ([\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture).ShouldBe(0.8 / 0.6, 0.01);
        double.Parse(System.Text.RegularExpressions.Regex.Match(odd, @"weight ([\d.]+)").Groups[1].Value, CultureInfo.InvariantCulture).ShouldBe(22.0, 3.0);

        // One stack of a filter is taken as it is, to the bit.
        var (alone, _) = PlanetaryComposition.JoinFilter([stacks[1]], Filter.Green, disk);
        var single = stacks[1].Image.GetChannelSpan(0);
        alone.ShouldNotBeNull();
        for (var i = 0; i < single.Length; i++)
        {
            alone[i / Size, i % Size].ShouldBe(single[i]);
        }
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

    // Stacks of one instant, each through its filter and framed by its own offset, ingested from WinJUPOS-style names.
    // A sky of 0.05 under each, so a pixel averaged with a zero reads half of it.
    private static List<PlanetaryMonoStack> SameInstant((string Filter, double Dx, double Dy)[] set)
    {
        var stacks = new List<PlanetaryMonoStack>();
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Start);
        foreach (var (filter, dx, dy) in set)
        {
            var plane = Render(aspect, DiskOf((filter, 0, 0, dx, dy)), filter == "B" ? 0.6 : 0.8);
            for (var i = 0; i < plane.Length; i++)
            {
                plane[i] += 0.05f;
            }
            // One instant, so each stack's own number keeps two of one filter apart on disk.
            var name = $"{Start:yyyy-MM-dd-HHmm}_{Start.Second / 6}-Test{stacks.Count}-{filter}-Jup.fits";
            var (stack, refusal) = PlanetaryComposition.Ingest(name, Image.FromChannel(ToPlane(plane)), instant: Start);
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
            PlanetaryComposition.WriteStack(stack, path);
            var (back, refusal) = PlanetaryComposition.ReadStack(stack.Name, path);
            read.Add(back.ShouldNotBeNull(refusal ?? $"{path} reads back as a stack"));
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
