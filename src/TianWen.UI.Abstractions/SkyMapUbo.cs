using System;
using System.Numerics;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The GPU sky map's per-frame view uniform block (std140, 144 bytes); one writer shared by
    /// the Vulkan pipeline (copies into its mapped per-frame slot) and the WebGL pipeline
    /// (uploads via SetUniformBlock). Layout (see the shader-side SkyMapUBO declarations):
    /// <code>
    /// mat4  viewMatrix       offset  0  (64 bytes, column-major - transposed on write)
    /// vec2  viewportCenter   offset 64
    /// float pixelsPerRadian  offset 72
    /// float magnitudeLimit   offset 76
    /// float fovDeg           offset 80
    /// float sinLat           offset 84
    /// vec2  viewportSize     offset 88
    /// float cosLat           offset 96
    /// float sinLST           offset 100
    /// float cosLST           offset 104
    /// int   horizonClip      offset 108
    /// vec3  tint             offset 112  (zero = none; a backdrop's Night colour)
    /// float brightness       offset 124  (1 = the map's own)
    /// int   gnomonic         offset 128  (0 = stereographic, the map's; 1 = a backdrop's perspective)
    /// </code>
    /// While <see cref="SkyMapState.Backdrop"/> is set (the orrery's backdrop, <see cref="SkyOrreryBackdrop"/>) the block carries THAT
    /// view: its matrix, focal length, tint and brightness, the gnomonic projection, no horizon clip, and the map's own
    /// <see cref="SkyMapState.CurrentViewMatrix"/> is left as it was, since the map's labels and hit tests project with it.
    /// </summary>
    public static class SkyMapUbo
    {
        public const int Size = 144;

        /// <summary>
        /// Composes the block into <paramref name="dst"/> (must be at least <see cref="Size"/>
        /// bytes) and stamps <see cref="SkyMapState.CurrentViewMatrix"/> as a side effect; the
        /// CPU label/overlay projection must agree with what the GPU renders this frame.
        /// <paramref name="offsetX"/>/<paramref name="offsetY"/> locate the map viewport inside
        /// the window; <paramref name="viewportWidth"/>/<paramref name="viewportHeight"/> are the
        /// NDC divisor the shaders use (the Vulkan path sets the map sub-rect via vkCmdSetViewport,
        /// the WebGL path passes the full canvas size and window-absolute center).
        /// </summary>
        public static void Write(
            Span<byte> dst, SkyMapState state,
            float viewportWidth, float viewportHeight,
            float offsetX, float offsetY,
            SiteContext site)
        {
            // LST trig + zenith direction in J2000 for Alt/Az mode
            var (fSinLST, fCosLST) = site.IsValid
                ? Math.SinCos(site.LST * (Math.PI / 12.0))
                : (0.0, 1.0);
            var zenithX = (float)(site.CosLat * fCosLST);
            var zenithY = (float)(site.CosLat * fSinLST);
            var zenithZ = (float)site.SinLat;
            // Refresh the roll from the mode's reference (celestial north / the local zenith) before
            // building the matrix. This is the one per-frame call site for both GPU backends, and it
            // is where the zenith is known; ComputeViewMatrix itself takes no reference direction, so
            // it cannot be singular. Near the reference the call holds the current roll instead of
            // recomputing it from a vanishing cross product.
            var backdrop = state.Backdrop;
            Matrix4x4 viewMatrix;
            float ppr;
            if (backdrop is { } view)
            {
                viewMatrix = view.View;
                ppr = (float)view.PixelsPerRadian;
            }
            else
            {
                state.UpdateRollForReference(zenithX, zenithY, zenithZ);
                viewMatrix = state.ComputeViewMatrix();
                state.CurrentViewMatrix = viewMatrix;
                ppr = (float)SkyMapProjection.PixelsPerRadian(viewportHeight, state.FieldOfViewDeg);
            }

            // Matrix4x4 is row-major in memory but GLSL mat4 expects column-major - transpose.
            var transposed = Matrix4x4.Transpose(viewMatrix);
            MemoryMarshal.Write(dst, in transposed);

            WriteF(dst, 64, offsetX + viewportWidth * 0.5f);
            WriteF(dst, 68, offsetY + viewportHeight * 0.5f);
            WriteF(dst, 72, ppr);
            // FOV-aware effective limit so zooming in reveals fainter stars (Stellarium computeRCMag idea).
            WriteF(dst, 76, state.EffectiveMagnitudeLimit);
            WriteF(dst, 80, (float)(backdrop?.FieldOfViewDeg ?? state.FieldOfViewDeg));
            WriteF(dst, 84, (float)site.SinLat);
            WriteF(dst, 88, viewportWidth);
            WriteF(dst, 92, viewportHeight);
            WriteF(dst, 96, (float)site.CosLat);
            WriteF(dst, 100, (float)fSinLST);
            WriteF(dst, 104, (float)fCosLST);
            var horizonClip = backdrop is null && state.ShowHorizon && site.IsValid ? 1 : 0;
            MemoryMarshal.Write(dst[108..], in horizonClip);
            var tint = backdrop?.Tint;
            WriteF(dst, 112, tint is { } t ? t.Red / 255f : 0f);
            WriteF(dst, 116, tint is { } g ? g.Green / 255f : 0f);
            WriteF(dst, 120, tint is { } b ? b.Blue / 255f : 0f);
            WriteF(dst, 124, backdrop?.Brightness ?? 1f);
            var gnomonic = backdrop is null ? 0 : 1;
            MemoryMarshal.Write(dst[128..], in gnomonic);
        }

        private static void WriteF(Span<byte> dst, int offset, float value)
            => MemoryMarshal.Write(dst[offset..], in value);
    }
}
