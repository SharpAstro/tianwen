using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Shared;

/// <summary>
/// Shared Vulkan shape helpers for overlay markers (galaxies, stars, DSOs).
/// Used by the FITS viewer (<see cref="VkImageRenderer"/>); the sky map tab's own
/// object overlay draws its ellipses through <c>SkyMapTab.ObjectOverlay</c> instead
/// (it needs renderer-agnostic drawing since the same atlas also runs on WebGL), and
/// only borrows <see cref="DrawCross"/> / <see cref="DrawReticle"/> from here.
/// </summary>
public static class VkOverlayShapes
{
    /// <summary>
    /// Draws a rotated ellipse outline (or filled ellipse if <paramref name="thickness"/>
    /// is non-positive). The ellipse is given by its centre, semi-axes in pixels, and
    /// a rotation angle in radians measured from the +X axis (matches
    /// <see cref="TianWen.UI.Abstractions.Overlays.OverlayMarker.Ellipse"/>).
    /// </summary>
    public static void DrawEllipse(
        VkRenderer renderer, float dpiScale,
        float cx, float cy,
        float semiMajor, float semiMinor, float angleRad,
        RGBAColor32 color, float thickness)
    {
        // A rotated OUTLINE goes through the one shared walk (OverlayEngine), which also
        // backs AnnotatedRasterExport's CPU export path -- this call site used to carry
        // its own copy that silently drifted to bounding-box-only (see the method's doc).
        if (thickness > 0f && MathF.Abs(angleRad) >= 1e-3f)
        {
            OverlayEngine.DrawRotatedEllipseOutline(renderer, cx, cy, semiMajor, semiMinor, angleRad,
                color, Math.Max(1, (int)MathF.Round(thickness * dpiScale)));
            return;
        }

        // Bounding box of the rotated ellipse: see https://iquilezles.org/articles/ellipses/
        // Exact for the unrotated case above, and the fallback for a rotated FILL, which
        // no caller exercises today -- there is no rotated-fill primitive to reach for.
        var cosA = MathF.Cos(angleRad);
        var sinA = MathF.Sin(angleRad);
        var bboxW = MathF.Sqrt(semiMajor * semiMajor * cosA * cosA + semiMinor * semiMinor * sinA * sinA);
        var bboxH = MathF.Sqrt(semiMajor * semiMajor * sinA * sinA + semiMinor * semiMinor * cosA * cosA);

        var rect = new RectInt(
            new PointInt((int)(cx + bboxW), (int)(cy + bboxH)),
            new PointInt((int)(cx - bboxW), (int)(cy - bboxH)));

        if (thickness > 0f)
        {
            renderer.DrawEllipseOutline(rect, color, thickness * dpiScale);
        }
        else
        {
            renderer.FillEllipse(rect, color);
        }
    }

    /// <summary>
    /// Draws a plus-shape cross marker (used for stars) at the given centre. The arm
    /// length is in screen pixels; line thickness scales with <paramref name="dpiScale"/>.
    /// </summary>
    public static void DrawCross(
        VkRenderer renderer, float dpiScale,
        float cx, float cy, float armLength, RGBAColor32 color)
    {
        var thickness = Math.Max(1, (int)dpiScale);

        // Horizontal arm
        var hRect = new RectInt(
            new PointInt((int)(cx + armLength), (int)(cy + thickness)),
            new PointInt((int)(cx - armLength), (int)(cy - thickness)));
        renderer.FillRectangle(hRect, color);

        // Vertical arm
        var vRect = new RectInt(
            new PointInt((int)(cx + thickness), (int)(cy + armLength)),
            new PointInt((int)(cx - thickness), (int)(cy - armLength)));
        renderer.FillRectangle(vRect, color);
    }

    /// <summary>
    /// Draws a Stellarium-style mount reticle: an outer circle with an inner crosshair
    /// (with a gap at the centre so the exact pointing coordinate stays readable). Used
    /// by the sky map's mount-position overlay. The reticle is always drawn as an
    /// outline, no fill, so catalog markers underneath remain visible.
    /// </summary>
    /// <param name="radius">Outer circle radius in screen pixels (before DPI scaling).</param>
    /// <param name="armLength">Crosshair arm length in pixels (before DPI scaling).</param>
    /// <param name="gap">Central gap radius in pixels (before DPI scaling). The crosshair
    /// starts at <paramref name="gap"/> from the centre so the exact pointing pixel is
    /// not overdrawn.</param>
    public static void DrawReticle(
        VkRenderer renderer, float dpiScale,
        float cx, float cy, float radius, float armLength, float gap,
        RGBAColor32 color, float thickness = 2f)
    {
        var r = radius * dpiScale;
        var arm = armLength * dpiScale;
        var g = gap * dpiScale;
        var t = Math.Max(1, (int)(thickness * dpiScale * 0.5f));

        // Outer circle
        DrawEllipse(renderer, dpiScale, cx, cy, r, r, 0f, color, thickness);

        // Crosshair arms with central gap (4 separate segments)
        // Left arm
        renderer.FillRectangle(new RectInt(
            new PointInt((int)(cx - g), (int)(cy + t)),
            new PointInt((int)(cx - arm), (int)(cy - t))), color);
        // Right arm
        renderer.FillRectangle(new RectInt(
            new PointInt((int)(cx + arm), (int)(cy + t)),
            new PointInt((int)(cx + g), (int)(cy - t))), color);
        // Top arm
        renderer.FillRectangle(new RectInt(
            new PointInt((int)(cx + t), (int)(cy - g)),
            new PointInt((int)(cx - t), (int)(cy - arm))), color);
        // Bottom arm
        renderer.FillRectangle(new RectInt(
            new PointInt((int)(cx + t), (int)(cy + arm)),
            new PointInt((int)(cx - t), (int)(cy + g))), color);
    }
}
