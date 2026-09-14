using System;
using Shouldly;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Orientation of the sky map's overlay ellipse, measured against an object that lies ON the axis
/// being drawn rather than against the position angle the drawing is derived from.
/// </summary>
/// <remarks>
/// <para>NGC 206 is a star cloud inside M31's disk, so the screen direction from M31's centre to
/// NGC 206 IS M31's major axis -- projected through the very same view matrix, from NGC 206's own
/// coordinates, with the position angle playing no part. That independence is the whole point: the
/// viewer's copy of this math carried two reflections for months while every test around it
/// re-derived its expectation through the same convention it was testing, which a reflection in
/// that convention cannot fail.</para>
/// <para>This covers <see cref="OverlayEngine.ComputeEllipseScreenAxes"/>, which is the CPU marker
/// AND -- as a hand-maintained algebraic mirror -- both GPU copies of the shader
/// (<c>skymap_overlay.vert</c> for Vulkan, the inline GLSL string in <c>WebGlSkyMapPipeline</c> for
/// the browser). The shader form <c>totalAngle = atan2(north.y, north.x) - pa</c> expands to
/// <c>cos(pa)*north + sin(pa)*(north.y, -north.x)</c>, which is this function exactly, so a failure
/// here is a failure in all three.</para>
/// </remarks>
[Collection("Astrometry")]
public sealed class SkyMapEllipseOrientationTests
{
    private const double M31RaHours = 10.6847 / 15.0;
    private const double M31DecDeg = 41.2691;
    private const double Ngc206RaHours = 10.1300 / 15.0;
    private const double Ngc206DecDeg = 40.7233;
    private const double M31PositionAngleDeg = 35.0;

    private const float ViewportH = 1000f;
    private const float CentreX = 800f;
    private const float CentreY = 500f;

    private static float AxisAlignment(bool mirrored)
    {
        var state = new SkyMapState
        {
            CenterRA = M31RaHours,
            CenterDec = M31DecDeg,
            MirrorView = mirrored,
        };
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        var ppr = SkyMapProjection.PixelsPerRadian(ViewportH, state.FieldOfViewDeg);

        SkyMapProjection.ProjectWithMatrix(M31RaHours, M31DecDeg,
            state.CurrentViewMatrix, ppr, CentreX, CentreY, out var cx, out var cy).ShouldBeTrue();
        SkyMapProjection.ProjectWithMatrix(M31RaHours, M31DecDeg + (1.0 / 60.0),
            state.CurrentViewMatrix, ppr, CentreX, CentreY, out var nxp, out var nyp).ShouldBeTrue();
        SkyMapProjection.ProjectWithMatrix(Ngc206RaHours, Ngc206DecDeg,
            state.CurrentViewMatrix, ppr, CentreX, CentreY, out var gx, out var gy).ShouldBeTrue();

        // The drawn major axis, exactly as DrawOverlayEllipse builds it.
        var paRad = (float)(M31PositionAngleDeg * Math.PI / 180.0);
        var (majorX, majorY, _, _) = OverlayEngine.ComputeEllipseScreenAxes(nxp - cx, nyp - cy, paRad, mirrored);

        // Ground truth: M31 -> NGC 206, same projection, no position angle involved.
        var tx = gx - cx;
        var ty = gy - cy;
        var tlen = MathF.Sqrt((tx * tx) + (ty * ty));
        tx /= tlen;
        ty /= tlen;

        // An axis is a line, so either direction along it is correct.
        return MathF.Abs((majorX * tx) + (majorY * ty));
    }

    [Fact]
    public void TheOverlayEllipseAxisPointsAtAnObjectLyingOnIt()
    {
        // 3 degrees of slack covers NGC 206 not sitting exactly on M31's centre line.
        AxisAlignment(mirrored: false).ShouldBeGreaterThan(MathF.Cos(3f * MathF.PI / 180f));
    }

    [Fact]
    public void TheOverlayEllipseAxisStillPointsAtItUnderMirrorView()
    {
        // MirrorView reflects the view's right axis, so the projected positions of BOTH M31 and
        // NGC 206 reflect together and the answer must not change. ComputeEllipseScreenAxes derives
        // east as a FIXED rotation of north (ex = ny, ey = -nx), which encodes one handedness and
        // takes no parity input -- so if that assumption is load-bearing, this is where it shows.
        // SkyBackdropView sets MirrorView from the solved frame, and a mirrored plate is ordinary.
        AxisAlignment(mirrored: true).ShouldBeGreaterThan(MathF.Cos(3f * MathF.PI / 180f));
    }
}
