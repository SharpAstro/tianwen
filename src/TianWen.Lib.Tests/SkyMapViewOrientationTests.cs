using System;
using System.Numerics;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Orientation has three degrees of freedom and the view centre carries two. The missing one used to
/// be re-derived per frame as <c>forward x reference</c>, which is singular where the view axis meets
/// the reference: the cross product's length goes to zero, so a small pan swings the field, and at
/// exact parallelism the code substituted a hardcoded right vector. <c>CenterRoll</c> stores it
/// instead, so no view direction is special.
/// </summary>
public class SkyMapViewOrientationTests
{
    // The roll realigns at a rate per SECOND, so every step here states the frame time it is
    // simulating; measuring it would give ~0 in a tight loop and the approach would never land.
    private const double FrameSeconds = 1.0 / 60.0;

    private static void ShouldBeOrthonormalRotation(Matrix4x4 m, string because)
    {
        var right = new Vector3(m.M11, m.M12, m.M13);
        var up = new Vector3(m.M21, m.M22, m.M23);
        var back = new Vector3(m.M31, m.M32, m.M33);

        foreach (var (v, name) in new[] { (right, "right"), (up, "up"), (back, "back") })
        {
            float.IsFinite(v.X).ShouldBeTrue($"{name}.X must be finite: {because}");
            v.Length().ShouldBe(1f, 1e-4f, $"{name} must be unit length: {because}");
        }

        Vector3.Dot(right, up).ShouldBe(0f, 1e-4f, $"right must be perpendicular to up: {because}");
        Vector3.Dot(right, back).ShouldBe(0f, 1e-4f, $"right must be perpendicular to back: {because}");
        Vector3.Dot(up, back).ShouldBe(0f, 1e-4f, $"up must be perpendicular to back: {because}");
    }

    // The construction the code used before CenterRoll existed: right = normalize(forward x
    // reference), up = right x forward. Kept here as the oracle, so the new matrix is pinned to the
    // old one wherever the old one was well-conditioned, i.e. no visible change in ordinary use.
    private static Matrix4x4 LegacyReferenceMatrix(double raHours, double decDeg, Vector3 reference)
    {
        var (sinRA, cosRA) = Math.SinCos(raHours * (Math.PI / 12.0));
        var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(decDeg));
        var forward = new Vector3((float)(cosDec * cosRA), (float)(cosDec * sinRA), (float)sinDec);

