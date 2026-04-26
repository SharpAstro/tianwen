using System.Collections.Immutable;
using System.Linq;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests
{
    /// <summary>
    /// Pure data-transformation tests for <see cref="PolarAnnotationBuilder"/>:
    /// verify the routine-specific <see cref="PolarOverlay"/> projects to the
    /// expected generic <see cref="WcsAnnotation"/> (3 markers + N rings) so the
    /// renderer never sees polar-specific code.
    /// </summary>
    public class PolarAnnotationBuilderTests
    {
        private static PolarOverlay MakeOverlay(
            double azArcmin = 0,
            double altArcmin = 0,
            Hemisphere hemisphere = Hemisphere.North,
            ImmutableArray<float> rings = default)
        {
            return new PolarOverlay(
                TruePoleRaHours: 0.0,
                TruePoleDecDeg: hemisphere == Hemisphere.North ? 90.0 : -90.0,
                RefractedPoleRaHours: 0.0,
                RefractedPoleDecDeg: hemisphere == Hemisphere.North ? 89.95 : -89.95,
                AxisRaHours: 6.0,
                AxisDecDeg: hemisphere == Hemisphere.North ? 89.0 : -89.0,
                RingRadiiArcmin: rings,
                AzErrorArcmin: azArcmin,
                AltErrorArcmin: altArcmin,
                Hemisphere: hemisphere);
        }

        [Fact]
        public void Build_ProducesThreeMarkers_InTruePoleRefractedPoleAxisOrder()
        {
            var overlay = MakeOverlay();

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Markers.Length.ShouldBe(3);

            // True pole cross at J2000 pole position.
            annotation.Markers[0].DecDeg.ShouldBe(90.0);
            annotation.Markers[0].Glyph.ShouldBe(SkyMarkerGlyph.Cross);
            annotation.Markers[0].Label.ShouldBe("NCP (True)");

            // Refracted pole cross.
            annotation.Markers[1].DecDeg.ShouldBe(89.95);
            annotation.Markers[1].Glyph.ShouldBe(SkyMarkerGlyph.Cross);
            annotation.Markers[1].Label.ShouldBe("NCP (Refracted)");

            // Axis marker as CircledCross at the recovered axis sky position.
            annotation.Markers[2].RaHours.ShouldBe(6.0);
            annotation.Markers[2].DecDeg.ShouldBe(89.0);
            annotation.Markers[2].Glyph.ShouldBe(SkyMarkerGlyph.CircledCross);
            annotation.Markers[2].Label.ShouldBeNull();
        }

        [Fact]
        public void Build_LabelsHemisphereCorrectly_ForSouthernSite()
        {
            var overlay = MakeOverlay(hemisphere: Hemisphere.South);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Markers[0].Label.ShouldBe("SCP (True)");
            annotation.Markers[1].Label.ShouldBe("SCP (Refracted)");
        }

        [Fact]
        public void Build_DefaultsToFiveFifteenThirtyArcminRings_WhenOverlayHasNone()
        {
            var overlay = MakeOverlay(rings: default);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            var radii = annotation.Rings.Select(r => r.RadiusArcmin).ToArray();
            radii.ShouldBe([5f, 15f, 30f]);
            annotation.Rings.Select(r => r.Label).ShouldBe(["5'", "15'", "30'"]);
        }

        [Fact]
        public void Build_HonoursCustomRingRadii_WhenSupplied()
        {
            var overlay = MakeOverlay(rings: ImmutableArray.Create(2f, 10f));

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Rings.Length.ShouldBe(2);
            annotation.Rings[0].RadiusArcmin.ShouldBe(2f);
            annotation.Rings[1].RadiusArcmin.ShouldBe(10f);
        }

        [Fact]
        public void Build_RingsCenterOnRefractedPole_NotTruePole()
        {
            var overlay = MakeOverlay();

            var annotation = PolarAnnotationBuilder.Build(overlay);

            // Rings centre on the refracted pole (the practical alignment target),
            // not the true pole — the user adjusts knobs to drive the axis marker
            // into the bullseye around the refracted pole.
            foreach (var ring in annotation.Rings)
            {
                ring.CenterDecDeg.ShouldBe(89.95);
                ring.CenterRaHours.ShouldBe(0.0);
            }
        }

        [Fact]
        public void Build_AxisColorRampsGreenWhenWithinFiveArcmin()
        {
            var overlay = MakeOverlay(azArcmin: 2.0, altArcmin: 1.0); // total ~2.2'
            var green = new RGBAColor32(0x44, 0xff, 0x44, 0xff);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Markers[2].Color.ShouldBe(green);
        }

        [Fact]
        public void Build_AxisColorRampsYellowWhenBetweenFiveAndFifteenArcmin()
        {
            var overlay = MakeOverlay(azArcmin: 8.0, altArcmin: 4.0); // total ~8.9'
            var yellow = new RGBAColor32(0xff, 0xcc, 0x33, 0xff);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Markers[2].Color.ShouldBe(yellow);
        }

        [Fact]
        public void Build_AxisColorRampsRedWhenBeyondFifteenArcmin()
        {
            var overlay = MakeOverlay(azArcmin: 20.0, altArcmin: 10.0); // total ~22.4'
            var red = new RGBAColor32(0xff, 0x44, 0x44, 0xff);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Markers[2].Color.ShouldBe(red);
        }
    }
}
