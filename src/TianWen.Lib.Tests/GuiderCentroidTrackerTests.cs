using Shouldly;
using System;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Guider")]
public class GuiderCentroidTrackerTests(ITestOutputHelper output)
{
    [Fact]
    public void GivenStarFieldWhenFirstFrameThenAcquiresGuideStars()
    {
        var frame = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 10, seed: 42);

        var tracker = new GuiderCentroidTracker();
        var result = tracker.ProcessFrame(frame);

        result.ShouldNotBeNull();
        tracker.IsAcquired.ShouldBeTrue();
        result.Value.DeltaX.ShouldBe(0, 0.01, "first frame delta should be zero");
        result.Value.DeltaY.ShouldBe(0, 0.01, "first frame delta should be zero");
        result.Value.SNR.ShouldBeGreaterThan(3.0);
        result.Value.Flux.ShouldBeGreaterThan(0);
        result.Value.TrackedStarCount.ShouldBeGreaterThanOrEqualTo(1);
        output.WriteLine($"Acquired {result.Value.TrackedStarCount} guide stars");
    }

    [Fact]
    public void GivenMultipleStarsWhenAcquiredThenMultipleTracked()
    {
        var frame = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 20, seed: 42);

        var tracker = new GuiderCentroidTracker(maxStars: 4);
        var result = tracker.ProcessFrame(frame);

        result.ShouldNotBeNull();
        tracker.TrackedStarCount.ShouldBeGreaterThan(1, "should track multiple stars");
        tracker.TrackedStarCount.ShouldBeLessThanOrEqualTo(4, "should not exceed maxStars");
        output.WriteLine($"Tracking {tracker.TrackedStarCount} guide stars");

        // All tracked stars should have valid positions
        foreach (var star in tracker.Stars)
        {
            star.LastX.ShouldBeGreaterThan(0);
            star.LastY.ShouldBeGreaterThan(0);
            star.SNR.ShouldBeGreaterThan(3.0);
        }
    }

    [Fact]
    public void GivenAcquiredStarsWhenOffsetAppliedThenAverageDeltaReflectsShift()
    {
        // Acquire on first frame (no offset)
        var frame0 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 10, seed: 42);

        var tracker = new GuiderCentroidTracker();
        var result0 = tracker.ProcessFrame(frame0);
        result0.ShouldNotBeNull();
        output.WriteLine($"Acquired {result0.Value.TrackedStarCount} stars");

        // Second frame with 3px X offset, 2px Y offset
        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 3.0, offsetY: 2.0, starCount: 10, seed: 42);

        var result1 = tracker.ProcessFrame(frame1);
        result1.ShouldNotBeNull();

        output.WriteLine($"Delta: ({result1.Value.DeltaX:F3}, {result1.Value.DeltaY:F3})");

        // Averaged delta should reflect the uniform shift
        result1.Value.DeltaX.ShouldBe(3.0, 0.5);
        result1.Value.DeltaY.ShouldBe(2.0, 0.5);
    }

    [Fact]
    public void GivenAcquiredStarWhenSubPixelShiftThenSubPixelDeltaDetected()
    {
        var frame0 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        tracker.ProcessFrame(frame0);

        // Sub-pixel shift of 0.3px
        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0.3, offsetY: -0.2, starCount: 5, seed: 42);

        var result = tracker.ProcessFrame(frame1);
        result.ShouldNotBeNull();

        output.WriteLine($"Sub-pixel delta: ({result.Value.DeltaX:F3}, {result.Value.DeltaY:F3})");

        // Sub-pixel accuracy: should be within 0.3px of true offset
        result.Value.DeltaX.ShouldBe(0.3, 0.3);
        result.Value.DeltaY.ShouldBe(-0.2, 0.3);
    }

    [Fact]
    public void GivenTrackerWhenResetThenRequiresReacquisition()
    {
        var frame = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 10, seed: 42);

        var tracker = new GuiderCentroidTracker();
        tracker.ProcessFrame(frame);
        tracker.IsAcquired.ShouldBeTrue();

        tracker.Reset();
        tracker.IsAcquired.ShouldBeFalse();
        tracker.TrackedStarCount.ShouldBe(0);

        // Next frame should re-acquire (delta = 0)
        var result = tracker.ProcessFrame(frame);
        result.ShouldNotBeNull();
        result.Value.DeltaX.ShouldBe(0, 0.01);
        result.Value.DeltaY.ShouldBe(0, 0.01);
    }

    [Fact]
    public void GivenTrackerWhenSetLockPositionThenDeltaResetsToZero()
    {
        var frame0 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42);

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        tracker.ProcessFrame(frame0);

        // Shift star 5px
        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 5.0, offsetY: 3.0, starCount: 5, seed: 42);

        var result1 = tracker.ProcessFrame(frame1);
        result1.ShouldNotBeNull();
        Math.Abs(result1.Value.DeltaX).ShouldBeGreaterThan(0.5);

        // Re-lock at current position (like after dither)
        tracker.SetLockPosition();

        // Same frame again: delta should be zero now
        var result2 = tracker.ProcessFrame(frame1);
        result2.ShouldNotBeNull();
        result2.Value.DeltaX.ShouldBe(0, 1.0);
        result2.Value.DeltaY.ShouldBe(0, 1.0);
    }

    [Fact]
    public void GivenSeeingWhenTrackingThenStarStillAcquiredAndTracked()
    {
        // Acquire with seeing
        var frame0 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42,
            seeingArcsec: 2.0, pixelScaleArcsec: 1.5);

        var tracker = new GuiderCentroidTracker();
        tracker.ProcessFrame(frame0);
        tracker.IsAcquired.ShouldBeTrue();

        // Track with offset and seeing: verify star is still tracked
        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 2.0, offsetY: -1.5, starCount: 5, seed: 42,
            seeingArcsec: 2.0, pixelScaleArcsec: 1.5);

        var result = tracker.ProcessFrame(frame1);
        result.ShouldNotBeNull();
        result.Value.SNR.ShouldBeGreaterThan(3.0);
        result.Value.Flux.ShouldBeGreaterThan(0);
        // With seeing, centroid accuracy degrades but tracking should detect drift direction
        Math.Abs(result.Value.DeltaX).ShouldBeGreaterThan(0.1);
    }

    [Fact]
    public void GivenMultipleFramesWhenTrackingThenPositionAccumulates()
    {
        var tracker = new GuiderCentroidTracker(maxStars: 1);

        // Frame 0: acquire
        var frame0 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42);
        tracker.ProcessFrame(frame0);

        // Frame 1: drift 1px
        var frame1 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 1.0, offsetY: 0.5, starCount: 5, seed: 42);
        var r1 = tracker.ProcessFrame(frame1);

        // Frame 2: drift 2px total
        var frame2 = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 2.0, offsetY: 1.0, starCount: 5, seed: 42);
        var r2 = tracker.ProcessFrame(frame2);

        r1.ShouldNotBeNull();
        r2.ShouldNotBeNull();

        // Delta is always relative to lock position (frame 0)
        r1.Value.DeltaX.ShouldBe(1.0, 0.5);
        r2.Value.DeltaX.ShouldBe(2.0, 0.5);
        r2.Value.DeltaY.ShouldBe(1.0, 0.5);
    }

    [Fact]
    public void GivenBrightEdgeStarAndDimmerInteriorStarWhenAcquireWithEdgeMarginThenPicksInterior()
    {
        const int w = 800, h = 600;
        // A bright star hugging the left edge + a dimmer star well inside the frame.
        ReadOnlySpan<ProjectedStar> stars =
        [
            new ProjectedStar(30, 300, Magnitude: 5.0),   // brightest, but near the left edge
            new ProjectedStar(400, 300, Magnitude: 6.0),  // dimmer, comfortably interior
        ];
        var frame = SyntheticStarFieldRenderer.Render(w, h, 0, stars, exposureSeconds: 2.0);

        // Legacy behaviour (no margin): lock the brightest star, which sits near the edge.
        var legacy = new GuiderCentroidTracker(maxStars: 1);
        var rLegacy = legacy.ProcessFrame(frame);
        rLegacy.ShouldNotBeNull();
        rLegacy.Value.X.ShouldBeLessThan(100, "without an edge margin the brightest (edge) star is chosen");

        // With an edge margin big enough to exclude the edge star: prefer the interior
        // one even though it is dimmer -- so the calibration throw won't push it off-frame.
        var robust = new GuiderCentroidTracker(maxStars: 1) { AcquisitionEdgeMargin = 80 };
        var rRobust = robust.ProcessFrame(frame);
        rRobust.ShouldNotBeNull();
        rRobust.Value.X.ShouldBeGreaterThan(300, "with an edge margin the interior star is preferred");
        output.WriteLine($"legacy primary X={rLegacy.Value.X:F1}, robust primary X={rRobust.Value.X:F1}");
    }

    [Fact]
    public void GivenOnlyEdgeStarsWhenAcquireWithEdgeMarginThenStillAcquires()
    {
        const int w = 800, h = 600;
        // Both stars are within the margin of an edge -> no interior candidate exists.
        ReadOnlySpan<ProjectedStar> stars =
        [
            new ProjectedStar(30, 300, Magnitude: 5.0),
            new ProjectedStar(w - 30, 300, Magnitude: 6.0),
        ];
        var frame = SyntheticStarFieldRenderer.Render(w, h, 0, stars, exposureSeconds: 2.0);

        var tracker = new GuiderCentroidTracker(maxStars: 1) { AcquisitionEdgeMargin = 80 };
        var result = tracker.ProcessFrame(frame);

        // Fall back to the brightest overall rather than failing to acquire.
        result.ShouldNotBeNull();
        tracker.IsAcquired.ShouldBeTrue();
        result.Value.X.ShouldBeLessThan(100, "falls back to the brightest star when none are interior");
    }

    [Fact]
    public void GivenMultiStarTrackingWhenOneStarLostThenOthersStillTracked()
    {
        // Use large field with many stars
        var frame0 = SyntheticStarFieldRenderer.Render(640, 480, 0,
            offsetX: 0, offsetY: 0, starCount: 30, seed: 42);

        var tracker = new GuiderCentroidTracker(maxStars: 4);
        var result0 = tracker.ProcessFrame(frame0);
        result0.ShouldNotBeNull();
        var initialCount = tracker.TrackedStarCount;
        initialCount.ShouldBeGreaterThan(1, "need multiple stars for this test");
        output.WriteLine($"Initially tracking {initialCount} stars");

        // Track with small offset, all stars should survive
        var frame1 = SyntheticStarFieldRenderer.Render(640, 480, 0,
            offsetX: 1.0, offsetY: 0.5, starCount: 30, seed: 42);

        var result1 = tracker.ProcessFrame(frame1);
        result1.ShouldNotBeNull();
        result1.Value.TrackedStarCount.ShouldBeGreaterThanOrEqualTo(1);
        output.WriteLine($"After offset: tracking {result1.Value.TrackedStarCount} stars, delta=({result1.Value.DeltaX:F3}, {result1.Value.DeltaY:F3})");
        result1.Value.DeltaX.ShouldBe(1.0, 0.5);
    }
    /// <summary>
    /// A star that moves further than one search radius between frames must stay LOCKED, and the
    /// move must be reported as the error it is.
    /// </summary>
    /// <remarks>
    /// The failure this pins is self-perpetuating rather than transient. Losing the lock is not a
    /// recoverable state: re-acquisition re-locks on wherever the star now IS, so the delta comes
    /// back zero, the guider issues no correction, and the drift that broke the lock carries on until
    /// it breaks the next one -- while a full-frame TryAcquire runs every frame, pegging a core.
    /// Found by sampling a session test that looked like a hang: four dumps of four sat in
    /// TryAcquire -> FindCandidateStars -> TryCentroid.
    /// </remarks>
    [Theory]
    [InlineData(10)]  // inside one search radius: this always worked
    [InlineData(22)]  // past it -- this is what used to drop the lock
    [InlineData(40)]  // and well past it
    public void AStarThatDriftsFurtherThanTheSearchRadiusIsStillTracked(int drift)
    {
        // Same field shape the other drift tests use: a roomy frame with several stars, so the lock
        // does not sit against an edge where TryCentroid refuses the aperture for reasons of its own.
        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var first = SyntheticStarFieldRenderer.Render(640, 480, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42);
        tracker.ProcessFrame(first).ShouldNotBeNull("premise: the tracker must lock on the first frame");
        tracker.IsAcquired.ShouldBeTrue();

        var moved = SyntheticStarFieldRenderer.Render(640, 480, 0,
            offsetX: drift, offsetY: 0, starCount: 5, seed: 42);
        var result = tracker.ProcessFrame(moved);

        tracker.IsAcquired.ShouldBeTrue("dropping the lock here is unrecoverable: the re-acquire reports no error");
        result.ShouldNotBeNull();
        result.Value.DeltaX.ShouldBe(drift, 3.0,
            "the drift must be reported as the error, or nothing ever corrects it");
    }

    /// <summary>
    /// The widened search stops short of a neighbour: recovering the WRONG star reports a confident
    /// delta that is pure fiction, which is worse than an honest loss.
    /// </summary>
    [Fact]
    public void AStarThatVanishesIsGivenUpRatherThanRecoveredOntoSomethingElse()
    {
        var tracker = new GuiderCentroidTracker(maxStars: 1);
        var withStar = SyntheticStarFieldRenderer.Render(640, 480, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42);
        tracker.ProcessFrame(withStar).ShouldNotBeNull("premise");

        var blank = new float[480, 640];
        for (var y = 0; y < 480; y++)
        {
            for (var x = 0; x < 640; x++)
            {
                blank[y, x] = 100f;
            }
        }

        tracker.ProcessFrame(blank);

        tracker.IsAcquired.ShouldBeFalse("with nothing there, the honest answer is that the star is gone");
    }
}
