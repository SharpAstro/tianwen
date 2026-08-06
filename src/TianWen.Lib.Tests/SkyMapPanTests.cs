using System;
using System.Numerics;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a pan does to the view, measured in the only unit that matters: where the star you grabbed
/// ends up on screen.
///
/// <para>The reported bug was "after panning there is a jerky movement, and even after mouse release
/// it would rotate", worse near the pole. Both halves were one cause. A drag rotates the whole frame
/// rigidly, which near the pole legitimately earns a large roll -- at Dec -89 a 100 px pan earns 82
/// degrees -- because a change of RA at the pole IS a rotation. The per-frame realign then servoed the
/// roll back to the mode's absolute reference (a constant 0 in Equatorial), undoing it. The drag was
/// never wrong; the thing that ran after it was.</para>
///
/// <para>The severity scaled with the roll earned, which is why it vanished toward the equator: 82
/// degrees at the pole, 4.4 at Dec -30, exactly 0 at Dec 0.</para>
/// </summary>
public class SkyMapPanTests
{
    private const float ContentW = 1000f, ContentH = 800f;
    private const double Fov = 60.0;
    private const double FrameSeconds = 1.0 / 60.0;

    private static Matrix4x4 ViewMatrix(SkyMapState s)
    {
        var (f, r0, u0) = SkyMapState.ReferenceFrame(s.CenterRA, s.CenterDec);
        var (sinRoll, cosRoll) = Math.SinCos(s.CenterRoll);
        var right = (float)cosRoll * r0 + (float)sinRoll * u0;
        var up = (float)cosRoll * u0 - (float)sinRoll * r0;
        return new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            -f.X, -f.Y, -f.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>
    /// SkyMapTab.HandleDrag's geometry, replayed against a state. Duplicated rather than driven
    /// through the tab because the tab needs a render surface; the arithmetic below is copied from it
    /// verbatim, so a change there that this misses shows up as this test agreeing with nothing.
    /// </summary>
    private static void Drag(SkyMapState s, float fromX, float fromY, float toX, float toY)
    {
        var ppr = SkyMapProjection.PixelsPerRadian(ContentH, s.FieldOfViewDeg);
        float cx = ContentW * 0.5f, cy = ContentH * 0.5f;
        var startMatrix = ViewMatrix(s);

        s.IsDragging = true;
        s.DragStart = (fromX, fromY);
        s.DragStartCenter = (s.CenterRA, s.CenterDec);
        s.DragStartViewMatrix = startMatrix;

        var (ra1, dec1) = SkyMapProjection.UnprojectWithMatrix(fromX, fromY, startMatrix, ppr, cx, cy);
        var (ra2, dec2) = SkyMapProjection.UnprojectWithMatrix(toX, toY, startMatrix, ppr, cx, cy);
        var v1 = SkyMapState.RaDecToUnitVec(ra1, dec1);
        var v2 = SkyMapState.RaDecToUnitVec(ra2, dec2);
        var from = new Vector3(v2.X, v2.Y, v2.Z);
        var to = new Vector3(v1.X, v1.Y, v1.Z);
        var q = Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1f + Vector3.Dot(from, to)));

        var startForward = new Vector3(-startMatrix.M31, -startMatrix.M32, -startMatrix.M33);
        var startRight = new Vector3(startMatrix.M11, startMatrix.M12, startMatrix.M13);
        var (newRA, newDec, newRoll) = SkyMapState.FrameToCenter(
            Vector3.Transform(startForward, q), Vector3.Transform(startRight, q));

