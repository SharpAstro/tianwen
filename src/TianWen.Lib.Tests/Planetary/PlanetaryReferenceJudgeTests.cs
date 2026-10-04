using System;
using System.Globalization;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A master judged against another program's result of the same capture (<see cref="PlanetaryReferenceJudge"/>): the reference is a
/// display picture at its own scale, turn, mirror and tone, so it is placed on the master and its tone matched before any band is read.
/// </summary>
public class PlanetaryReferenceJudgeTests
{
    [Fact]
    public void AReferenceTurnedMirroredScaledAndCurvedIsPlacedAndAgreesBandForBand()
    {
        // The same Jupiter rendered 1.5 times larger, turned 35 degrees, mirrored and through a square-root display curve: the judge
        // finds the mirror and the scale, and once placed and matched every band but the finest agrees with the master's.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, new DateTimeOffset(2022, 9, 3, 12, 10, 0, TimeSpan.Zero));
        var axisRatio = PlanetaryLimbFit.OptionsFor(aspect).AxisRatio;
        var map = PlanetaryDerotationTests.SpottedMap();
        var (master, masterDisk) = Render(map, aspect, 200, 200, new DiskPlacement(99.6, 101.3, 60, NorthAngleDeg: -80), axisRatio);
        const int size = 300;
        var (rendered, _) = Render(map, aspect, size, size, new DiskPlacement(149.2, 151.7, 90, NorthAngleDeg: -45), axisRatio);
        var reference = new float[rendered.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                reference[(y * size) + x] = MathF.Sqrt(Math.Max(0, rendered[(y * size) + (size - 1 - x)]));
            }
        }
        var referenceDisk = new MetricDisk(size - 1 - 149.2, 151.7, 90, axisRatio);

        var judgement = Judge(master, 200, 200, masterDisk, reference, size, size, referenceDisk);

        judgement.Placement.Mirrored.ShouldBeTrue();
        judgement.Placement.Scale.ShouldBe(60.0 / 90, 0.01);
        judgement.Placement.Correlation.ShouldBeGreaterThan(0.95);
        judgement.Rings.ShouldBeEmpty();
        foreach (var band in judgement.Globe.Where(b => b.Band is >= 2 and <= 4))
        {
            band.Correlation.ShouldBeGreaterThan(0.95, $"band {band.Band}");
            band.EnergyRatio.ShouldBeInRange(0.85, 1.15, $"band {band.Band}");
        }
    }

    [Fact]
    public void ASaturnsRingsAreJudgedOffItsGlobe()
    {
        // A ringed Saturn and the same 1.3 times larger, turned 20 degrees and curved: the rings off the globe are read as a region of their
        // own, and agree once placed.
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, new DateTimeOffset(2022, 10, 25, 0, 57, 0, TimeSpan.Zero));
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var map = PlanetaryDerotationTests.SpottedMap();
        var (master, masterDisk) = Render(map, aspect, 240, 170, new DiskPlacement(119.6, 85.3, 34, NorthAngleDeg: 155), options.AxisRatio, options);
        var (rendered, referenceDisk) = Render(map, aspect, 320, 230, new DiskPlacement(160.4, 114.8, 44.2, NorthAngleDeg: 175), options.AxisRatio, options);
        var reference = rendered.Select(v => MathF.Pow(Math.Max(0, v), 0.6f)).ToArray();

        var judgement = Judge(master, 240, 170, masterDisk, reference, 320, 230, referenceDisk);

        judgement.Placement.Mirrored.ShouldBeFalse();
        judgement.Placement.Correlation.ShouldBeGreaterThan(0.9);
        judgement.RingPixels.ShouldBeGreaterThan(300);
        judgement.Rings.Single(b => b.Band == 3).Correlation.ShouldBeGreaterThan(0.9);
        judgement.Globe.Single(b => b.Band == 3).Correlation.ShouldBeGreaterThan(0.9);
    }

    private static (float[] Plane, MetricDisk Disk) Render(PlanetMap map, in PlanetAspect aspect, int width, int height, DiskPlacement placement,
        double axisRatio, LimbFitOptions? options = null)
    {
        var plane = PlanetaryRender.Render(map, aspect, placement, width, height, 0.85, supersample: 2, rings: options?.Rings);
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, axisRatio, placement.NorthAngleDeg)
        {
            Rings = options?.Rings is { } rings ? DiskRings.Of(placement.NorthAngleDeg, placement.NorthAngleDeg, options, rings) : null,
        };
        return (plane, disk);
    }

    private static ReferenceJudgement Judge(float[] master, int width, int height, MetricDisk masterDisk, float[] reference, int referenceWidth,
        int referenceHeight, MetricDisk referenceDisk)
    {
        var placement = PlanetaryReferenceJudge.Place(master, width, height, masterDisk, reference, referenceWidth, referenceHeight, referenceDisk);
        var resampled = PlanetaryReferenceJudge.Resample(reference, referenceWidth, referenceHeight, referenceDisk, width, height, masterDisk, placement);
        var matched = PlanetaryReferenceJudge.MatchTone(master, resampled, width, height, masterDisk);
        var judgement = PlanetaryReferenceJudge.Judge(master, matched, width, height, masterDisk, placement);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"placed at scale {placement.Scale:0.0000}, turned {placement.RotationDeg:0.0} deg{(placement.Mirrored ? ", mirrored" : "")}, shift ({placement.ShiftX:0.00}, {placement.ShiftY:0.00}), correlation {placement.Correlation:0.000}"));
        foreach (var (name, bands) in new[] { ("globe", judgement.Globe), ("rings", judgement.Rings) })
        {
            foreach (var b in bands)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {name} band {b.Band}: correlation {b.Correlation:0.000}, energy ratio {b.EnergyRatio:0.000}, shared gain {b.SharedGain:0.000}"));
            }
        }
        return judgement;
    }
}
