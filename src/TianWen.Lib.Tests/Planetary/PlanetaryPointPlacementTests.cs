using System;
using System.Globalization;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where alignment points go (#1253): a fifth of the strongest gradient leaves a ringed Saturn's points on its ring edges and limb, where a
/// patch floats along the edge, while placing them over the planet by the frame's own noise reaches its globe and keeps clear of its edge.
/// </summary>
public class PlanetaryPointPlacementTests
{
    [Fact]
    public void ASaturnsPointsGoOverItsGlobeByItsNoiseNotOnItsRingEdges()
    {
        const int width = 240, height = 160;
        var aspect = PhysicalEphemeris.Compute(CatalogIndex.Saturn, new DateTimeOffset(2023, 10, 10, 13, 20, 0, TimeSpan.Zero));
        var options = PlanetaryLimbFit.OptionsFor(aspect);
        var placement = new DiskPlacement(119.6, 80.3, 40, NorthAngleDeg: 90);
        var render = PlanetaryRender.Render(PlanetaryDerotationTests.SpottedMap(), aspect, placement, width, height, 0.85, supersample: 2,
            rings: options.Rings);
        var disk = new MetricDisk(placement.CenterX, placement.CenterY, placement.EquatorialRadius, options.AxisRatio, placement.NorthAngleDeg)
        {
            Rings = DiskRings.Of(placement.NorthAngleDeg, placement.NorthAngleDeg, options, options.Rings ?? throw new InvalidOperationException("Saturn has rings")),
        };
        // A stack's noise: small against the disk, as a reference of 1,000 frames carries.
        var random = new Random(1253);
        var plane = new float[height, width];
        for (var i = 0; i < render.Length; i++)
        {
            var gaussian = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            plane[i / width, i % width] = (float)(render[i] + (0.002 * gaussian));
        }
        var frame = new Image([plane], BitDepth.Float32, 1, 0, 0, new ImageMeta());

        var (peak, _) = FeatureDetector.DetectByPeakFraction(frame, new Geometry.PixelRect(0, 0, width, height), spacing: 16, maxPoints: 1000);
        var (over, _) = FeatureDetector.DetectOverPlanet(frame, spacing: 16, maxPoints: 1000);

        var peakOnGlobe = peak.Count(p => disk.RadiiAt(p.X, p.Y) < 0.8 && !disk.RingTouched(p.X, p.Y));
        var overOnGlobe = over.Count(p => disk.RadiiAt(p.X, p.Y) < 0.8 && !disk.RingTouched(p.X, p.Y));
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"a fifth of the strongest gradient: {peak.Length} points, {peakOnGlobe} on the globe; over the planet: {over.Length}, {overOnGlobe} on the globe"));
        // A fifth of the strongest gradient puts every point on an edge (39, none on the globe, measured); over the planet, the globe has its own.
        peakOnGlobe.ShouldBe(0);
        overOnGlobe.ShouldBeGreaterThanOrEqualTo(5);
        // None on the planet's outer edge: every point lies on the planet with its margin.
        foreach (var p in over)
        {
            disk.ClearRadiiAt(p.X, p.Y).ShouldBeLessThan(1.0, string.Create(CultureInfo.InvariantCulture, $"({p.X}, {p.Y})"));
        }
    }
}