        s.CenterRA = newRA;
        s.CenterDec = newDec;
        s.CenterRoll = newRoll;
        s.NormalizeCenter();
    }

    private static void SettleAfterRelease(SkyMapState s)
    {
        s.IsDragging = false;
        for (var frame = 0; frame < 600; frame++)
        {
            s.UpdateRollForReference(deltaSeconds: FrameSeconds);
        }
    }

    [Theory]
    [InlineData(-89.0)] // the DEFAULT southern view, and where this was unusable
    [InlineData(-85.0)]
    [InlineData(-60.0)]
    [InlineData(-30.0)]
    [InlineData(0.0)]
    [InlineData(89.0)]  // and the northern default, since nothing here may be hemisphere-specific
    public void TheGrabbedStarStaysUnderThePointer_AndDoesNotMoveAfterRelease(double startDec)
    {
        var s = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = Fov, CenterRA = 16.12, CenterDec = startDec };
        var ppr = SkyMapProjection.PixelsPerRadian(ContentH, Fov);
        float cx = ContentW * 0.5f, cy = ContentH * 0.5f;

        var (grabRA, grabDec) = SkyMapProjection.UnprojectWithMatrix(500f, 400f, ViewMatrix(s), ppr, cx, cy);
        Drag(s, 500f, 400f, 600f, 400f);

        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var releaseX, out var releaseY)
            .ShouldBeTrue();
        releaseX.ShouldBe(600f, 0.5f, "the grabbed star must sit under the pointer at mouse-up");
        releaseY.ShouldBe(400f, 0.5f);

        SettleAfterRelease(s);

        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var settledX, out var settledY)
            .ShouldBeTrue();
        var slid = Math.Sqrt(Math.Pow(settledX - releaseX, 2) + Math.Pow(settledY - releaseY, 2));

        // This measured 131.9 px at Dec -89 before the fix -- MORE than the 100 px the user dragged,
        // so letting go threw the sky further than the gesture had moved it.
        slid.ShouldBeLessThan(0.5, $"the sky must not keep moving after the gesture ends (Dec {startDec})");
    }

    [Theory]
    // Alt/Az has the same requirement and the same singularity, with the ZENITH standing in for the
    // pole: a pan must move the sky in the direction of the drag, so the point you grabbed ends up
    // where you dropped it. Near the zenith the reference is ill-conditioned, which is exactly where
    // servoing to it used to throw the field.
    [InlineData(40.0, "at the zenith")]        // the site's zenith Dec, i.e. straight overhead
    [InlineData(38.0, "just off the zenith")]  // and just outside the old lock cone, the worst case
    [InlineData(10.0, "well down the sky")]
    public void InAltAz_ThePanFollowsTheDragDirection(double startDec, string where)
    {
        // A fixed zenith: over one gesture the sky turns by microdegrees, and the claim here is about
        // the gesture, not the sky's rotation (which InHorizonMode_TheRollFOLLOWS... covers).
        var zenith = SkyMapState.RaDecToUnitVec(8.0, 40.0);
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 8.0, CenterDec = startDec };
        var ppr = SkyMapProjection.PixelsPerRadian(ContentH, Fov);
        float cx = ContentW * 0.5f, cy = ContentH * 0.5f;

        // Grab off-centre and drag diagonally, so a sign error on either axis shows up.
        const float fromX = 430f, fromY = 330f, toX = 610f, toY = 455f;
        var (grabRA, grabDec) = SkyMapProjection.UnprojectWithMatrix(fromX, fromY, ViewMatrix(s), ppr, cx, cy);

        Drag(s, fromX, fromY, toX, toY);
        s.IsDragging = false;

        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var x, out var y).ShouldBeTrue();
        x.ShouldBe(toX, 0.5f, $"the grabbed point must land where the drag ended ({where})");
        y.ShouldBe(toY, 0.5f, $"and on both axes ({where})");

        // Still there after the frames that follow the release.
        for (var frame = 0; frame < 600; frame++)
        {
            s.UpdateRollForReference(zenith.X, zenith.Y, zenith.Z, FrameSeconds);
        }
        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var settledX, out var settledY).ShouldBeTrue();
        Math.Sqrt(Math.Pow(settledX - x, 2) + Math.Pow(settledY - y, 2))
            .ShouldBeLessThan(0.5, $"and it must not drift once the gesture is over ({where})");
    }

    [Fact]
    public void APanNearThePoleEarnsRoll_AndKeepsIt()
    {
        // The roll is not an artefact to be cleaned up: near the pole it is what holds the sky still
        // under the pointer, because a change of RA at the pole is itself a rotation. Deleting it is
        // exactly what made the field spin.
        var s = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = Fov, CenterRA = 16.12, CenterDec = -89.0 };
        Drag(s, 500f, 400f, 600f, 400f);

        var earned = double.RadiansToDegrees(s.CenterRoll);
        Math.Abs(earned).ShouldBeGreaterThan(45.0, "a 100 px pan at Dec -89 rolls the frame a long way");

        SettleAfterRelease(s);
        double.RadiansToDegrees(s.CenterRoll).ShouldBe(earned, 1e-6, "and nothing may take it back unasked");
    }

    [Fact]
    public void TheUserCanAlwaysGetBackToNorthUp()
    {
        // The counterpart of the rule above: since nothing re-levels on its own, there has to be a
        // deliberate way back, or a pole pan leaves the atlas permanently tilted. (The L key.)
        var s = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = Fov, CenterRA = 16.12, CenterDec = -89.0 };
        Drag(s, 500f, 400f, 600f, 400f);
        s.IsDragging = false;

        s.RequestLevelToReference();
        for (var frame = 0; frame < 600 && s.IsLevelling; frame++)
        {
            s.UpdateRollForReference(deltaSeconds: FrameSeconds);
        }

        s.CenterRoll.ShouldBe(0.0, 1e-9, "north-up is roll 0 in Equatorial mode, at every declination");
    }

    [Fact]
    public void RollIsNeverTakenBackMidGesture()
    {
        // The realign must not fight a drag that is still in progress either -- that was the earlier
        // failure mode, a 63 degree jump the instant the drag left the old lock cone.
        var s = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = Fov, CenterRA = 16.12, CenterDec = -89.0 };
        Drag(s, 500f, 400f, 600f, 400f); // leaves IsDragging true, as a mid-gesture move does
        var midGesture = s.CenterRoll;

        s.UpdateRollForReference(deltaSeconds: FrameSeconds).ShouldBeFalse("a pan owns the roll while it is happening");
        s.CenterRoll.ShouldBe(midGesture, 1e-12);
    }
}
