using Shouldly;
using TianWen.Lib.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Imaging.StarRemoval;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The halo the starless plate keeps round a saturated star (<see cref="InjectionPopulation.PlateRingExcess"/>,
/// <see cref="InjectionPopulation.HaloOverPlate"/>, --halo-over-plate): a measurement of the master less the plate never sees
/// it, so it is read off the plate and drawn as the session's median per amplitude.
/// </summary>
[Collection("Imaging")]
public class HaloOverPlateTests
{
    private const int Size = 161;
    private const double Cx = 80.2, Cy = 79.7, Sky = 0.01;

    // What the plate keeps of a star of amplitude 1: nothing inside 6 px (subtracted and inpainted), a slow fall past it.
    private static double Kept(double r) => r >= 6 ? 4e-4 * Math.Exp(-(r - 6) / 12.0) : 0.0;

    private static float[] Plate(double amplitude, double extra = 0.0)
    {
        var plane = new float[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(((x - Cx) * (x - Cx)) + ((y - Cy) * (y - Cy)));
                plane[(y * Size) + x] = (float)(Sky + (amplitude * Kept(r)) + (r < InjectionPopulation.PlateHaloReachPx ? extra : 0.0));
            }
        }
        return plane;
    }

    [Fact]
    public void WhatThePlateKeepsIsReadPerAmplitudeOverItsOwnOuterRing()
    {
        const double amplitude = 5.0;
        var excess = InjectionPopulation.PlateRingExcess([Plate(amplitude)], Size, Size, null, Cx, Cy, 1.0, [amplitude]).ShouldNotBeNull();
        excess.Length.ShouldBe(1);
        excess[0].Length.ShouldBe(InjectionPopulation.PlateHaloReachPx + 1);
        // The sky ring holds the halo's own tail, which reads every ring that much low.
        var tail = Kept(55.0);
        foreach (var k in new[] { 8, 12, 20, 30, 40 })
        {
            excess[0][k].ShouldBe(Kept(k) - tail, 2e-5, $"ring {k}");
        }
        excess[0][3].ShouldBe(-tail, 2e-5, "nothing is kept inside the subtracted core");
    }

    [Fact]
    public void AStampThatLeavesTheFrameOrHoldsAnAbsentPixelIsNotRead()
    {
        var plate = Plate(5.0);
        InjectionPopulation.PlateRingExcess([plate], Size, Size, null, 40.0, Cy, 1.0, [5.0]).ShouldBeNull("its sky ring leaves the frame");
        plate[((int)Cy * Size) + (int)Cx + 20] = float.NaN;
        InjectionPopulation.PlateRingExcess([plate], Size, Size, null, Cx, Cy, 1.0, [5.0]).ShouldBeNull("a NaN in the stamp");
    }

    [Fact]
    public void AStampHoldingTheZeroCanvasRingIsNotReadThoughItsPixelsAreFinite()
    {
        // A master's canvas ring is exact zeros, which the classical remover leaves in the plate: finite, so only the absent
        // matrix says they are no sky. The ring edge is 40 px from the star, inside its 60 px stamp.
        var plate = Plate(5.0);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (x < 40 || y < 40 || x >= Size - 40 || y >= Size - 40)
                {
                    plate[(y * Size) + x] = 0f;
                }
            }
        }
        var absent = new Image([ToPlane(plate)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()).AbsentPixels().ShouldNotBeNull();
        InjectionPopulation.PlateRingExcess([plate], Size, Size, absent, Cx, Cy, 1.0, [5.0]).ShouldBeNull("zeros of the canvas ring in the stamp");
        InjectionPopulation.PlateRingExcess([plate], Size, Size, null, Cx, Cy, 1.0, [5.0]).ShouldNotBeNull("without the matrix the zeros read as sky, the bias the matrix prevents");
    }

    private static float[,] ToPlane(float[] flat)
    {
        var plane = new float[Size, Size];
        Buffer.BlockCopy(flat, 0, plane, 0, flat.Length * sizeof(float));
        return plane;
    }

    [Fact]
    public void TheSessionsHaloIsTheMedianOverItsStarsSoOneStarsNebulaIsNotDrawn()
    {
        // Nine stars of different amplitudes keep the same halo per amplitude; one sits on a nebula ten times that bright.
        var kept = new List<double[][]>();
        var own = new List<ImmutableArray<OuterHalo>>();
        var ownTable = Enumerable.Range(0, InjectionPopulation.MeasuredHaloReachPx + 1).Select(k => k >= 2 ? (float)(1e-3 * Math.Exp(-k / 5.0)) : 0f).ToImmutableArray();
        for (var i = 0; i < 9; i++)
        {
            var amplitude = 2.0 + i;
            kept.Add(InjectionPopulation.PlateRingExcess([Plate(amplitude, i == 4 ? 4e-3 : 0.0)], Size, Size, null, Cx, Cy, 1.0, [amplitude]).ShouldNotBeNull());
            own.Add([new OuterHalo(0, 0, 0, ownTable)]);
        }
        var (keptHalo, fieldHalo) = InjectionPopulation.HaloOverPlate(own, kept, 1);
        var tail = Kept(55.0);
        keptHalo[0].Length.ShouldBe(InjectionPopulation.PlateHaloReachPx + 1);
        foreach (var k in new[] { 8, 12, 20, 30 })
        {
            ((double)keptHalo[0][k]).ShouldBe(Kept(k) - tail, 3e-5, $"ring {k}: the nebula under one star is not in the median");
        }
        for (var k = 0; k <= InjectionPopulation.MatchToPx; k++)
        {
            keptHalo[0][k].ShouldBe(0f, $"ring {k}: nothing inside the match radii, where the plate is inpainted");
        }
        keptHalo[0].ShouldAllBe(static v => v >= 0f);
        keptHalo[0][InjectionPopulation.PlateHaloReachPx].ShouldBeLessThan(keptHalo[0][InjectionPopulation.PlateHaloReachPx - 5], "faded out at its reach");

        // Every other star: nothing inside the ramp, all of the stars' own table and the plate's share past it.
        for (var k = 0; k <= InjectionPopulation.FieldHaloFromPx; k++)
        {
            fieldHalo[0][k].ShouldBe(0f, $"ring {k}");
        }
        const int half = (InjectionPopulation.FieldHaloFromPx + InjectionPopulation.FieldHaloFullPx) / 2;
        ((double)fieldHalo[0][half]).ShouldBe(0.5 * (ownTable[half] + keptHalo[0][half]), 1e-7, "half in midway");
        const int full = InjectionPopulation.FieldHaloFullPx + 2;
        ((double)fieldHalo[0][full]).ShouldBe(ownTable[full] + keptHalo[0][full], 1e-7);
        ((double)fieldHalo[0][40]).ShouldBe(keptHalo[0][40], 1e-9, "past the stars' own tables only the plate's share is left");
        fieldHalo[0].ShouldAllBe(static v => v >= 0f);
    }

    private const double Ceiling = 0.9;

    private static readonly StarProfile Truth = StarProfile.Round(StarProfileFamily.Moffat, 2.5, 3.0);

    private static FittedStar Row(float x, float y, bool saturated, float amplitude)
        => new FittedStar(x, y, 100, amplitude, 1f, 0.1f, 0.01f, StarFitOutcome.Subtracted, saturated, false, float.NaN, float.NaN, false, float.NaN,
            StarFitModel.Moffat, [amplitude]);

    // A one-channel master of saturated stars on a grid, each clipped at the ceiling, and its plate: the classical remover
    // took each star's profile and left Kept(r) of its amplitude in the plate.
    private static InjectionPopulation Population(int saturatedStars, bool haloOverPlate, bool measured = true)
    {
        const int size = 640, pitch = 180, first = 100;
        const double amplitude = 5.0;
        var master = new float[size, size];
        var plate = new float[size, size];
        var catalogue = ImmutableArray.CreateBuilder<FittedStar>();
        var centres = Enumerable.Range(0, saturatedStars).Select(i => (X: first + 0.3 + (pitch * (i % 3)), Y: first - 0.2 + (pitch * (i / 3)))).ToArray();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double star = 0.0, kept = 0.0;
                foreach (var (cx, cy) in centres)
                {
                    var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                    if (r < 70)
                    {
                        star += amplitude * Truth.PixelMean(x, y, cx, cy);
                        kept += r < InjectionPopulation.PlateHaloReachPx ? amplitude * Kept(r) : 0.0;
                    }
                }
                plate[y, x] = (float)(Sky + kept);
                master[y, x] = (float)Math.Min(Ceiling, Sky + kept + star);
            }
        }
        foreach (var (cx, cy) in centres)
        {
            catalogue.Add(Row((float)cx, (float)cy, saturated: true, (float)amplitude));
        }
        // Unsaturated rows to draw brightness from, in the frame's last strip, away from every stamp.
        for (var i = 0; i < 12; i++)
        {
            catalogue.Add(Row(20f + (50f * i), size - 12f, saturated: false, 0.05f + (0.01f * i)));
        }
        return InjectionPopulation.Build(catalogue.ToImmutable(), new Image([master], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()),
            new Image([plate], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta()), 1.0, [(2.5, 3.0)], null, measuredSaturated: measured, haloOverPlate: haloOverPlate);
    }

    [Fact]
    public void ASessionOfEnoughMeasuredStarsDrawsThePlatesShareOnEveryStar()
    {
        var with = Population(9, haloOverPlate: true);
        var without = Population(9, haloOverPlate: false);
        with.SaturatedPool.ShouldBe(9);
        with.HaloOverPlateStars.ShouldBe(9);
        without.HaloOverPlateStars.ShouldBe(0);

        // Every other star: the session's halo under its profile, where there was none.
        var tail = Kept(55.0);
        without.ProfilesAt(300, 300, StarProfileFamily.Moffat)[0].Outer.IsNone.ShouldBeTrue();
        var field = with.ProfilesAt(300, 300, StarProfileFamily.Moffat)[0].Outer;
        field.AtRadius(4).ShouldBe(0.0, "nothing inside the ramp");
        foreach (var r in new[] { 16, 20, 30 })
        {
            field.AtRadius(r).ShouldBe(Kept(r) - tail, 4e-5, $"the plate's share at {r} px");
        }

        // A measured star: its own table, read off the master less the plate (which holds none of this), plus the plate's share.
        var plan = with.Plan(0, 0, 640, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 1.0, new Random(8));
        var planWithout = without.Plan(0, 0, 640, 0, InjectionPlacement.Random, StarProfileFamily.Moffat, 1.0, new Random(8));
        var saturated = plan.Stars.First(static s => s.Saturated);
        var saturatedWithout = planWithout.Stars.First(static s => s.Saturated);
        foreach (var r in new[] { 12, 20, 30, 40 })
        {
            (saturated.Profiles[0].Outer.AtRadius(r) - saturatedWithout.Profiles[0].Outer.AtRadius(r)).ShouldBe(Kept(r) - tail, 4e-5, $"a measured star at {r} px");
        }
        saturatedWithout.Profiles[0].Outer.AtRadius(40).ShouldBe(0.0, "without it a measured star's table ends at 30 px");

        // It draws no random number: the same seed places and sizes every star the same with it and without it.
        plan.Stars.Length.ShouldBe(planWithout.Stars.Length);
        for (var i = 0; i < plan.Stars.Length; i++)
        {
            plan.Stars[i].X.ShouldBe(planWithout.Stars[i].X, $"star {i} x");
            plan.Stars[i].Y.ShouldBe(planWithout.Stars[i].Y, $"star {i} y");
            plan.Stars[i].Amplitudes.ShouldBe(planWithout.Stars[i].Amplitudes, $"star {i} amplitudes");
            plan.Stars[i].Saturated.ShouldBe(planWithout.Stars[i].Saturated, $"star {i} saturated");
        }
    }

    [Fact]
    public void ASessionOfTooFewMeasuredStarsDrawsAsWithoutIt()
    {
        var few = Population(InjectionPopulation.MinHaloOverPlateStars - 1, haloOverPlate: true);
        few.SaturatedPool.ShouldBe(InjectionPopulation.MinHaloOverPlateStars - 1);
        few.HaloOverPlateStars.ShouldBe(0);
        few.ProfilesAt(300, 300, StarProfileFamily.Moffat)[0].Outer.IsNone.ShouldBeTrue();
    }

    [Fact]
    public void ItIsRefusedWithoutTheMeasuredStarsItIsReadFrom()
        => Should.Throw<ArgumentException>(() => Population(9, haloOverPlate: true, measured: false)).ParamName.ShouldBe("haloOverPlate");

    [Fact]
    public void ItIsRefusedBesideAnOuterHalo()
    {
        var image = new Image([new float[8, 8]], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        ImmutableArray<OuterHalo> outer = [new OuterHalo(0, 0, 0, [0f], "test")];
        Should.Throw<ArgumentException>(() => InjectionPopulation.Build([], image, image, 1.0, [(2.5, 3.0)], null,
            outerHalo: outer, measuredSaturated: true, haloOverPlate: true)).ParamName.ShouldBe("haloOverPlate");
    }
}
