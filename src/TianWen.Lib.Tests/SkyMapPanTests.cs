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
    // Alt/Az keeps the horizon parallel to the window and pans as Stellarium's alt-az mode does (the owner, 2026-10-12 and
    // 2026-10-19): the drag moves the view in azimuth and altitude, so the frame is level throughout and the sky goes the
    // way the drag goes, the grabbed point close under the pointer low in the sky (within 15 percent of the drag: exact at
    // the centre, off it the projection's own curvature). Near the zenith, InAltAz_ADragUpToAndPastTheZenithIsCalmAndStopsAtIt,
    // InAltAz_ASidewaysDragJustUnderTheZenithTurnsInProportion and InAltAz_FromTheZenithADragUpLeavesItAndADragDownHoldsIt.
    [InlineData(10.0, 430f, 330f, 610f, 455f, "well down the sky")]    // diagonally, so a sign error on either axis shows
    [InlineData(-30.0, 430f, 330f, 610f, 455f, "near the horizon")]
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
        Vector2.Distance(new Vector2(x, y), new Vector2(toX, toY)).ShouldBeLessThan(0.15f * dragged.Length(),
            $"and landed close under the pointer ({where}): ({x:F1}, {y:F1}) against ({toX}, {toY})");

        // And nothing moves once the gesture is over.
        for (var frame = 0; frame < 600; frame++)
        {
            s.UpdateRollForReference(zx, zy, zz, FrameSeconds);
        }
        SkyMapProjection.ProjectWithMatrix(grabRA, grabDec, ViewMatrix(s), ppr, cx, cy, out var settledX, out var settledY).ShouldBeTrue();
        Math.Sqrt(Math.Pow(settledX - x, 2) + Math.Pow(settledY - y, 2))
            .ShouldBeLessThan(0.5, $"and it must not drift once the gesture is over ({where})");
    }

    /// <summary>
    /// At the zenith a drag that pulls the sky up (the pointer moving up) leaves it, toward the azimuth the view faced, with the horizon
    /// level; one that pulls it down asks for an altitude past 90 and is held at the zenith, never over the top.
    /// </summary>
    [Fact]
    public void InAltAz_FromTheZenithADragUpLeavesItAndADragDownHoldsIt()
    {
        var (zx, zy, zz) = SkyMapState.RaDecToUnitVec(8.0, 40.0);
        var zenith = new Vector3(zx, zy, zz);
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 8.0, CenterDec = 30.0 };
        s.UpdateRollForReference(zx, zy, zz, FrameSeconds);
        DragThrough(s, zenith, 500f, 100f, [(500f, 400f)]);
        var (az0, alt0) = AzAlt(s, zenith);
        double.RadiansToDegrees(alt0).ShouldBe(90.0, 0.01, "the drag up to the zenith stops at it");

        DragThrough(s, zenith, 500f, 400f, [(500f, 600f)]);
        double.RadiansToDegrees(AzAlt(s, zenith).Alt).ShouldBe(90.0, 0.01, "a drag down at the zenith is held there");

        DragThrough(s, zenith, 500f, 500f, [(500f, 300f)]);
        var (az, alt) = AzAlt(s, zenith);
        double.RadiansToDegrees(alt).ShouldBeLessThan(80.0, "a drag up leaves the zenith");
        double.RadiansToDegrees(SkyMapState.NormalizeSignedAngle(az - az0)).ShouldBe(0.0, 0.01, "toward the azimuth the view faced");
        HorizonTiltDeg(s, zenith).ShouldBeLessThan(0.05, "with the horizon level");
    }

    // A level view's azimuth (radians, from north through east) and altitude, from its forward and up together, which name the
    // azimuth even with the centre at the zenith.
    private static (double Az, double Alt) AzAlt(SkyMapState s, Vector3 zenith)
    {
        var m = ViewMatrix(s);
        var forward = new Vector3(-m.M31, -m.M32, -m.M33);
        var up = new Vector3(m.M21, m.M22, m.M23);
        var alt = Math.Asin(Math.Clamp(Vector3.Dot(forward, zenith), -1f, 1f));
        var level = Vector3.Normalize(forward * (float)Math.Cos(alt) - up * (float)Math.Sin(alt));
        var north = Vector3.Normalize(Vector3.UnitZ - zenith * zenith.Z);
        var east = Vector3.Cross(north, zenith);
        return (Math.Atan2(Vector3.Dot(level, east), Vector3.Dot(level, north)), alt);
    }

    // One press and a pointer that moves to each of (x, y) in turn, as the app hands every move of a drag to the state: each
    // step measured from the press, through the frame the press found. Returns the largest turn of screen-up between two moves.
    private static double DragThrough(SkyMapState s, Vector3 zenith, float fromX, float fromY, (float X, float Y)[] path)
    {
        var ppr = SkyMapProjection.PixelsPerRadian(ContentH, s.FieldOfViewDeg);
        float cx = ContentW * 0.5f, cy = ContentH * 0.5f;
        var startMatrix = ViewMatrix(s);
        s.IsDragging = true;
        s.DragStart = (fromX, fromY);
        s.DragStartCenter = (s.CenterRA, s.CenterDec);
        s.DragStartViewMatrix = startMatrix;
        var (ra1, dec1) = SkyMapProjection.UnprojectWithMatrix(fromX, fromY, startMatrix, ppr, cx, cy);
        var g = SkyMapState.RaDecToUnitVec(ra1, dec1);
        var grabbed = new Vector3(g.X, g.Y, g.Z);
        var previousUp = new Vector3(startMatrix.M21, startMatrix.M22, startMatrix.M23);
        var worst = 0.0;
        foreach (var (x, y) in path)
        {
            var (ra2, dec2) = SkyMapProjection.UnprojectWithMatrix(x, y, startMatrix, ppr, cx, cy);
            var p = SkyMapState.RaDecToUnitVec(ra2, dec2);
            s.Pan(grabbed, new Vector3(p.X, p.Y, p.Z));
            var m = ViewMatrix(s);
            var up = new Vector3(m.M21, m.M22, m.M23);
            worst = Math.Max(worst, double.RadiansToDegrees(Math.Acos(Math.Clamp(Vector3.Dot(up, previousUp), -1f, 1f))));
            previousUp = up;
        }
        s.IsDragging = false;
        return worst;
    }

    /// <summary>
    /// The owner's gesture (2026-10-19): a slow drag from low in the sky up toward and past the zenith, which spun the view
    /// "violently". Stellarium's alt-az way: the altitude rises with the drag and STOPS at the zenith, never going over the top, and
    /// the view never turns more than a few degrees between two moves of the pointer; the azimuth it looked along is the one it
    /// still looks along, and the horizon is level throughout.
    /// </summary>
    [Fact]
    public void InAltAz_ADragUpToAndPastTheZenithIsCalmAndStopsAtIt()
    {
        var (zx, zy, zz) = SkyMapState.RaDecToUnitVec(8.0, 40.0);
        var zenith = new Vector3(zx, zy, zz);
        // Seventy degrees up, facing south (the zenith's RA, twenty degrees of Dec below it).
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 8.0, CenterDec = 20.0 };
        s.UpdateRollForReference(zx, zy, zz, FrameSeconds);
        var (az0, alt0) = AzAlt(s, zenith);

        // Pulling the sky down the whole window, a pixel at a time: about 45 degrees, past the zenith by 25.
        var path = new (float X, float Y)[600];
        for (var i = 0; i < path.Length; i++)
        {
            path[i] = (500f, 100f + i + 1);
        }
        var worst = DragThrough(s, zenith, 500f, 100f, path);
        var (az, alt) = AzAlt(s, zenith);
        System.Console.WriteLine($"from alt {double.RadiansToDegrees(alt0):F1} az {double.RadiansToDegrees(az0):F1}: to alt {double.RadiansToDegrees(alt):F2} az {double.RadiansToDegrees(az):F1}, worst turn between moves {worst:F2} deg");

        worst.ShouldBeLessThan(2.0, "the view turned violently between two moves of the pointer");
        double.RadiansToDegrees(alt).ShouldBe(90.0, 0.01, "the drag stops at the zenith, never over the top");
        double.RadiansToDegrees(SkyMapState.NormalizeSignedAngle(az - az0)).ShouldBe(0.0, 0.01, "the view still faces the way it did");
    }

    /// <summary>
    /// Just under the zenith a sideways drag turns the view in proportion to the drag, as Stellarium does: at most twice the drag's own
    /// angle in azimuth, never the half turn it took to keep the grabbed star exactly under the pointer there.
    /// </summary>
    [Fact]
    public void InAltAz_ASidewaysDragJustUnderTheZenithTurnsInProportion()
    {
        var (zx, zy, zz) = SkyMapState.RaDecToUnitVec(8.0, 40.0);
        var zenith = new Vector3(zx, zy, zz);
        // Two degrees under the zenith, facing south.
        var s = new SkyMapState { Mode = SkyMapMode.Horizon, FieldOfViewDeg = Fov, CenterRA = 8.0, CenterDec = 38.0 };
        s.UpdateRollForReference(zx, zy, zz, FrameSeconds);
        var (az0, _) = AzAlt(s, zenith);

        var path = new (float X, float Y)[100];
        for (var i = 0; i < path.Length; i++)
        {
            path[i] = (450f + i + 1, 300f);
        }
        var worst = DragThrough(s, zenith, 450f, 300f, path);
        var (az, alt) = AzAlt(s, zenith);
        var dragDeg = double.RadiansToDegrees(100.0 / SkyMapProjection.PixelsPerRadian(ContentH, Fov));
        var turned = double.RadiansToDegrees(Math.Abs(SkyMapState.NormalizeSignedAngle(az - az0)));
        System.Console.WriteLine($"a {dragDeg:F2} deg sideways drag at alt {double.RadiansToDegrees(alt):F1}: azimuth turned {turned:F2} deg, worst turn between moves {worst:F2} deg");

        // A 1 percent margin: the drag's angle on screen is not exactly its pixels over the scale.
        turned.ShouldBeLessThanOrEqualTo(2.02 * dragDeg, "a sideways drag under the zenith turned the view out of proportion");
        turned.ShouldBeGreaterThan(0.5 * dragDeg, "and it still turns");
        worst.ShouldBeLessThan(2.0, "the view turned violently between two moves of the pointer");
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
