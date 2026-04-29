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
            ImmutableArray<float> rings = default,
            PolarCorrectionArrow? correctionArrow = null)
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
                Hemisphere: hemisphere,
                CorrectionArrow: correctionArrow);
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

            // Center-of-rotation crosshair (red, plain Cross) at the recovered axis.
            annotation.Markers[2].RaHours.ShouldBe(6.0);
            annotation.Markers[2].DecDeg.ShouldBe(89.0);
            annotation.Markers[2].Glyph.ShouldBe(SkyMarkerGlyph.Cross);
            annotation.Markers[2].Label.ShouldBe("Center of rotation");
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
        public void Build_DefaultsToFourRingsAtSharpCapCadence_WhenOverlayHasNone()
        {
            var overlay = MakeOverlay(rings: default);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            // Mirrors SharpCap's 5'/15'/30'/45' ring set so the user gets the
            // same coarse-to-fine readout cadence; the 45' outer ring also
            // defines where the cross meridians terminate.
            var radii = annotation.Rings.Select(r => r.RadiusArcmin).ToArray();
            radii.ShouldBe([5f, 15f, 30f, 45f]);
            annotation.Rings.Select(r => r.Label).ShouldBe(["5'", "15'", "30'", "45'"]);
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
        public void Build_CenterOfRotationCrosshair_IsAlwaysRed_RegardlessOfErrorMagnitude()
        {
            // Color no longer ramps with error magnitude -- the *distance*
            // between the red crosshair and the refracted-pole cross now
            // carries the alignment-progress signal, not the colour.
            var red = new RGBAColor32(0xff, 0x44, 0x44, 0xff);
            foreach (var (az, alt) in new[] { (2.0, 1.0), (8.0, 4.0), (20.0, 10.0) })
            {
                var overlay = MakeOverlay(azArcmin: az, altArcmin: alt);
                var annotation = PolarAnnotationBuilder.Build(overlay);
                annotation.Markers[2].Color.ShouldBe(red, $"az={az} alt={alt}");
            }
        }

        [Fact]
        public void Build_NoCorrectionArrow_ProducesThreeMarkersAndNoArrows()
        {
            var overlay = MakeOverlay(); // CorrectionArrow null by default.

            var annotation = PolarAnnotationBuilder.Build(overlay);

            annotation.Markers.Length.ShouldBe(3);
            annotation.Arrows.IsDefaultOrEmpty.ShouldBeTrue("no correction supplied -> no arrow emitted");
        }

        [Fact]
        public void Build_WithCorrectionArrow_AppendsYellowArrowAndTargetReticle()
        {
            var arrow = new PolarCorrectionArrow(
                StartRaHours: 1.0, StartDecDeg: 60.0,
                EndRaHours: 1.5, EndDecDeg: 61.0);
            var overlay = MakeOverlay(correctionArrow: arrow);

            var annotation = PolarAnnotationBuilder.Build(overlay);

            // 4 markers: true pole, refracted pole, axis, plus the yellow
            // target reticle at the arrow head.
            annotation.Markers.Length.ShouldBe(4);
            annotation.Markers[3].RaHours.ShouldBe(1.5);
            annotation.Markers[3].DecDeg.ShouldBe(61.0);
            annotation.Markers[3].Glyph.ShouldBe(SkyMarkerGlyph.Circle);

            // Single yellow correction arrow (no cross-meridian SkyArrows).
            annotation.Arrows.Length.ShouldBe(1);
            annotation.Arrows[0].StartRaHours.ShouldBe(1.0);
            annotation.Arrows[0].EndRaHours.ShouldBe(1.5);
            annotation.Arrows[0].HeadSizePx.ShouldBeGreaterThan(0f, "correction arrow has an arrowhead");
            // Arrow and reticle share the same yellow palette so the user
            // reads them as a single hint pair.
            annotation.Arrows[0].Color.ShouldBe(annotation.Markers[3].Color);
        }

        [Fact]
        public void Build_CenterOfRotationCrosshair_IsLargerThanPoleCrosses_ForVisibility()
        {
            var overlay = MakeOverlay();

            var annotation = PolarAnnotationBuilder.Build(overlay);

            // True/refracted poles are 12px reference markers; the center-of-
            // rotation crosshair is the user's primary alignment target, so it
            // gets a meaningfully larger glyph (32px).
            annotation.Markers[2].SizePx.ShouldBeGreaterThan(annotation.Markers[0].SizePx);
            annotation.Markers[2].SizePx.ShouldBeGreaterThan(annotation.Markers[1].SizePx);
        }
    }
}
