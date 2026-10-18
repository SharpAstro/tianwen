using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Where the orrery's compass was drawn (#1657), in device pixels: its centre and radius, the ends of its north-south axis and of the
/// equinox's tick, the ring's minor radius (the ecliptic plane's tilt), whether north faces the viewer, and the box it takes.
/// </summary>
internal readonly record struct OrreryCompassDrawn(float CentreX, float CentreY, float Radius, float NorthX, float NorthY,
    float SouthX, float SouthY, float EquinoxX, float EquinoxY, float RingMinorRadius, bool NorthTowardViewer, RectF32 Bounds);

/// <summary>
/// The orrery's compass (#1657): which way is north of the planets' plane. The camera keeps ecliptic north up on screen, so an arrow
/// alone would always point up and say nothing; what the eye loses is which SIDE of the plane it looks from, how steeply, and which
/// way longitude runs. So the compass is the plane itself as a ring (squashed by the view's tilt: a line edge on, round from a pole),
/// its axis with N and S (the end that faces the viewer drawn over the ring, the far one dim under it: N faces you above the plane,
/// S below it), and the vernal equinox's tick, longitude 0, which turns with the camera. From the camera's rotation alone, in the
/// view's top-right corner, every colour from the palette (Night spends no blue).
/// </summary>
public partial class SkyMapTab<TSurface>
{
    /// <summary>The compass's radius, design pixels.</summary>
    internal const float OrreryCompassRadius = 24f;

    /// <summary>The compass the last orrery frame drew, or null: what the tests read.</summary>
    internal OrreryCompassDrawn? LastOrreryCompass { get; private set; }

    private void DrawOrreryCompass(in OrreryCamera camera, RectF32 rect, float dpiScale)
    {
        var palette = GuiTheme.Palette;
        var radius = OrreryCompassRadius * dpiScale;
        var margin = 18f * dpiScale;
        var (cx, cy) = (rect.X + rect.Width - radius - margin, rect.Y + radius + margin);

        // Each direction on screen (y down), and how far it points toward the viewer.
        var north = camera.Orient(0, 0, 1);
        var equinox = camera.Orient(1, 0, 0);
        var (nx, ny) = (cx + (float)(north.Right * radius), cy - (float)(north.Up * radius));
        var (sx, sy) = (cx - (float)(north.Right * radius), cy + (float)(north.Up * radius));
        var (ex, ey) = (cx + (float)(equinox.Right * radius), cy - (float)(equinox.Up * radius));
        var northNear = north.Toward > 0;

        // The plane's ring: a unit circle in the ecliptic, its major axis along the screen's right (the camera's right lies in the
        // plane) and its minor axis the pole's share toward the viewer.
        var minor = (float)Math.Abs(north.Toward) * radius;
        var thin = (int)MathF.Max(1, MathF.Round(dpiScale));
        var thick = (int)MathF.Max(1, MathF.Round(1.5f * dpiScale));
        var (near, far) = (palette.BodyText, GuiTheme.Mix(palette.DimText, palette.ContentBg, 0.35f));

        // The far half of the axis first, so the ring goes over it; then the ring and the equinox's tick; then the near half.
        if (northNear)
        {
            DrawLine(cx, cy, sx, sy, far, thin);
        }
        else
        {
            DrawLine(cx, cy, nx, ny, far, thin);
        }
        if (minor < 0.5f)
        {
            DrawLine(cx - radius, cy, cx + radius, cy, palette.DimText, thin);
        }
        else
        {
            Renderer.DrawEllipse((cx, cy), (radius, 0f), (0f, minor), palette.DimText, thin);
        }
        DrawLine(cx, cy, ex, ey, palette.DimText, thin);
        if (northNear)
        {
            DrawLine(cx, cy, nx, ny, near, thick);
        }
        else
        {
            DrawLine(cx, cy, sx, sy, near, thick);
        }

        // The labels just beyond each end, read in the body text (Night's rule for what is read); the far end's dimmer.
        var fontSize = BaseFontSize * dpiScale * 0.75f;
        var box = fontSize * 1.6f;
        var fontPath = FontPath;
        void Label(string text, float x, float y, float dx, float dy, RGBAColor32 colour)
        {
            var length = MathF.Sqrt((dx * dx) + (dy * dy));
            var (ux, uy) = length > 1e-3f ? (dx / length, dy / length) : (0f, -1f);
            var (lx, ly) = (x + (ux * box * 0.6f), y + (uy * box * 0.6f));
            DrawText(text.AsSpan(), fontPath, lx - (box / 2), ly - (box / 2), box, box, fontSize, colour, TextAlign.Center, TextAlign.Center);
        }
        Label("N", nx, ny, nx - cx, ny - cy, northNear ? near : far);
        Label("S", sx, sy, sx - cx, sy - cy, northNear ? far : near);
        Label("0°", ex, ey, ex - cx, ey - cy, palette.BodyText);

        var reach = radius + box;
        LastOrreryCompass = new OrreryCompassDrawn(cx, cy, radius, nx, ny, sx, sy, ex, ey, minor, northNear,
            new RectF32(cx - reach, cy - reach, 2 * reach, 2 * reach));
    }
}
