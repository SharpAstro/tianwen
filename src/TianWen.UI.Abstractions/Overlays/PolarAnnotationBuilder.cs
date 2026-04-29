using System.Collections.Immutable;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.UI.Abstractions.Overlays
{
    /// <summary>
    /// Builds a generic <see cref="WcsAnnotation"/> from a routine-specific
    /// <see cref="PolarOverlay"/>. Pure data transformation: routine-specific
    /// pole/axis sky positions in -> generic markers + rings out, ready to
    /// hand to <see cref="WcsAnnotationLayer"/> with no polar-specific
    /// shader code in the renderer.
    ///
    /// Composition for the polar-alignment side panel:
    /// <list type="bullet">
    /// <item>True-pole cross (white) labelled "NCP/SCP (True)".</item>
    /// <item>Refracted-pole cross (green) labelled "NCP/SCP (Refracted)" — the
    /// refraction-corrected target the rotation axis should hit.</item>
    /// <item>Current rotation-axis crosshair (red, plain Cross glyph at 32px) --
    /// "center of rotation": where the camera sweeps around as the RA encoder
    /// turns. The user nudges polar knobs to walk this onto the refracted pole.</item>
    /// <item>5'/15'/30' rings (faint green) around the refracted pole.</item>
    /// </list>
    /// </summary>
    public static class PolarAnnotationBuilder
    {
        // White / green palette mirrors the SharpCap polar-alignment overlay so
        // returning users get a familiar look.
        private static readonly RGBAColor32 TruePoleColor = new(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 RefractedPoleColor = new(0x44, 0xff, 0x88, 0xff);
        private static readonly RGBAColor32 RingColor = new(0x44, 0xcc, 0x66, 0x88);
        // Brighter, more opaque ring for the J2000 preview overlay -- it's the
        // only ring on screen during rotation, so it must read clearly against
        // the dimmed grid (the regular RingColor is tuned for the busier
        // 5'/15'/30' triple stack of the post-Phase-A overlay).
        private static readonly RGBAColor32 PreviewRingColor = new(0x88, 0xff, 0xaa, 0xee);

        // Center-of-rotation crosshair: bright red at full opacity. Always red,
        // not ramped by error magnitude -- the *colour* shouldn't carry the
        // alignment-progress signal, the *distance* between this crosshair and
        // the refracted-pole cross does. Red was chosen to read clearly against
        // the green WCS grid + ring overlay without colour-blind ambiguity.
        private static readonly RGBAColor32 CenterOfRotationColor = new(0xff, 0x44, 0x44, 0xff);

        // Bright yellow for the SharpCap-style correction-direction arrow and
        // its target reticle: distinct from the red center-of-rotation
        // crosshair (so the user reads "yellow = action you should take" vs
        // "red = where the axis is right now") and from the green pole/ring
        // palette.
        private static readonly RGBAColor32 CorrectionArrowColor = new(0xff, 0xff, 0x55, 0xff);

        /// <summary>
        /// Minimal "preview" annotation shown during polar-align mode before the
        /// first probe frame solves. Just the J2000 celestial pole as a crosshair
        /// labelled NCP/SCP plus one 30' reference ring -- enough to anchor the
        /// user's eye on the actual pole while the rotation phase runs and the
        /// WCS grid sweeps past. Hemisphere is read off the WCS center Dec sign;
        /// when the camera is genuinely far from any pole the crosshair will be
        /// off-frame, which is fine -- the annotation simply doesn't render.
        /// </summary>
        public static WcsAnnotation BuildJ2000PolePreview(double centerDecDeg)
        {
            var hemisphere = centerDecDeg >= 0 ? Hemisphere.North : Hemisphere.South;
            var poleLabel = hemisphere == Hemisphere.North ? "NCP" : "SCP";
            var poleDec = hemisphere == Hemisphere.North ? 90.0 : -90.0;

            var markers = ImmutableArray.Create(
                new SkyMarker(
                    RaHours: 0.0,
                    DecDeg: poleDec,
                    Glyph: SkyMarkerGlyph.Cross,
                    Color: TruePoleColor,
                    Label: $"{poleLabel} (J2000)",
                    SizePx: 32f));

            // Mirror the post-Phase-A 4-ring set so the long Phase A wait
            // isn't visually empty. The shader's WCS grid layers the
            // meridian/parallel reference on top -- no overlay cross needed.
            var ringRadii = new[] { 5f, 15f, 30f, 45f };
            var rings = ImmutableArray.CreateBuilder<SkyRing>(ringRadii.Length);
            foreach (var r in ringRadii)
            {
                rings.Add(new SkyRing(
                    CenterRaHours: 0.0,
                    CenterDecDeg: poleDec,
                    RadiusArcmin: r,
                    Color: PreviewRingColor,
                    Label: $"{r:F0}'"));
            }

            return new WcsAnnotation(markers, rings.ToImmutable(), ImmutableArray<SkyArrow>.Empty);
        }

        /// <summary>Build the annotation. Caller hands the result to the renderer.</summary>
        public static WcsAnnotation Build(in PolarOverlay overlay)
        {
            var poleLabel = overlay.Hemisphere == Hemisphere.North ? "NCP" : "SCP";

            // Three reference markers: true pole, refracted pole, current axis.
            // The correction arrow's target reticle (when present) is appended
            // as a fourth Circle marker so the user can see the destination
            // even if the renderer skips the arrow shaft (sub-pixel or
            // off-screen endpoint).
            var markersBuilder = ImmutableArray.CreateBuilder<SkyMarker>(overlay.CorrectionArrow is null ? 3 : 4);
            markersBuilder.Add(new SkyMarker(
                RaHours: overlay.TruePoleRaHours,
                DecDeg: overlay.TruePoleDecDeg,
                Glyph: SkyMarkerGlyph.Cross,
                Color: TruePoleColor,
                Label: $"{poleLabel} (True)",
                SizePx: 12f));
            markersBuilder.Add(new SkyMarker(
                RaHours: overlay.RefractedPoleRaHours,
                DecDeg: overlay.RefractedPoleDecDeg,
                Glyph: SkyMarkerGlyph.Cross,
                Color: RefractedPoleColor,
                Label: $"{poleLabel} (Refracted)",
                SizePx: 12f));
            markersBuilder.Add(new SkyMarker(
                RaHours: overlay.AxisRaHours,
                DecDeg: overlay.AxisDecDeg,
                Glyph: SkyMarkerGlyph.Cross,
                Color: CenterOfRotationColor,
                Label: "Center of rotation",
                SizePx: 32f));
            if (overlay.CorrectionArrow is { } arrow)
            {
                markersBuilder.Add(new SkyMarker(
                    RaHours: arrow.EndRaHours,
                    DecDeg: arrow.EndDecDeg,
                    Glyph: SkyMarkerGlyph.Circle,
                    Color: CorrectionArrowColor,
                    Label: null,
                    SizePx: 16f));
            }

            // Default ring set mirrors SharpCap (5'/15'/30'/45'): coarse-to-
            // fine readout. The shader's WCS grid stays on top of these and
            // provides the meridian/parallel reference -- no need for an
            // overlay-side cross.
            var radii = overlay.RingRadiiArcmin.IsDefaultOrEmpty
                ? ImmutableArray.Create(5f, 15f, 30f, 45f)
                : overlay.RingRadiiArcmin;

            var ringsBuilder = ImmutableArray.CreateBuilder<SkyRing>(radii.Length);
            foreach (var radius in radii)
            {
                ringsBuilder.Add(new SkyRing(
                    CenterRaHours: overlay.RefractedPoleRaHours,
                    CenterDecDeg: overlay.RefractedPoleDecDeg,
                    RadiusArcmin: radius,
                    Color: RingColor,
                    Label: $"{radius:F0}'"));
            }

            // Optional SharpCap-style yellow correction arrow: shaft + 12px
            // head + a target reticle marker (added above) at the head so the
            // user sees "drag this star into that circle".
            ImmutableArray<SkyArrow> arrows = default;
            if (overlay.CorrectionArrow is { } a)
            {
                arrows = ImmutableArray.Create(new SkyArrow(
                    StartRaHours: a.StartRaHours,
                    StartDecDeg: a.StartDecDeg,
                    EndRaHours: a.EndRaHours,
                    EndDecDeg: a.EndDecDeg,
                    Color: CorrectionArrowColor,
                    Label: null,
                    ThicknessPx: 2f,
                    HeadSizePx: 12f));
            }

            return new WcsAnnotation(markersBuilder.ToImmutable(), ringsBuilder.ToImmutable(), arrows);
        }
    }
}
