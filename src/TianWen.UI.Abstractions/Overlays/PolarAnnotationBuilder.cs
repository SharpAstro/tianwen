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
    /// <item>Current rotation-axis marker (CircledCross, colour ramping
    /// green/yellow/red as the total error magnitude crosses the ring
    /// thresholds).</item>
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

        // Axis marker tri-band colour ramp keyed off arcmin error magnitude.
        // Matches the 5'/15' ring boundaries (last band shows red beyond 15').
        private static readonly RGBAColor32 AxisGreen = new(0x44, 0xff, 0x44, 0xff);
        private static readonly RGBAColor32 AxisYellow = new(0xff, 0xcc, 0x33, 0xff);
        private static readonly RGBAColor32 AxisRed = new(0xff, 0x44, 0x44, 0xff);

        /// <summary>Build the annotation. Caller hands the result to the renderer.</summary>
        public static WcsAnnotation Build(in PolarOverlay overlay)
        {
            var poleLabel = overlay.Hemisphere == Hemisphere.North ? "NCP" : "SCP";

            var totalArcmin = System.Math.Sqrt(
                overlay.AzErrorArcmin * overlay.AzErrorArcmin
                + overlay.AltErrorArcmin * overlay.AltErrorArcmin);
            var axisColor = totalArcmin <= 5.0 ? AxisGreen
                : totalArcmin <= 15.0 ? AxisYellow
                : AxisRed;

            var markers = ImmutableArray.Create(
                new SkyMarker(
                    RaHours: overlay.TruePoleRaHours,
                    DecDeg: overlay.TruePoleDecDeg,
                    Glyph: SkyMarkerGlyph.Cross,
                    Color: TruePoleColor,
                    Label: $"{poleLabel} (True)",
                    SizePx: 12f),
                new SkyMarker(
                    RaHours: overlay.RefractedPoleRaHours,
                    DecDeg: overlay.RefractedPoleDecDeg,
                    Glyph: SkyMarkerGlyph.Cross,
                    Color: RefractedPoleColor,
                    Label: $"{poleLabel} (Refracted)",
                    SizePx: 12f),
                new SkyMarker(
                    RaHours: overlay.AxisRaHours,
                    DecDeg: overlay.AxisDecDeg,
                    Glyph: SkyMarkerGlyph.CircledCross,
                    Color: axisColor,
                    Label: null,
                    SizePx: 10f));

            var radii = overlay.RingRadiiArcmin.IsDefaultOrEmpty
                ? ImmutableArray.Create(5f, 15f, 30f)
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

            return new WcsAnnotation(markers, ringsBuilder.ToImmutable());
        }
    }
}
