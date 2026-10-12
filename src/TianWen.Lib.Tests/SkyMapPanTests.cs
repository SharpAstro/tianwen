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
    /// A press at (fromX, fromY) and a move to (toX, toY), as SkyMapTab.HandleDrag hands them to
    /// <see cref="SkyMapState.Pan"/>: both points unprojected through the frame the press found. The step itself is
    /// the state's own code, so nothing here can agree with a copy the tab no longer runs.
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
        var grabbed = SkyMapState.RaDecToUnitVec(ra1, dec1);
        var underPointer = SkyMapState.RaDecToUnitVec(ra2, dec2);
        s.Pan(new Vector3(grabbed.X, grabbed.Y, grabbed.Z), new Vector3(underPointer.X, underPointer.Y, underPointer.Z));
    }

    /// <summary>Screen-up lies in the plane of the view axis and the zenith, on the zenith's side: a level horizon.</summary>
    private static double HorizonTiltDeg(SkyMapState s, Vector3 zenith)
    {
        var m = ViewMatrix(s);
        var forward = new Vector3(-m.M31, -m.M32, -m.M33);
        var up = new Vector3(m.M21, m.M22, m.M23);
        var expectedUp = Vector3.Normalize(zenith - forward * Vector3.Dot(zenith, forward));
        return double.RadiansToDegrees(Math.Acos(Math.Clamp(Vector3.Dot(up, expectedUp), -1f, 1f)));
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
    // Alt/Az keeps the horizon parallel to the window, as Stellarium's alt-az mode does (the owner, 2026-10-12): a
    // pan solves for the LEVEL frame that puts the grabbed point exactly under the pointer, so the frame is level
    // throughout and the sky still goes exactly where the drag takes it. It used to be a rigid rotation that tilted
    // the horizon by whatever the gesture earned and kept it.
    //
    // From the zenith a drag leaves it by pulling the sky in TOWARD the centre: overhead, a point's altitude is its
    // distance from the centre, so a drag outward would ask for an altitude past 90 and is held at the zenith
    // (InAltAz_ASidewaysDragAtTheZenithTurnsTheViewAboutIt covers what it does there).
    [InlineData(40.0, 300f, 300f, 450f, 370f, "from the zenith")]      // the site's zenith Dec: straight overhead
    [InlineData(38.0, 300f, 300f, 450f, 370f, "just off the zenith")]  // inside the lock cone, where the zenith names no up
    [InlineData(10.0, 430f, 330f, 610f, 455f, "well down the sky")]    // diagonally, so a sign error on either axis shows
    public void InAltAz_APanKeepsTheHorizonLevelAndMovesTheSkyTheWayTheDragWent(
        double startDec, float fromX, float fromY, float toX, float toY, string where)
    {
        // A fixed zenith: over one gesture the sky turns by microdegrees, and the claim here is about the gesture,
        // not the sky's rotation.
        var (zx, zy, zz) = SkyMapState.RaDecToUnitVec(8.0, 40.0);
        var zenith = new Vector3(zx, zy, zz);
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 8.0, CenterDec = startDec };
        s.UpdateRollForReference(zx, zy, zz, FrameSeconds); // the frame before the press, as the app draws one
        var ppr = SkyMapProjection.PixelsPerRadian(ContentH, Fov);
        float cx = ContentW * 0.5f, cy = ContentH * 0.5f;

        var (grabRA, grabDec) = SkyMapProjection.UnprojectWithMatrix(fromX, fromY, ViewMatrix(s), ppr, cx, cy);

        Drag(s, fromX, fromY, toX, toY);
        s.IsDragging = false;

        HorizonTiltDeg(s, zenith).ShouldBeLessThan(0.05, $"the horizon is level after the drag ({where})");

        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var x, out var y).ShouldBeTrue();
        var dragged = new Vector2(toX - fromX, toY - fromY);
        var moved = new Vector2(x - fromX, y - fromY);
        Vector2.Dot(moved, dragged).ShouldBeGreaterThan(0.8f * dragged.LengthSquared(),
            $"the grabbed point went the way the drag went ({where}): to ({x:F0}, {y:F0}) for a drag to ({toX}, {toY})");
        Vector2.Distance(new Vector2(x, y), new Vector2(toX, toY)).ShouldBeLessThan(1f,
            $"and landed under the pointer ({where}): ({x:F1}, {y:F1}) against ({toX}, {toY})");

        // And nothing moves once the gesture is over.
        for (var frame = 0; frame < 600; frame++)
        {
            s.UpdateRollForReference(zx, zy, zz, FrameSeconds);
        }
        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var settledX, out var settledY).ShouldBeTrue();
        Math.Sqrt(Math.Pow(settledX - x, 2) + Math.Pow(settledY - y, 2))
            .ShouldBeLessThan(0.5, $"and it must not drift once the gesture is over ({where})");
    }

    [Fact]
    public void InAltAz_ADragAcrossTheZenithLandsLevelAndUprightOnTheFarSide()
    {
        // Carried right across the zenith, the view must come out the other side with the horizon level and the
        // zenith UP, not upside down: an alt-az mount passing under the zenith turns through half a turn in
        // azimuth, and so does the frame. The rigid pan alone would land inverted here.
        var (zx, zy, zz) = SkyMapState.RaDecToUnitVec(8.0, 40.0);
        var zenith = new Vector3(zx, zy, zz);
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 8.0, CenterDec = 30.0 };
        s.UpdateRollForReference(zx, zy, zz, FrameSeconds);

        // Ten degrees below the zenith, pulling the sky down by about twenty: the centre goes up and over.
        Drag(s, 500f, 140f, 500f, 400f);
        s.IsDragging = false;

        var m = ViewMatrix(s);
        var forward = new Vector3(-m.M31, -m.M32, -m.M33);
        var up = new Vector3(m.M21, m.M22, m.M23);
        Vector3.Dot(forward, zenith).ShouldBeLessThan(MathF.Cos(float.DegreesToRadians(5f)), "the centre left the zenith's cone");
        HorizonTiltDeg(s, zenith).ShouldBeLessThan(0.05, "the horizon is level on the far side");
        Vector3.Dot(up, zenith).ShouldBeGreaterThan(0f, "and screen-up still leans toward the zenith");
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

    // Melbourne, at a local sidereal time of 23.4 h: the zenith as the UBO writer passes it in Horizon mode.
    private static readonly Vector3 Zenith = new(
        (float)(Math.Cos(double.DegreesToRadians(-37.9)) * Math.Cos(23.4 * Math.PI / 12.0)),
        (float)(Math.Cos(double.DegreesToRadians(-37.9)) * Math.Sin(23.4 * Math.PI / 12.0)),
        (float)Math.Sin(double.DegreesToRadians(-37.9)));

    private static void HorizonFrames(SkyMapState s, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            s.UpdateRollForReference(Zenith.X, Zenith.Y, Zenith.Z, FrameSeconds);
        }
    }

    /// <summary>
    /// <b>A level asked for by P does not throw the view round after a drag.</b> Reported as "after switching with P
    /// the view rotates abruptly after dragging": P asks for a level and it eases in over a few frames, but a drag
    /// begun before it arrives held it, and the release resumed it from wherever the drag had left the frame, an
    /// absolute servo the gesture never asked for. Inside the zenith's lock cone the level cannot start at all, so
    /// P pressed there and a drag out of it did the same, by more.
    /// </summary>
    [Theory]
    [InlineData(17.04, -30.0, "a drag begun two frames after P")]   // measured: 74 degrees after the release
    [InlineData(23.4, -36.0, "P inside the zenith's lock cone")]    // measured: 128 degrees after the release
    public void ADragAbandonsALevelAskedForBeforeIt(double ra, double dec, string where)
    {
        var s = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = Fov, CenterRA = ra, CenterDec = dec };
        s.Mode = SkyMapMode.Horizon; // what P does
        s.RequestLevelToReference();
        HorizonFrames(s, 2);
        s.IsLevelling.ShouldBeTrue($"the level has not arrived yet ({where})");

        Drag(s, 500f, 400f, 740f, 560f); // leaves IsDragging true, as a mid-gesture move does
        HorizonFrames(s, 1);             // the frame the move asks for
        s.IsLevelling.ShouldBeFalse($"the drag moved the view, so it owns the roll now ({where})");

        var atRelease = s.CenterRoll;
        s.IsDragging = false;
        HorizonFrames(s, 120);
        double.RadiansToDegrees(s.CenterRoll).ShouldBe(double.RadiansToDegrees(atRelease), 1e-6,
            $"nothing may turn the view once the drag is over ({where})");
    }

    [Fact]
    public void AClickThatDoesNotMoveTheViewLetsTheLevelFinish()
    {
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 17.04, CenterDec = -30.0 };
        s.RequestLevelToReference();
        HorizonFrames(s, 2);

        // A press and a release with no move between them: the centre never leaves where the press found it.
        s.IsDragging = true;
        s.DragStart = (500f, 400f);
        s.DragStartCenter = (s.CenterRA, s.CenterDec);
        HorizonFrames(s, 1);
        s.IsLevelling.ShouldBeTrue("a click is not a gesture on the view");
        s.IsDragging = false;

        HorizonFrames(s, 120);
        s.IsLevelling.ShouldBeFalse("and the level P asked for arrives");
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
