using Shouldly;
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
        var excess = InjectionPopulation.PlateRingExcess([Plate(amplitude)], Size, Size, Cx, Cy, 1.0, [amplitude]).ShouldNotBeNull();
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
        InjectionPopulation.PlateRingExcess([plate], Size, Size, 40.0, Cy, 1.0, [5.0]).ShouldBeNull("its sky ring leaves the frame");
        plate[((int)Cy * Size) + (int)Cx + 20] = float.NaN;
        InjectionPopulation.PlateRingExcess([plate], Size, Size, Cx, Cy, 1.0, [5.0]).ShouldBeNull("an absent pixel in the stamp");
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
            kept.Add(InjectionPopulation.PlateRingExcess([Plate(amplitude, i == 4 ? 4e-3 : 0.0)], Size, Size, Cx, Cy, 1.0, [amplitude]).ShouldNotBeNull());
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

        // Every other star: nothing inside 6 px, all of the stars' own table and the plate's share from 10 px.
        for (var k = 0; k <= InjectionPopulation.FieldHaloFromPx; k++)
        {
            fieldHalo[0][k].ShouldBe(0f, $"ring {k}");
        }
        ((double)fieldHalo[0][8]).ShouldBe(0.5 * (ownTable[8] + keptHalo[0][8]), 1e-7, "half in at 8 px");
        ((double)fieldHalo[0][12]).ShouldBe(ownTable[12] + keptHalo[0][12], 1e-7);
        ((double)fieldHalo[0][40]).ShouldBe(keptHalo[0][40], 1e-9, "past the stars' own tables only the plate's share is left");
        fieldHalo[0].ShouldAllBe(static v => v >= 0f);
    }
}
