using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class FakeMountDriverTests(ITestOutputHelper output)
{
    private (FakeMountDriver mount, FakeExternal external) CreateMount(
        double latitude = 48.2,
        double longitude = 16.3,
        DateTimeOffset? now = null)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(device, external);
        return (mount, external);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenSetPositionThenCoordinatesMatch()
    {
        var (mount, _) = CreateMount();
        var ct = TestContext.Current.CancellationToken;
        await mount.ConnectAsync(ct);

        await mount.SetPositionAsync(16.695, 36.46, ct); // M13

        var ra = await mount.GetRightAscensionAsync(ct);
        var dec = await mount.GetDeclinationAsync(ct);

        ra.ShouldBe(16.695, 0.001);
        dec.ShouldBe(36.46, 0.001);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenTrackingMountWhenTimeAdvancesThenRaTracksCorrectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetPositionAsync(16.695, 36.46, ct);
        await mount.SetTrackingAsync(true, ct);

        var ra0 = await mount.GetRightAscensionAsync(ct);

        // Advance 1 hour
        await external.SleepAsync(TimeSpan.FromHours(1), ct);

        var ra1 = await mount.GetRightAscensionAsync(ct);

        // RA should have advanced by ~1 sidereal hour (to track the object)
        // Sidereal rate: 24h / 86164s * 3600s ≈ 1.0027 hours per hour
        var delta = ra1 - ra0;
        if (delta < -12) delta += 24;
        if (delta > 12) delta -= 24;
        output.WriteLine($"RA delta after 1h: {delta:F6} hours");
        delta.ShouldBe(1.0027, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMountWhenPulseGuideEastThenRaDecreases()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var raBefore = await mount.GetRightAscensionAsync(ct);

        // Guide east for 1 second
        await mount.PulseGuideAsync(GuideDirection.East, TimeSpan.FromSeconds(1), ct);

        var raAfter = await mount.GetRightAscensionAsync(ct);

        // East guide should decrease RA (move scope east = sky moves west)
        var delta = raAfter - raBefore;
        if (delta > 12) delta -= 24;
        if (delta < -12) delta += 24;
        output.WriteLine($"RA delta after East pulse: {delta * 3600 * 15:F2} arcsec");
        delta.ShouldBeLessThan(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMountWhenPulseGuideNorthThenDecIncreases()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var decBefore = await mount.GetDeclinationAsync(ct);

        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromSeconds(1), ct);

        var decAfter = await mount.GetDeclinationAsync(ct);

        output.WriteLine($"Dec delta after North pulse: {(decAfter - decBefore) * 3600:F2} arcsec");
        decAfter.ShouldBeGreaterThan(decBefore);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPeriodicErrorWhenTrackingThenRaDrifts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.PeriodicErrorAmplitudeArcsec = 15.0; // 15" peak PE
        mount.PeriodicErrorPeriodSeconds = 480.0;  // 8 min worm period
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        // Sample PE over one full period
        var maxError = 0.0;
        var minError = 0.0;
        var samples = 48; // sample every 10s over 480s
        var sampleInterval = TimeSpan.FromSeconds(10);

        for (var i = 0; i < samples; i++)
        {
            await external.SleepAsync(sampleInterval, ct);
            var error = await mount.GetTrackingErrorRaArcsecAsync(ct);
            if (error > maxError) maxError = error;
            if (error < minError) minError = error;
        }

        output.WriteLine($"PE range: {minError:F2} to {maxError:F2} arcsec");

        // Peak-to-peak should be close to 2 * amplitude = 30"
        var peakToPeak = maxError - minError;
        peakToPeak.ShouldBeGreaterThan(20.0, "PE peak-to-peak should approach 2*amplitude");
        peakToPeak.ShouldBeLessThanOrEqualTo(30.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPolarDriftWhenTrackingThenDecDrifts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.PolarDriftRateDecArcsecPerSec = 0.5; // 0.5"/s drift north
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        var dec0 = await mount.GetDeclinationAsync(ct);

        // Advance 60 seconds → expect ~30" = 0.00833° drift
        await external.SleepAsync(TimeSpan.FromSeconds(60), ct);

        var dec1 = await mount.GetDeclinationAsync(ct);
        var driftArcsec = (dec1 - dec0) * 3600;
        output.WriteLine($"Dec drift after 60s: {driftArcsec:F2} arcsec");

        driftArcsec.ShouldBe(30.0, 2.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenDecBacklashWhenDirectionReversedThenDeadZoneApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.DecBacklashArcsec = 5.0; // 5" backlash
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var dec0 = await mount.GetDeclinationAsync(ct);

        // Guide north first (establishes direction)
        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromSeconds(1), ct);
        var dec1 = await mount.GetDeclinationAsync(ct);
        var northMove = (dec1 - dec0) * 3600;
        output.WriteLine($"North move: {northMove:F2} arcsec");

        // Now guide south (reversal) — first 5" should be consumed by backlash
        // Guide rate ~10.028 arcsec/sec, so 1 second pulse = ~10" total
        await mount.PulseGuideAsync(GuideDirection.South, TimeSpan.FromSeconds(1), ct);
        var dec2 = await mount.GetDeclinationAsync(ct);
        var southMove = (dec1 - dec2) * 3600; // should be positive (moved south)
        output.WriteLine($"South move after reversal: {southMove:F2} arcsec (expected ~5 with backlash)");

        // Without backlash it would be ~10", with 5" backlash it should be ~5"
        southMove.ShouldBe(5.0, 1.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenDecBacklashWhenSameDirectionThenNoDeadZone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.DecBacklashArcsec = 5.0;
        await mount.SetPositionAsync(12.0, 45.0, ct);

        // Two north pulses in same direction — no backlash
        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromSeconds(1), ct);
        var dec1 = await mount.GetDeclinationAsync(ct);

        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromSeconds(1), ct);
        var dec2 = await mount.GetDeclinationAsync(ct);

        var firstMove = (dec1 - 45.0) * 3600;
        var secondMove = (dec2 - dec1) * 3600;
        output.WriteLine($"First north move: {firstMove:F2}, Second: {secondMove:F2} arcsec");

        // Both should be equal (no backlash penalty on same direction)
        secondMove.ShouldBe(firstMove, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMountWhenSlewThenReachesTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetPositionAsync(12.0, 45.0, ct);

        // Slew to different position
        await mount.BeginSlewRaDecAsync(14.0, 50.0, ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();

        // Advance time to allow slew to complete
        await external.SleepAsync(TimeSpan.FromSeconds(30), ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();

        var ra = await mount.GetRightAscensionAsync(ct);
        var dec = await mount.GetDeclinationAsync(ct);
        ra.ShouldBe(14.0, 0.001);
        dec.ShouldBe(50.0, 0.001);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenPerfectMountWhenTrackingThenNoError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        // All error injection defaults to 0
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        await external.SleepAsync(TimeSpan.FromMinutes(5), ct);

        (await mount.GetTrackingErrorRaArcsecAsync(ct)).ShouldBe(0.0, 0.01);
        (await mount.GetTrackingErrorDecArcsecAsync(ct)).ShouldBe(0.0, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMountWhenIsPulseGuidingThenReturnsCorrectState()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        // Before pulse: not guiding
        (await mount.IsPulseGuidingAsync(ct)).ShouldBeFalse();

        // Start a pulse guide
        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromSeconds(2), ct);

        // During pulse: guiding
        (await mount.IsPulseGuidingAsync(ct)).ShouldBeTrue();

        // Advance past pulse duration
        await external.SleepAsync(TimeSpan.FromSeconds(3), ct);

        // After pulse: not guiding
        (await mount.IsPulseGuidingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSyntheticRendererWhenOffsetAppliedThenStarsShift()
    {
        // Render same field with and without offset
        var noOffset = SyntheticStarFieldRenderer.Render(320, 240, 0, offsetX: 0, offsetY: 0,
            starCount: 10, seed: 42);
        var withOffset = SyntheticStarFieldRenderer.Render(320, 240, 0, offsetX: 5.0, offsetY: 3.0,
            starCount: 10, seed: 42);

        // Find brightest pixel in each
        var (maxX0, maxY0) = FindBrightestPixel(noOffset);
        var (maxX1, maxY1) = FindBrightestPixel(withOffset);

        output.WriteLine($"No offset brightest: ({maxX0}, {maxY0})");
        output.WriteLine($"With offset brightest: ({maxX1}, {maxY1})");

        // Brightest pixel should shift by approximately the offset
        ((double)(maxX1 - maxX0)).ShouldBe(5.0, 1.5);
        ((double)(maxY1 - maxY0)).ShouldBe(3.0, 1.5);
    }

    [Fact(Timeout = 60_000)]
    public void GivenSyntheticRendererWhenHotPixelsInjectedThenMaxADUPresent()
    {
        var data = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 5, seed: 42, hotPixelCount: 3, maxADU: 4096);

        // Count pixels at maxADU
        var hotCount = 0;
        for (var y = 0; y < 240; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                if (data[y, x] >= 4096f) hotCount++;
            }
        }

        output.WriteLine($"Hot pixels found: {hotCount}");
        hotCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact(Timeout = 60_000)]
    public void GivenSyntheticRendererWhenSeeingAppliedThenPsfWidens()
    {
        // No seeing
        var noSeeing = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 1, seed: 42, seeingArcsec: 0);

        // With 3" seeing at 1.5"/px = 2px FWHM seeing added in quadrature
        var withSeeing = SyntheticStarFieldRenderer.Render(320, 240, 0,
            offsetX: 0, offsetY: 0, starCount: 1, seed: 42, seeingArcsec: 3.0, pixelScaleArcsec: 1.5);

        // The star with seeing should have a wider profile (lower peak, more spread)
        var (_, _, peakNoSeeing) = FindBrightestPixelAndValue(noSeeing);
        var (_, _, peakWithSeeing) = FindBrightestPixelAndValue(withSeeing);

        output.WriteLine($"Peak without seeing: {peakNoSeeing:F1}");
        output.WriteLine($"Peak with seeing: {peakWithSeeing:F1}");

        // Wider PSF = flux spread over more pixels = lower peak
        peakWithSeeing.ShouldBeLessThan(peakNoSeeing);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMountWhenGetAxisPositionThenReturnsEncoderTicks()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetPositionAsync(6.0, 45.0, ct); // RA=6h, Dec=+45°

        var raTicks = await mount.GetAxisPositionAsync(TelescopeAxis.Primary, ct);
        var decTicks = await mount.GetAxisPositionAsync(TelescopeAxis.Seconary, ct);
        var tertiaryTicks = await mount.GetAxisPositionAsync(TelescopeAxis.Tertiary, ct);

        raTicks.ShouldNotBeNull();
        decTicks.ShouldNotBeNull();
        tertiaryTicks.ShouldBeNull("tertiary axis not supported");

        // RA=6h → 6/24 of full revolution
        raTicks.Value.ShouldBeGreaterThan(0);
        output.WriteLine($"RA encoder: {raTicks}, Dec encoder: {decTicks}");
    }

    private static (int x, int y) FindBrightestPixel(float[,] data)
    {
        var (x, y, _) = FindBrightestPixelAndValue(data);
        return (x, y);
    }

    private static (int x, int y, float value) FindBrightestPixelAndValue(float[,] data)
    {
        var maxVal = float.MinValue;
        var maxX = 0;
        var maxY = 0;
        for (var y = 0; y < data.GetLength(0); y++)
        {
            for (var x = 0; x < data.GetLength(1); x++)
            {
                if (data[y, x] > maxVal)
                {
                    maxVal = data[y, x];
                    maxX = x;
                    maxY = y;
                }
            }
        }
        return (maxX, maxY, maxVal);
    }

    // --- Wind gust tests ---

    [Fact(Timeout = 60_000)]
    public async Task GivenWindGustsWhenTrackingThenPositionFluctuates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.WindGustAmplitudeArcsec = 3.0;
        mount.WindGustDecayTimeSeconds = 5.0;
        mount.WindGustSeed = 42;
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        var raErrors = new double[20];
        var decErrors = new double[20];
        for (var i = 0; i < 20; i++)
        {
            await external.SleepAsync(TimeSpan.FromSeconds(2), ct);
            raErrors[i] = await mount.GetTrackingErrorRaArcsecAsync(ct);
            decErrors[i] = await mount.GetTrackingErrorDecArcsecAsync(ct);
        }

        // Compute variance — should be non-zero (wind is active)
        var raVariance = Variance(raErrors);
        var decVariance = Variance(decErrors);
        output.WriteLine($"RA wind variance: {raVariance:F4}, Dec wind variance: {decVariance:F4}");

        raVariance.ShouldBeGreaterThan(0.01, "wind should cause RA fluctuations");
        decVariance.ShouldBeGreaterThan(0.01, "wind should cause Dec fluctuations");

        // Amplitude should be bounded (OU process is mean-reverting)
        foreach (var err in raErrors)
        {
            Math.Abs(err).ShouldBeLessThan(30.0, "RA wind error should be bounded");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenWindGustsWithSameSeedThenDeterministic()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero);
        var external1 = new FakeExternal(output, now: now);
        var mount1 = new FakeMountDriver(new FakeDevice(DeviceType.Mount, 1), external1);
        await mount1.ConnectAsync(ct);
        mount1.WindGustAmplitudeArcsec = 3.0;
        mount1.WindGustSeed = 77;
        await mount1.SetPositionAsync(12.0, 45.0, ct);
        await mount1.SetTrackingAsync(true, ct);

        var external2 = new FakeExternal(output, now: now);
        var mount2 = new FakeMountDriver(new FakeDevice(DeviceType.Mount, 2), external2);
        await mount2.ConnectAsync(ct);
        mount2.WindGustAmplitudeArcsec = 3.0;
        mount2.WindGustSeed = 77;
        await mount2.SetPositionAsync(12.0, 45.0, ct);
        await mount2.SetTrackingAsync(true, ct);

        for (var i = 0; i < 10; i++)
        {
            await external1.SleepAsync(TimeSpan.FromSeconds(2), ct);
            await external2.SleepAsync(TimeSpan.FromSeconds(2), ct);

            var ra1 = await mount1.GetTrackingErrorRaArcsecAsync(ct);
            var ra2 = await mount2.GetTrackingErrorRaArcsecAsync(ct);
            ra1.ShouldBe(ra2, 1e-10, $"RA error should be identical at step {i}");

            var dec1 = await mount1.GetTrackingErrorDecArcsecAsync(ct);
            var dec2 = await mount2.GetTrackingErrorDecArcsecAsync(ct);
            dec1.ShouldBe(dec2, 1e-10, $"Dec error should be identical at step {i}");
        }
    }

    // --- Cable snag tests ---

    [Fact(Timeout = 60_000)]
    public async Task GivenCableSnagWhenTimeReachedThenStepApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.CableSnagTimeSeconds = 30.0;
        mount.CableSnagAmplitudeRaArcsec = 10.0;
        mount.CableSnagAmplitudeDecArcsec = -5.0;
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        // Before snag time
        await external.SleepAsync(TimeSpan.FromSeconds(20), ct);
        var raBefore = await mount.GetRightAscensionAsync(ct);
        var decBefore = await mount.GetDeclinationAsync(ct);

        // After snag time
        await external.SleepAsync(TimeSpan.FromSeconds(15), ct); // total = 35s, past 30s snag
        var raAfter = await mount.GetRightAscensionAsync(ct);
        var decAfter = await mount.GetDeclinationAsync(ct);

        // Verify step was applied (RA increased by ~10", Dec decreased by ~5")
        // Account for sidereal tracking in RA
        var decDeltaArcsec = (decAfter - decBefore) * 3600.0;
        output.WriteLine($"Dec delta across snag: {decDeltaArcsec:F2} arcsec");
        decDeltaArcsec.ShouldBe(-5.0 / 3600.0 * 3600.0, 1.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCableSnagWhenTimeNotReachedThenNoEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.CableSnagTimeSeconds = 60.0;
        mount.CableSnagAmplitudeRaArcsec = 20.0;
        mount.CableSnagAmplitudeDecArcsec = 20.0;
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        // Only advance 30s — snag at 60s should not fire
        await external.SleepAsync(TimeSpan.FromSeconds(30), ct);

        (await mount.GetTrackingErrorRaArcsecAsync(ct)).ShouldBe(0.0, 0.01);
        (await mount.GetTrackingErrorDecArcsecAsync(ct)).ShouldBe(0.0, 0.01);
    }

    // --- Flexure drift tests ---

    [Fact(Timeout = 60_000)]
    public async Task GivenFlexureDriftWhenTrackingThenDecDriftsWithHA()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.FlexureDriftRateDecArcsecPerHaHour = 2.0; // 2"/HA-hour
        await mount.SetPositionAsync(12.0, 45.0, ct);
        await mount.SetTrackingAsync(true, ct);

        // Advance 1 hour (3600 seconds)
        await external.SleepAsync(TimeSpan.FromHours(1), ct);

        var flexureError = await mount.GetTrackingErrorDecArcsecAsync(ct);
        output.WriteLine($"Flexure Dec error after 1h: {flexureError:F4} arcsec");

        // Sidereal rate = 24h/86164s, so in 3600s: haHours = 3600 * 24/86164 ≈ 1.0027
        // Expected flexure = 2.0 * 1.0027 ≈ 2.005"
        flexureError.ShouldBe(2.005, 0.1);
    }

    private static double Variance(double[] values)
    {
        var mean = 0.0;
        foreach (var v in values) mean += v;
        mean /= values.Length;
        var sumSq = 0.0;
        foreach (var v in values) sumSq += (v - mean) * (v - mean);
        return sumSq / values.Length;
    }
}