        var right = Vector3.Normalize(Vector3.Cross(forward, reference));
        var up = Vector3.Cross(right, forward);
        return new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            -forward.X, -forward.Y, -forward.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(6.0, 45.0)]
    [InlineData(13.7, -30.0)]
    [InlineData(18.0, 80.0)]
    [InlineData(23.9, -89.0)]
    public void EquatorialRollZero_MatchesTheOldReferenceConstruction(double ra, double dec)
    {
        // roll 0 IS north-up at every declination: forward x zhat equals cosDec times the roll-0
        // right vector, so normalising it lands on exactly the same frame. This is what makes the
        // rewrite safe rather than a change of look.
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = ra, CenterDec = dec };

        // In Equatorial mode the refresh lands on roll 0 whether or not it ran: outside the lock cone
        // it solves for 0, and inside it holds the 0 the view started at. Dec -89 is inside the cone,
        // which is why this asserts the roll and not the return value.
        state.UpdateRollForReference(deltaSeconds: FrameSeconds);
        state.CenterRoll.ShouldBe(0.0, 1e-6);

        var actual = state.ComputeViewMatrix();
        var expected = LegacyReferenceMatrix(ra, dec, new Vector3(0f, 0f, 1f));

        actual.M11.ShouldBe(expected.M11, 1e-4f);
        actual.M12.ShouldBe(expected.M12, 1e-4f);
        actual.M13.ShouldBe(expected.M13, 1e-4f);
        actual.M21.ShouldBe(expected.M21, 1e-4f);
        actual.M22.ShouldBe(expected.M22, 1e-4f);
        actual.M23.ShouldBe(expected.M23, 1e-4f);
        actual.M31.ShouldBe(expected.M31, 1e-4f);
        actual.M32.ShouldBe(expected.M32, 1e-4f);
        actual.M33.ShouldBe(expected.M33, 1e-4f);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(80.0)]
    [InlineData(86.0)]
    [InlineData(-89.5)]
    [InlineData(90.0)]
    public void EquatorialMode_HasNoLockCone_BecauseNorthUpIsRollZeroEverywhere(double dec)
    {
        // Equatorial mode's answer is analytically 0 at every declination, the pole included, so
        // there is nothing ill-conditioned to protect against and it gets no cone. Giving it one was
        // a real defect: a pan held its rigid roll inside the cone and then snapped it away on the
        // way out, flipping the field by 63 degrees in one frame at Dec 85.
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = dec };
        state.RequestLevelToReference();
        state.UpdateRollForReference(deltaSeconds: FrameSeconds).ShouldBeTrue("Equatorial mode always knows where north is");
        state.CenterRoll.ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void WhileDragging_TheRollIsHeld_SoThePanCannotBeFoughtMidGesture()
    {
        // The pan rotates the whole frame rigidly and derives the roll from it. Re-levelling
        // underneath it is what produced the reported flip.
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = 6.0,
            CenterDec = 85.0,
            CenterRoll = 1.1,
            IsDragging = true,
        };

        state.UpdateRollForReference(deltaSeconds: FrameSeconds).ShouldBeFalse();
        state.CenterRoll.ShouldBe(1.1, 1e-9);
    }

    [Fact]
    public void AskedToLevel_TheRollTravelsBackToLevel_WithoutSnapping()
    {
        // A rolled view re-levels over several frames, taking the SHORT way round, and lands exactly.
        // One frame of it must be a small fraction of the journey, or it is the flip again.
        //
        // Note what triggers it: the USER asking (the L key). This travel used to run unbidden on
        // every frame, which is what undid a pan the instant the button came up.
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = 6.0,
            CenterDec = 85.0,
            CenterRoll = double.DegreesToRadians(63.0), // the angle measured off the report
        };

        state.RequestLevelToReference();
        state.UpdateRollForReference(deltaSeconds: FrameSeconds).ShouldBeTrue();
        var afterOneFrame = double.RadiansToDegrees(state.CenterRoll);
        afterOneFrame.ShouldBeLessThan(63.0, "it must move toward level");
        afterOneFrame.ShouldBeGreaterThan(30.0, "but nothing like all the way, or it is a flip");
        state.NeedsRedraw.ShouldBeTrue("the approach needs the next frame to continue");

        var previous = double.MaxValue;
        for (var frame = 0; frame < 200 && Math.Abs(state.CenterRoll) > 1e-9; frame++)
        {
            state.UpdateRollForReference(deltaSeconds: FrameSeconds);
            var current = Math.Abs(state.CenterRoll);
            current.ShouldBeLessThan(previous, "the approach must be monotonic, never overshoot");
            previous = current;
        }

        state.CenterRoll.ShouldBe(0.0, 1e-9, "it has to land exactly, not creep forever");
    }

    [Fact]
    public void TheRollTravelsForTheSameDURATIONWhateverTheFrameRate()
    {
        // The step used to be a flat fraction of the remaining angle PER CALL, so the travel took a
        // fixed number of frames and a duration that scaled with frame time: a settle that reads as
        // instant at 60 fps takes six times as long at 10 fps, which is the reported "it keeps
        // rotating after I let go" on the slower web build. Same simulated half second, two frame
        // rates, same answer.
        const double startRoll = 63.0;
        const double halfSecond = 0.5;

        var fast = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = 85.0, CenterRoll = double.DegreesToRadians(startRoll) };
        fast.RequestLevelToReference();
        for (var i = 0; i < 30; i++)
        {
            fast.UpdateRollForReference(deltaSeconds: halfSecond / 30);
        }

        var slow = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = 85.0, CenterRoll = double.DegreesToRadians(startRoll) };
        slow.RequestLevelToReference();
        for (var i = 0; i < 5; i++)
        {
            slow.UpdateRollForReference(deltaSeconds: halfSecond / 5);
        }

        // Both have had half a second, so both are level; the point is that they agree, not the value.
        Math.Abs(fast.CenterRoll).ShouldBeLessThan(1e-6);
        Math.Abs(slow.CenterRoll).ShouldBeLessThan(1e-6);

        // And partway through, where the difference would actually show, they still track each other.
        var fastPartial = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = 85.0, CenterRoll = double.DegreesToRadians(startRoll) };
        fastPartial.RequestLevelToReference();
        for (var i = 0; i < 6; i++)
        {
            fastPartial.UpdateRollForReference(deltaSeconds: 1.0 / 60);
        }

        var slowPartial = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = 85.0, CenterRoll = double.DegreesToRadians(startRoll) };
        slowPartial.RequestLevelToReference();
        slowPartial.UpdateRollForReference(deltaSeconds: 6.0 / 60);

        double.RadiansToDegrees(fastPartial.CenterRoll)
            .ShouldBe(double.RadiansToDegrees(slowPartial.CenterRoll), tolerance: 0.5,
                "one 100 ms frame and six 16.7 ms frames are the same 100 ms of travel");
    }

    [Fact]
    public void AMeasuredStepIsUsedWhenTheCallerDoesNotSupplyOne()
    {
        // The render loop passes nothing and gets a measured interval. Two back-to-back calls measure
        // almost no time, so the roll barely moves -- correct, and the reason the tests above have to
        // state a frame time rather than relying on the default.
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = 6.0,
            CenterDec = 85.0,
            CenterRoll = double.DegreesToRadians(63.0),
        };
        state.RequestLevelToReference();

        // First call has no previous instant, so it takes the NOMINAL frame (1/60 s). That is exact and
        // owes nothing to the clock: 63 * exp(-(1/60) / 0.058) = 47.27 degrees remaining.
        state.UpdateRollForReference().ShouldBeTrue();
        var afterFirst = double.RadiansToDegrees(state.CenterRoll);
        afterFirst.ShouldBe(63.0 * Math.Exp(-(1.0 / 60.0) / 0.058), 0.01);

        // Immediately again: now it MEASURES, so how far it travels depends on how long the machine
        // took to get here -- which under a loaded full-suite run is milliseconds, not microseconds.
        // Pinning a tight delta here made this flaky by design, so assert only what is true at any
        // scheduling: it keeps approaching and never overshoots past the target.
        state.UpdateRollForReference().ShouldBeTrue();
        var afterSecond = double.RadiansToDegrees(state.CenterRoll);
        afterSecond.ShouldBeLessThan(afterFirst, "a measured interval is still an interval, so it moves");
        afterSecond.ShouldBeGreaterThanOrEqualTo(0.0, "and however long the gap, it may not sail past level");
    }

    [Theory]
    [InlineData(-170.0)]
    [InlineData(170.0)]
    [InlineData(-95.0)]
    [InlineData(20.0)]
    public void TheApproach_TakesTheShortWayRound_AndNeverLeaps(double startRollDeg)
    {
        // From -170 the short way to 0 is +170 (up through -127), not -190. So the property to pin is
        // the signed one: the step shrinks the wrapped distance to the target, and no single step
        // covers more than its share of a half turn.
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRoll = double.DegreesToRadians(startRollDeg),
        };
        state.RequestLevelToReference();

        var before = Math.Abs(startRollDeg);
        state.UpdateRollForReference(deltaSeconds: FrameSeconds);
        var after = Math.Abs(double.RadiansToDegrees(state.CenterRoll));

        after.ShouldBeLessThan(before, "one step must close some of the distance to level");
        (before - after).ShouldBeLessThanOrEqualTo(180.0 * 0.25 + 1e-6,
            "and no step may cover more than its fraction of a half turn, or it reads as a flip");
    }

    [Fact]
    public void AlreadyLevel_IsUntouched_AndAsksForNoExtraFrame()
    {
        // The common case by far: nobody asked for a re-level and celestial north has not moved, so
        // the frame must cost nothing at all -- no roll change and no request for another frame.
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 3.0, CenterDec = 20.0 };
        state.NeedsRedraw = false;

        state.UpdateRollForReference(deltaSeconds: FrameSeconds).ShouldBeFalse("nothing is travelling");

        state.CenterRoll.ShouldBe(0.0);
        state.NeedsRedraw.ShouldBeFalse("a level view is not travelling anywhere");
    }

    [Fact]
    public void InHorizonMode_TheRollFOLLOWSTheZenithAsTheSkyTurns_ButOnlyByHowFarItMoved()
    {
        // Horizon mode's reference really does move (the sky turns ~15 deg/hr), and keeping the
        // horizon level over a session is the one thing the per-frame realign is entitled to do.
        // It must do it by tracking the reference's MOTION, never by servoing to its absolute value:
        // servoing is what dragged a panned view back and made the sky slide after mouse-up.
        var state = new SkyMapState { Mode = SkyMapMode.Horizon, CenterRA = 8.0, CenterDec = 10.0 };
        var zenith = SkyMapState.RaDecToUnitVec(4.0, 40.0);
        state.RequestLevelToReference();
        for (var frame = 0; frame < 200; frame++)
        {
            state.UpdateRollForReference(zenith.X, zenith.Y, zenith.Z, FrameSeconds);
        }
        var levelRoll = state.CenterRoll;

        // A gesture rolls the frame off level. Nothing may take that back on its own.
        state.CenterRoll = levelRoll + double.DegreesToRadians(30.0);
        state.UpdateRollForReference(zenith.X, zenith.Y, zenith.Z, FrameSeconds);
        double.RadiansToDegrees(state.CenterRoll - levelRoll)
            .ShouldBe(30.0, 1e-6, "a static zenith must leave the gesture's roll exactly alone");

        // Now the sky turns. The roll follows it, carrying the gesture's 30 degrees along.
        var rolled = state.CenterRoll;
        var moved = SkyMapState.RaDecToUnitVec(4.25, 40.0); // a quarter hour of RA later
        state.UpdateRollForReference(moved.X, moved.Y, moved.Z, FrameSeconds);
        var followed = state.CenterRoll - rolled;
        Math.Abs(followed).ShouldBeGreaterThan(1e-6, "the horizon must stay level as the sky turns");

        // And it followed by exactly the reference's own motion, so the 30 degree offset survives.
        var reference = new SkyMapState { Mode = SkyMapMode.Horizon, CenterRA = 8.0, CenterDec = 10.0 };
        reference.RequestLevelToReference();
        for (var frame = 0; frame < 200; frame++)
        {
            reference.UpdateRollForReference(moved.X, moved.Y, moved.Z, FrameSeconds);
        }
        double.RadiansToDegrees(state.CenterRoll - reference.CenterRoll)
            .ShouldBe(30.0, 1e-3, "the gesture's offset rides along, it is not eaten by the tracking");
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    [InlineData(89.5)]
    public void AtThePole_TheMatrixIsStillARotation(double dec)
    {
        // The old construction divided by a vanishing length here and fell back to a hardcoded
        // right vector at exactly +/-90, which is a visible discontinuity.
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 7.5, CenterDec = dec };
        ShouldBeOrthonormalRotation(state.ComputeViewMatrix(), $"Dec {dec}");
    }

    [Fact]
    public void AtTheZenith_TheRollIsHeldRatherThanRecomputed()
    {
        // Horizon mode's reference is the local zenith, and unlike the pole in Equatorial mode the
        // view CAN point exactly at it. Inside the lock cone the reference cannot name an up
        // direction, so the roll must be kept, not solved for.
        var zenith = SkyMapState.RaDecToUnitVec(4.0, 40.0);
        var state = new SkyMapState
        {
            Mode = SkyMapMode.Horizon,
            CenterRA = 4.0,
            CenterDec = 40.0,
            CenterRoll = 0.75,
        };

        state.UpdateRollForReference(zenith.X, zenith.Y, zenith.Z, FrameSeconds)
            .ShouldBeFalse("the view axis is the reference, so there is no up to derive");
        state.CenterRoll.ShouldBe(0.75, 1e-9, "the last good roll must survive");
        ShouldBeOrthonormalRotation(state.ComputeViewMatrix(), "centre at the zenith");
    }

    [Fact]
    public void AwayFromTheZenith_TheRollPutsTheZenithUp()
    {
        var zenith = SkyMapState.RaDecToUnitVec(4.0, 40.0);
        var state = new SkyMapState { Mode = SkyMapMode.Horizon, CenterRA = 8.0, CenterDec = 10.0 };
        state.RequestLevelToReference(); // entering the mode establishes its frame (the P key does this)

        // The roll APPROACHES its target a fraction per frame, so settle it before measuring; one
        // call is deliberately only part of the way.
        for (var frame = 0; frame < 200; frame++)
        {
            state.UpdateRollForReference(zenith.X, zenith.Y, zenith.Z, FrameSeconds);
        }

        var m = state.ComputeViewMatrix();
        ShouldBeOrthonormalRotation(m, "horizon mode away from the zenith");

        // Screen-up must lie in the plane spanned by the view axis and the zenith, on the zenith's
        // side: that is what "the horizon stays level" means.
        var forward = new Vector3(-m.M31, -m.M32, -m.M33);
        var up = new Vector3(m.M21, m.M22, m.M23);
        var zenithVec = new Vector3(zenith.X, zenith.Y, zenith.Z);
        var expectedUp = Vector3.Normalize(zenithVec - forward * Vector3.Dot(zenithVec, forward));
        Vector3.Dot(up, expectedUp).ShouldBe(1f, 1e-3f);
    }

    [Fact]
    public void NoUsableReference_KeepsTheRollInsteadOfPickingOne()
    {
        // An invalid site hands Horizon mode a zero-length zenith. That used to reach the arbitrary
        // right vector; now it simply leaves the view as it is.
        var state = new SkyMapState { Mode = SkyMapMode.Horizon, CenterRA = 2.0, CenterDec = 20.0, CenterRoll = -0.3 };
        state.UpdateRollForReference(0f, 0f, 0f, FrameSeconds).ShouldBeFalse();
        state.CenterRoll.ShouldBe(-0.3, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(11.0, 60.0, 1.2)]
    [InlineData(19.5, -75.0, -2.9)]
    [InlineData(3.0, 89.9, 0.4)]
    [InlineData(3.0, 90.0, 0.4)]
    public void FrameToCenter_RoundTripsComputeViewMatrix(double ra, double dec, double roll)
    {
        // The pan gesture rotates the frame rigidly and then has to store it back into three
        // scalars, so the decomposition has to be exact, including at the pole where RA and roll
        // trade off against each other.
        var state = new SkyMapState { CenterRA = ra, CenterDec = dec, CenterRoll = roll };
        var m = state.ComputeViewMatrix();

        var forward = new Vector3(-m.M31, -m.M32, -m.M33);
        var right = new Vector3(m.M11, m.M12, m.M13);
        var (ra2, dec2, roll2) = SkyMapState.FrameToCenter(forward, right);

        var rebuilt = new SkyMapState { CenterRA = ra2, CenterDec = dec2, CenterRoll = roll2 }.ComputeViewMatrix();
        rebuilt.M11.ShouldBe(m.M11, 1e-4f);
        rebuilt.M12.ShouldBe(m.M12, 1e-4f);
        rebuilt.M13.ShouldBe(m.M13, 1e-4f);
        rebuilt.M21.ShouldBe(m.M21, 1e-4f);
        rebuilt.M22.ShouldBe(m.M22, 1e-4f);
        rebuilt.M23.ShouldBe(m.M23, 1e-4f);
        rebuilt.M31.ShouldBe(m.M31, 1e-4f);
        rebuilt.M32.ShouldBe(m.M32, 1e-4f);
        rebuilt.M33.ShouldBe(m.M33, 1e-4f);
    }

    [Fact]
    public void ARigidRotationNearThePole_DoesNotSwingTheField()
    {
        // The reported symptom, as a number. Rotate a near-pole view by a small angle the way a pan
        // does, store it through the three scalars, and the field must have turned by that same small
        // angle. Deriving the roll from the reference instead amplifies it by roughly 1 / cos(Dec),
        // which at Dec 89.5 is a factor of about 115.
        var start = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 6.0, CenterDec = 89.5 };
        var m0 = start.ComputeViewMatrix();
        var forward0 = new Vector3(-m0.M31, -m0.M32, -m0.M33);
        var right0 = new Vector3(m0.M11, m0.M12, m0.M13);

        // A 0.1 degree pan across the pole's neighbourhood, about an axis in the view plane.
        const float panDeg = 0.1f;
        var q = Quaternion.CreateFromAxisAngle(right0, float.DegreesToRadians(panDeg));
        var forward1 = Vector3.Transform(forward0, q);
        var right1 = Vector3.Transform(right0, q);

        var (ra, dec, roll) = SkyMapState.FrameToCenter(forward1, right1);
        var end = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = ra, CenterDec = dec, CenterRoll = roll };
        var m1 = end.ComputeViewMatrix();

        // Angle between the two "up" vectors is how much the field appears to rotate.
        var up0 = new Vector3(m0.M21, m0.M22, m0.M23);
        var up1 = new Vector3(m1.M21, m1.M22, m1.M23);
        var swingDeg = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(up0, up1), -1f, 1f)));

        swingDeg.ShouldBeLessThan(panDeg * 1.5f,
            "a rigid pan must turn the field by the pan angle, not by an amplified one");
    }
}
