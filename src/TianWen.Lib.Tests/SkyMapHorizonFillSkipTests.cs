using System;
using System.Numerics;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The ground fill is skipped for a view wholly above the horizon (<see cref="SkyMapState.ViewReachesBelowHorizon"/>): its shader
/// shades only below the horizon, so there it was a full-window pass, 1 to 1.6 ms a frame on an Iris Xe, that changed no pixel
/// (2026-11-25). The test that matters is that a skipped view really has no pixel below the horizon, judged through the
/// projection itself rather than the formula's own arithmetic.
/// </summary>
public class SkyMapHorizonFillSkipTests
{
    private const float ContentW = 1000f, ContentH = 800f;

    // Melbourne at a local sidereal time of 6 h: the zenith as the uniform writer passes it.
    private static readonly double Lat = -37.9, Lst = 6.0;

    private static Vector3 Zenith()
    {
        var (s, c) = Math.SinCos(double.DegreesToRadians(Lat));
        var (sl, cl) = Math.SinCos(Lst * Math.PI / 12.0);
        return new Vector3((float)(c * cl), (float)(c * sl), (float)s);
    }

    private static SkyMapState ViewAt(double altitudeDeg, double azimuthDeg, double fovDeg)
    {
        // The centre in RA/Dec for an altitude and azimuth at this site and sidereal time.
        var zenith = Zenith();
        var north = Vector3.Normalize(Vector3.UnitZ - zenith * zenith.Z);
        var east = Vector3.Cross(north, zenith);
        var (sAlt, cAlt) = Math.SinCos(double.DegreesToRadians(altitudeDeg));
        var (sAz, cAz) = Math.SinCos(double.DegreesToRadians(azimuthDeg));
        var forward = (float)cAlt * ((float)cAz * north + (float)sAz * east) + (float)sAlt * zenith;
        var ra = Math.Atan2(forward.Y, forward.X) * 12.0 / Math.PI;
        var dec = double.RadiansToDegrees(Math.Asin(forward.Z));
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Horizon, FieldOfViewDeg = fovDeg, CenterRA = (ra + 24.0) % 24.0, CenterDec = dec,
        };
        state.UpdateRollForReference(zenith.X, zenith.Y, zenith.Z, 1.0 / 60.0);
        state.CurrentViewMatrix = state.ComputeViewMatrix();
        return state;
    }

    // The lowest altitude of any pixel on the window's border, through the projection: the border holds the farthest pixels.
    private static double LowestBorderAltitudeDeg(SkyMapState state)
    {
        var zenith = Zenith();
        var ppr = SkyMapProjection.PixelsPerRadian(ContentH, state.FieldOfViewDeg);
        float cx = ContentW * 0.5f, cy = ContentH * 0.5f;
        var lowest = double.MaxValue;
        for (var i = 0; i <= 200; i++)
        {
            var t = i / 200f;
            foreach (var (x, y) in new[] { (t * ContentW, 0f), (t * ContentW, ContentH), (0f, t * ContentH), (ContentW, t * ContentH) })
            {
                var (ra, dec) = SkyMapProjection.UnprojectWithMatrix(x, y, state.CurrentViewMatrix, ppr, cx, cy);
                var v = SkyMapState.RaDecToUnitVec(ra, dec);
                var alt = double.RadiansToDegrees(Math.Asin(Math.Clamp(Vector3.Dot(new Vector3(v.X, v.Y, v.Z), zenith), -1f, 1f)));
                lowest = Math.Min(lowest, alt);
            }
        }
        return lowest;
    }

    [Theory]
    [InlineData(90.0, 0.0, 60.0)]
    [InlineData(85.0, 120.0, 21.8)]   // the owner's view, near the zenith
    [InlineData(70.0, 200.0, 30.0)]
    [InlineData(50.0, 45.0, 60.0)]
    [InlineData(30.0, 300.0, 40.0)]
    [InlineData(20.0, 180.0, 10.0)]
    [InlineData(10.0, 90.0, 60.0)]
    [InlineData(5.0, 270.0, 2.0)]
    [InlineData(60.0, 10.0, 120.0)]
    public void ASkippedViewHasNoPixelBelowTheHorizon_AndAViewThatHasOneIsNeverSkipped(double altitudeDeg, double azimuthDeg, double fovDeg)
    {
        var state = ViewAt(altitudeDeg, azimuthDeg, fovDeg);
        var lowest = LowestBorderAltitudeDeg(state);
        var reaches = state.ViewReachesBelowHorizon(ContentW, ContentH);

        if (!reaches)
        {
            lowest.ShouldBeGreaterThan(0.0, $"skipped at alt {altitudeDeg} fov {fovDeg}, yet a border pixel is at {lowest:F2} deg");
        }
        if (lowest < 0.0)
        {
            reaches.ShouldBeTrue($"a border pixel is at {lowest:F2} deg (alt {altitudeDeg} fov {fovDeg}), so the fill must draw");
        }
    }

    [Fact]
    public void LookingUpTheFillIsSkipped_AndLookingAtTheHorizonItIsNot()
    {
        ViewAt(90.0, 0.0, 60.0).ViewReachesBelowHorizon(ContentW, ContentH).ShouldBeFalse("the zenith at a 60 deg field");
        ViewAt(85.0, 120.0, 21.8).ViewReachesBelowHorizon(ContentW, ContentH).ShouldBeFalse("the owner's view");
        ViewAt(20.0, 180.0, 60.0).ViewReachesBelowHorizon(ContentW, ContentH).ShouldBeTrue("the horizon is on screen");
    }

    [Fact]
    public void WithNoZenithNothingIsSkipped()
        => new SkyMapState { FieldOfViewDeg = 60.0, CenterDec = 89.0 }.ViewReachesBelowHorizon(ContentW, ContentH).ShouldBeTrue();
}
