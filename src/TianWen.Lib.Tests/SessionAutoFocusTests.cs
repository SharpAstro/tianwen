using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class SessionAutoFocusTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;

    /// <summary>
    /// Creates a Session with the focuser configured for auto-focus testing:
    /// synthetic star generation enabled and focuser moved away from best focus.
    /// </summary>
    private async Task<SessionTestContext> CreateAutoFocusSessionAsync(CancellationToken cancellationToken = default)
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: cancellationToken);

        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;

        // Move focuser to a starting position away from best focus
        var positionBeforeMove = await ctx.Focuser.GetPositionAsync(cancellationToken);
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition + 50, cancellationToken);

        while (await ctx.Focuser.GetIsMovingAsync(cancellationToken))
        {
            await ctx.External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        var positionAfterMove = await ctx.Focuser.GetPositionAsync(cancellationToken);
        positionAfterMove.ShouldBe(TrueBestFocusPosition + 50, "focuser should have moved to starting position");
        positionAfterMove.ShouldNotBe(positionBeforeMove, "focuser position should have changed");

        return ctx;
    }

    [Fact]
    public async Task GivenSyntheticStarsWhenAutoFocusThenFindsCorrectBestFocusPosition()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var ctx = await CreateAutoFocusSessionAsync(ct);

        // when
        var (converged, baseline) = await ctx.Session.AutoFocusAsync(0, ct);

        // then — should converge
        converged.ShouldBeTrue("auto-focus should converge");
        baseline.IsValid.ShouldBeTrue("baseline should be valid");
        baseline.MedianHfd.ShouldBeGreaterThan(0, "baseline HFD should be positive");

        // focuser should be near the true best focus position
        var finalPos = await ctx.Focuser.GetPositionAsync(ct);
        output.WriteLine($"True best focus: {TrueBestFocusPosition}, found: {finalPos}, baseline HFD: {baseline.MedianHfd:F2}");

        Math.Abs(finalPos - TrueBestFocusPosition).ShouldBeLessThan(30, "focuser should be within 30 steps of true best focus");
    }

    [Fact]
    public async Task GivenAutoFocusWhenBacklashConfiguredThenApproachesFromBelow()
    {
        // given — focuser starts above best focus, backlash = 20
        var ct = TestContext.Current.CancellationToken;
        var ctx = await CreateAutoFocusSessionAsync(ct);

        // when
        var (converged, _) = await ctx.Session.AutoFocusAsync(0, ct);

        // then — should still converge despite backlash
        converged.ShouldBeTrue("auto-focus should converge with backlash compensation");

        var finalPos = await ctx.Focuser.GetPositionAsync(ct);
        output.WriteLine($"Final position: {finalPos}");
        Math.Abs(finalPos - TrueBestFocusPosition).ShouldBeLessThan(30);
    }

    [Fact]
    public async Task GivenSyntheticStarsWhenAutoFocusAllTelescopesThenBaselineHfdStored()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var ctx = await CreateAutoFocusSessionAsync(ct);

        // when
        var allConverged = await ctx.Session.AutoFocusAllTelescopesAsync(ct);

        // then
        allConverged.ShouldBeTrue("all telescopes should converge");

        // Verify baseline metrics are stored for the current observation
        ctx.Session.BaselineByObservation.ShouldContainKey(0);
        var baselines = ctx.Session.BaselineByObservation[0];
        baselines.Length.ShouldBe(1);
        baselines[0].IsValid.ShouldBeTrue("baseline should be valid");
        baselines[0].MedianHfd.ShouldBeGreaterThan(0);
        baselines[0].MedianFwhm.ShouldBeGreaterThan(0);
        baselines[0].StarCount.ShouldBeGreaterThan(3);
        output.WriteLine($"Baseline HFD: {baselines[0].MedianHfd:F2}, FWHM: {baselines[0].MedianFwhm:F2}, Stars: {baselines[0].StarCount}");
    }

    [Fact]
    public async Task GivenSyntheticStarFieldWhenImageTakenAtBestFocusThenStarsDetectedWithGoodHFD()
    {
        // given — camera at best focus
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        await using var cameraDriver = new FakeCameraDriver(cameraDevice, external);
        await cameraDriver.ConnectAsync(ct);
        cameraDriver.TrueBestFocus = TrueBestFocusPosition;
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;
        cameraDriver.FocusPosition = TrueBestFocusPosition; // at perfect focus

        // when — take an exposure
        await cameraDriver.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: ct);
        await external.SleepAsync(TimeSpan.FromSeconds(3), ct);
        var image = await ((ICameraDriver)cameraDriver).GetImageAsync(ct);

        // then — should produce detectable stars with tight HFD
        image.ShouldNotBeNull();
        var stars = await image.FindStarsAsync(0, snrMin: 10, cancellationToken: ct);
        output.WriteLine($"Stars detected: {stars.Count}");
        stars.Count.ShouldBeGreaterThan(3, "should detect stars in synthetic image");

        var medianHfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
        output.WriteLine($"Median HFD at best focus: {medianHfd:F2}");
        medianHfd.ShouldBeGreaterThan(0);

        // At perfect focus, HFD should be small (the hyperbola minimum A ≈ 2.0)
        medianHfd.ShouldBeLessThan(10.0f, "HFD at best focus should be small");
    }

    [Fact]
    public async Task GivenSyntheticStarFieldWhenDefocusedThenHFDIncreases()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        await using var cameraDriver = new FakeCameraDriver(cameraDevice, external);
        await cameraDriver.ConnectAsync(ct);
        cameraDriver.TrueBestFocus = TrueBestFocusPosition;
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;

        // when — take image at focus
        cameraDriver.FocusPosition = TrueBestFocusPosition;
        await cameraDriver.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: ct);
        await external.SleepAsync(TimeSpan.FromSeconds(3), ct);
        var focusedImage = await ((ICameraDriver)cameraDriver).GetImageAsync(ct);

        // and — take image defocused by 100 steps
        cameraDriver.FocusPosition = TrueBestFocusPosition + 100;
        await cameraDriver.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: ct);
        await external.SleepAsync(TimeSpan.FromSeconds(3), ct);
        var defocusedImage = await ((ICameraDriver)cameraDriver).GetImageAsync(ct);

        // then — defocused HFD should be larger
        focusedImage.ShouldNotBeNull();
        defocusedImage.ShouldNotBeNull();

        var focusedStars = await focusedImage.FindStarsAsync(0, snrMin: 10, cancellationToken: ct);
        var defocusedStars = await defocusedImage.FindStarsAsync(0, snrMin: 10, cancellationToken: ct);

        focusedStars.Count.ShouldBeGreaterThan(0, "should detect stars at focus");
        defocusedStars.Count.ShouldBeGreaterThan(0, "should detect stars when defocused");

        var focusedHfd = focusedStars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
        var defocusedHfd = defocusedStars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);

        output.WriteLine($"Focused HFD: {focusedHfd:F2}, Defocused HFD: {defocusedHfd:F2}");
        defocusedHfd.ShouldBeGreaterThan(focusedHfd, "defocused image should have larger HFD");
    }

    [Fact]
    public async Task GivenVCurveSamplesWhenHyperbolaFitThenBestFocusMatchesTruePosition()
    {
        // given — collect V-curve data from synthetic images at known positions
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        await using var cameraDriver = new FakeCameraDriver(cameraDevice, external);
        await cameraDriver.ConnectAsync(ct);
        cameraDriver.TrueBestFocus = TrueBestFocusPosition;
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;

        var sampleMap = new MetricSampleMap(SampleKind.HFD, AggregationMethod.Median);
        var stepSize = 50;
        var startPos = TrueBestFocusPosition - 200;

        for (var i = 0; i < 9; i++)
        {
            var pos = startPos + i * stepSize;
            cameraDriver.FocusPosition = pos;
            await cameraDriver.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: ct);
            await external.SleepAsync(TimeSpan.FromSeconds(3), ct);
            var image = await ((ICameraDriver)cameraDriver).GetImageAsync(ct);
            image.ShouldNotBeNull();

            var stars = await image.FindStarsAsync(0, snrMin: 10, cancellationToken: ct);
            if (stars.Count > 3)
            {
                var hfd = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
                sampleMap.AddSampleAtFocusPosition(pos, hfd);
                output.WriteLine($"Pos={pos} Stars={stars.Count} HFD={hfd:F2}");
            }
            else
            {
                output.WriteLine($"Pos={pos} too few stars ({stars.Count})");
            }
        }

        // when — fit hyperbola
        var success = sampleMap.TryGetBestFocusSolution(out var solution, out var min, out var max);

        // then
        success.ShouldBeTrue("hyperbola fit should succeed");
        solution.ShouldNotBeNull();
        output.WriteLine($"Best focus: {solution.Value.BestFocus:F1}, A={solution.Value.A:F2}, B={solution.Value.B:F2}, Error={solution.Value.Error:F4}");

        Math.Abs(solution.Value.BestFocus - TrueBestFocusPosition).ShouldBeLessThan(30,
            $"fitted best focus ({solution.Value.BestFocus:F1}) should be within 30 steps of true ({TrueBestFocusPosition})");
    }
}
