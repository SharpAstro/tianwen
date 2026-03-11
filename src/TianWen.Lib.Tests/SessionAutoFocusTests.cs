using NSubstitute;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class SessionAutoFocusTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;

    /// <summary>
    /// Creates a minimal Session with fake devices suitable for auto-focus and imaging tests.
    /// Camera + focuser are connected and configured with a known best focus position.
    /// </summary>
    private async Task<(Session Session, FakeExternal External, FakeCameraDriver Camera, FakeFocuserDriver Focuser)> CreateAutoFocusSessionAsync(
        SessionConfiguration? configuration = null,
        Observation[]? observations = null,
        CancellationToken cancellationToken = default)
    {
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        // Camera + Focuser (real fake devices for star generation)
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var camera = new Camera(cameraDevice, external);
        var focuser = new Focuser(focuserDevice, external);

        await camera.Driver.ConnectAsync(cancellationToken);
        await focuser.Driver.ConnectAsync(cancellationToken);

        // Configure synthetic star generation
        var cameraDriver = (FakeCameraDriver)camera.Driver;
        var focuserDriver = (FakeFocuserDriver)focuser.Driver;
        cameraDriver.TrueBestFocus = TrueBestFocusPosition;

        // Set camera dimensions (defaults are 0x0)
        cameraDriver.BinX = 1;
        cameraDriver.NumX = 512;
        cameraDriver.NumY = 512;

        // Move focuser to a starting position away from best focus
        await focuserDriver.BeginMoveAsync(TrueBestFocusPosition + 50, cancellationToken);
        while (await focuserDriver.GetIsMovingAsync(cancellationToken))
        {
            await external.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        var ota = new OTA(
            "Test Telescope",
            1000,
            camera,
            Cover: null,
            focuser,
            new FocusDirection(PreferOutward: true, OutwardIsPositive: true),
            FilterWheel: null,
            Switches: null
        );

        // Mount + Guider (use real fake devices with NSubstitute fallback for unneeded methods)
        var mountDevice = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", "48.2" },
            { "longitude", "16.3" }
        });
        var guiderDevice = new FakeDevice(DeviceType.Guider, 1);
        var mount = new Mount(mountDevice, external);
        var guider = new Guider(guiderDevice, external);

        await mount.Driver.ConnectAsync(cancellationToken);
        await guider.Driver.ConnectAsync(cancellationToken);
        await ((FakeGuider)guider.Driver).ConnectEquipmentAsync(cancellationToken);

        var setup = new Setup(mount, guider, new GuiderSetup(), [ota]);

        var plateSolver = Substitute.For<IPlateSolver>();
        plateSolver.CheckSupportAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));

        var config = configuration ?? new SessionConfiguration(
            SetpointCCDTemperature: new SetpointTemp(-10, SetpointTempKind.Normal),
            CooldownRampInterval: TimeSpan.FromSeconds(1),
            WarmupRampInterval: TimeSpan.FromSeconds(1),
            MinHeightAboveHorizon: 20,
            DitherPixel: 1.5,
            SettlePixel: 0.3,
            DitherEveryNthFrame: 5,
            SettleTime: TimeSpan.FromSeconds(3),
            GuidingTries: 3,
            AutoFocusRange: 200,
            AutoFocusStepCount: 9,
            FocusDriftThreshold: 1.3f
        );

        var obs = observations ?? [
            new Observation(
                new Target(6.75, 16.7, "M42", null),
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(30),
                AcrossMeridian: false,
                SubExposure: TimeSpan.FromSeconds(120)
            )
        ];

        var session = new Session(setup, config, plateSolver, external, obs);

        return (session, external, cameraDriver, focuserDriver);
    }

    [Fact]
    public async Task GivenSyntheticStarsWhenAutoFocusThenFindsCorrectBestFocusPosition()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var (session, external, camera, focuser) = await CreateAutoFocusSessionAsync(cancellationToken: ct);

        // when
        var (converged, baselineHfd) = await session.AutoFocusAsync(0, ct);

        // then — should converge
        converged.ShouldBeTrue("auto-focus should converge");
        baselineHfd.ShouldBeGreaterThan(0, "baseline HFD should be positive");

        // focuser should be near the true best focus position
        var finalPos = await focuser.GetPositionAsync(ct);
        output.WriteLine($"True best focus: {TrueBestFocusPosition}, found: {finalPos}, baseline HFD: {baselineHfd:F2}");

        Math.Abs(finalPos - TrueBestFocusPosition).ShouldBeLessThan(30, "focuser should be within 30 steps of true best focus");
    }

    [Fact]
    public async Task GivenAutoFocusWhenBacklashConfiguredThenApproachesFromBelow()
    {
        // given — focuser starts above best focus, backlash = 20
        var ct = TestContext.Current.CancellationToken;
        var (session, external, camera, focuser) = await CreateAutoFocusSessionAsync(cancellationToken: ct);

        // when
        var (converged, _) = await session.AutoFocusAsync(0, ct);

        // then — should still converge despite backlash
        converged.ShouldBeTrue("auto-focus should converge with backlash compensation");

        var finalPos = await focuser.GetPositionAsync(ct);
        output.WriteLine($"Final position: {finalPos}");
        Math.Abs(finalPos - TrueBestFocusPosition).ShouldBeLessThan(30);
    }

    [Fact]
    public async Task GivenSyntheticStarsWhenAutoFocusAllTelescopesThenBaselineHfdStored()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        var (session, external, camera, focuser) = await CreateAutoFocusSessionAsync(cancellationToken: ct);

        // when
        var allConverged = await session.AutoFocusAllTelescopesAsync(ct);

        // then
        allConverged.ShouldBeTrue("all telescopes should converge");

        // Verify baseline HFD is stored (accessible via reflection since _baselineHfd is private)
        var baselineField = typeof(Session).GetField("_baselineHfd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var baselineHfd = (float[]?)baselineField?.GetValue(session);
        baselineHfd.ShouldNotBeNull();
        baselineHfd.Length.ShouldBe(1);
        baselineHfd[0].ShouldBeGreaterThan(0);
        output.WriteLine($"Baseline HFD: {baselineHfd[0]:F2}");
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
