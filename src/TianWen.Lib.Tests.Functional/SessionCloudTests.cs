using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The session grades each light as it lands (<c>LiveCloudJudge</c>): a light judged cloud triggers no refocus, feeds no
/// focus history, lets no plate solve run and is saved as <c>MAYBE_CLOUD_</c>; a clear light resumes all of it; cloud that
/// lasts past <see cref="SessionConfiguration.CloudWaitTimeout"/> takes the condition-deterioration path (#1977).
/// </summary>
[Collection("Session")]
public class SessionCloudTests(ITestOutputHelper output)
{
    private const int TrueBestFocusPosition = 1000;
    private static readonly TimeSpan SubExposure = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A site where 22:00 UTC on 2025-06-15 is astronomical dark, with the helper's longitude (so M13 still transits then): at the
    /// helper's 48.2 N the Sun is at about -17 degrees, a dusk twilight light the expected sky never reads (the Sun is its own cause).
    /// </summary>
    private const double DarkSiteLatitude = 30.0;

    private async Task<SessionTestContext> CreateAsync(SessionConfiguration config, TimeSpan duration, System.Threading.CancellationToken ct,
        double latitude = 48.2)
    {
        var observations = new[]
        {
            new ScheduledObservation(
                new Target(16.695, 36.46, "M13", null),
                new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero),
                duration,
                AcrossMeridian: false,
                FilterPlan: FilterPlanBuilder.BuildSingleFilterPlan(SubExposure),
                Gain: 0,
                Offset: 0)
        };

        var ctx = await SessionTestHelper.CreateSessionAsync(output, config, observations, latitude: latitude, cancellationToken: ct);
        ctx.Camera.TrueBestFocus = TrueBestFocusPosition;
        ctx.Camera.FocusPosition = TrueBestFocusPosition;
        await ctx.Focuser.BeginMoveAsync(TrueBestFocusPosition, ct);
        while (await ctx.Focuser.GetIsMovingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }
        ctx.Session.AdvanceObservationForTest();

        var target = ctx.Session.ActiveObservation!.Target;
        IMountDriver mount = ctx.Mount;
        await mount.BeginSlewRaDecAsync(target.RA, target.Dec, ct);
        while (await mount.IsSlewingAsync(ct))
        {
            await ctx.TimeProvider.SleepAsync(TimeSpan.FromMilliseconds(100), ct);
        }
        // The fake's clear sky holds a fixed patchiness floor by default (FakeCameraDriver.SkyMottle), as a real one does.
        ctx.Camera.SkyMottle.ShouldBe(FakeCameraDriver.DefaultSkyMottle);
        ctx.External.MaxFitsWrites = 200;
        return ctx;
    }

    /// <summary>
    /// The harness builds a session and runs its imaging loop without <c>InitialisationAsync</c>, which is where a real run gives each
    /// camera the run's site (<c>ConnectTelescopeAsync</c>), so a frame made here records no site and the expected sky, which is keyed
    /// on one, could never be read. This does what the init does, from the mount's own site.
    /// </summary>
    private static async ValueTask StampSiteAsync(SessionTestContext ctx, System.Threading.CancellationToken ct)
    {
        IMountDriver mount = ctx.Mount;
        ctx.Camera.Latitude ??= await mount.GetSiteLatitudeAsync(ct);
        ctx.Camera.Longitude ??= await mount.GetSiteLongitudeAsync(ct);
    }

    private static SessionConfiguration Config(TimeSpan? cloudWait = null) => SessionTestHelper.DefaultConfiguration with
    {
        ConditionDeteriorationThreshold = 0.6f,
        ConditionRecoveryTimeout = TimeSpan.FromMinutes(3),
        BaselineHfdFrameCount = 2,
        DitherEveryNthFrame = 0,
        CloudWaitTimeout = cloudWait,
    };

    private async Task<ImageLoopNextAction> RunAsync(SessionTestContext ctx, Func<int, ValueTask> onIteration, System.Threading.CancellationToken ct)
    {
        IMountDriver mount = ctx.Mount;
        await mount.EnsureTrackingAsync(cancellationToken: ct);

        var guider = (FakeGuider)ctx.Session.Setup.Guider.Driver;
        await guider.GuideAsync(0.3, 3, 30, ct);
        await ctx.TimeProvider.SleepAsync(TimeSpan.FromSeconds(4), ct);

        var observation = ctx.Session.ActiveObservation.ShouldNotBeNull();
        var hourAngle = await mount.GetHourAngleAsync(ct);

        ctx.TimeProvider.ExternalTimePump = true;
        var imagingTask = ctx.Track(Task.Run(async () => await ctx.Session.ImagingLoopAsync(observation, hourAngle, cancellationToken: ctx.Token), ctx.Token));
        await ctx.TimeProvider.PumpUntilCompletedAsync(imagingTask, TimeSpan.FromHours(4),
            onIteration: onIteration,
            progress: () => ctx.Session.ImagingLoopTicks, cancellationToken: ct);
        imagingTask.IsCompleted.ShouldBeTrue("imaging loop should have completed within timeout");
        return await imagingTask;
    }

    private static string[] FramesOf(SessionTestContext ctx)
        => Directory.GetFiles(ctx.External.ImageOutputFolder.FullName, "*frame_*.fits", SearchOption.AllDirectories);

    [Fact(Timeout = 180_000)]
    public async Task ACloudyLightAfterClearOnesIsSavedAsMaybeCloudAndKeptOutOfFocusAndTheNextClearOneResumes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await CreateAsync(Config(), TimeSpan.FromMinutes(25), ct);
        foreach (var stale in FramesOf(ctx))
        {
            File.Delete(stale);
        }

        var injected = false;
        var cleared = false;
        var historyAtInjection = 0;
        var historyAtCloud = -1;
        var framesAtClear = 0;

        await RunAsync(ctx, iteration =>
        {
            var written = ctx.Session.TotalFramesWritten;
            if (!injected && written >= 8)
            {
                historyAtInjection = ctx.Session.FrameMetricsHistoryCount(0);
                ctx.Camera.CloudCoverage = 0.5;
                injected = true;
            }
            else if (injected && !cleared && ctx.Session.CloudFrameCount >= 2)
            {
                historyAtCloud = ctx.Session.FrameMetricsHistoryCount(0);
                ctx.Camera.CloudCoverage = 0;
                framesAtClear = written;
                cleared = true;
            }
            return ValueTask.CompletedTask;
        }, ct);

        injected.ShouldBeTrue();
        cleared.ShouldBeTrue("the judge must have found cloud, with the sky cloudy from frame 8");
        ctx.Session.CloudFrameCount.ShouldBeGreaterThanOrEqualTo(2);

        // No refocus, and the cloud lights are not in the drift history: the history held at most the frames that were
        // taken before cloud plus the one that landed before the judge saw it. This loop runs no plate solve, cloud or not, so
        // the solve half (the flip's recentre, which the cloud gates) is tested where a flip happens:
        // MeridianFlipVerificationSessionTests.AFlipUnderCloudRunsNoPlateSolveAndOnceClearTheSolvesResume.
        ctx.Session.DriftRefocusCount.ShouldBe(0);
        ctx.Session.ConditionDeteriorationCount.ShouldBe(0, "cloud that clears inside CloudWaitTimeout is no deterioration");
        output.WriteLine($"history at injection {historyAtInjection}, when the second cloud light landed {historyAtCloud}");
        historyAtCloud.ShouldBeLessThanOrEqualTo(historyAtInjection + 1,
            "at most the one frame the judge could not yet call cloud may join the history; cloud lights add none");

        // Files: the cloud lights are MAYBE_CLOUD_, the rest plain, and a clear light after the cloud is plain.
        var files = FramesOf(ctx);
        var maybe = files.Where(f => Path.GetFileName(f).StartsWith("MAYBE_CLOUD_frame_", StringComparison.Ordinal)).ToArray();
        maybe.Length.ShouldBe(ctx.Session.CloudFrameCount);
        files.Length.ShouldBe(ctx.Session.TotalFramesWritten);
        var plainAfter = files.Except(maybe)
            .Count(f => string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(maybe.Max()!).Replace("MAYBE_CLOUD_", "", StringComparison.Ordinal)) > 0);
        output.WriteLine($"cloud {maybe.Length} of {files.Length}; plain files after the last cloud one: {plainAfter}; cleared at frame {framesAtClear}");
        plainAfter.ShouldBeGreaterThan(0, "the clear lights after the cloud are written plainly");

        // Both resume: the history grows again once the sky is clear.
        ctx.Session.FrameMetricsHistoryCount(0).ShouldBeGreaterThan(historyAtCloud);
    }

    [Fact(Timeout = 180_000)]
    public async Task CloudPastTheTimeoutReachesTheDeteriorationPath()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await CreateAsync(Config(cloudWait: TimeSpan.FromMinutes(2)), TimeSpan.FromMinutes(30), ct);

        var injected = false;
        var cleared = false;

        await RunAsync(ctx, iteration =>
        {
            if (!injected && ctx.Session.TotalFramesWritten >= 8)
            {
                ctx.Camera.CloudCoverage = 0.5;
                injected = true;
            }
            else if (injected && !cleared && ctx.Session.ConditionDeteriorationCount > 0)
            {
                // Let the recovery wait succeed.
                ctx.Camera.CloudCoverage = 0;
                cleared = true;
            }
            return ValueTask.CompletedTask;
        }, ct);

        injected.ShouldBeTrue();
        ctx.Session.CloudFrameCount.ShouldBeGreaterThan(0);
        ctx.Session.ConditionDeteriorationCount.ShouldBeGreaterThan(0, "cloud past CloudWaitTimeout routes to the deterioration path");
        cleared.ShouldBeTrue();
        ctx.Session.ConditionRecoveryCount.ShouldBeGreaterThan(0);
    }

    [Fact(Timeout = 180_000)]
    public async Task WithDetectionOffCloudLightsAreWrittenPlainly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await CreateAsync(Config() with { DetectCloudWhileImaging = false }, TimeSpan.FromMinutes(12), ct);

        var injected = false;
        await RunAsync(ctx, iteration =>
        {
            if (!injected && ctx.Session.TotalFramesWritten >= 6)
            {
                ctx.Camera.CloudCoverage = 0.5;
                injected = true;
            }
            else if (injected && ctx.Session.ConditionDeteriorationCount > 0)
            {
                ctx.Camera.CloudCoverage = 0;
            }
            return ValueTask.CompletedTask;
        }, ct);

        ctx.Session.CloudFrameCount.ShouldBe(0);
        FramesOf(ctx).Count(f => Path.GetFileName(f).StartsWith("MAYBE_CLOUD_", StringComparison.Ordinal)).ShouldBe(0);
    }

    /// <summary>
    /// #1974, the wiring a unit test of <c>LiveCloudJudge</c> cannot see: <c>Session</c> loads the rig's stored expected sky from
    /// <c>ExpectedSkyStore</c> at the start of an observation and hands it to each OTA's judge, so a light the viewer would call a veil
    /// against that sky is saved as <c>MAYBE_CLOUD_</c>. The control is the same clear fake sky with nothing stored (judged clear, by
    /// the unchanged patchiness terms), which also writes the lights the stored model is keyed from: read back from their FITS headers
    /// as <c>ExpectedSkyFitter</c> keys them, so a key the fitter writes but the live judge never matches fails here, not at the telescope.
    /// The stored model expects a sky an e-fold darker than the fake's, which is the veil, with no patchiness in it.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARunReadsTheRigsStoredExpectedSkyAndSavesAVeiledLightAsMaybeCloud()
    {
        var ct = TestContext.Current.CancellationToken;
        ExpectedSkyKey key;
        float level, gradient;

        await using (var control = await CreateAsync(Config(), TimeSpan.FromMinutes(10), ct, DarkSiteLatitude))
        {
            await StampSiteAsync(control, ct);
            foreach (var stale in FramesOf(control))
            {
                File.Delete(stale);
            }
            await RunAsync(control, _ => ValueTask.CompletedTask, ct);

            control.Session.CloudFrameCount.ShouldBe(0, "a clear sky with nothing stored is judged clear");
            var written = FramesOf(control);
            written.ShouldNotBeEmpty();
            Array.Sort(written, StringComparer.Ordinal);
            Image.TryReadFitsHeader(written[0], out var info).ShouldBeTrue();
            var meta = info.Meta;
            key = new ExpectedSkyKey(LightGrading.RoundSite(meta.Latitude), LightGrading.RoundSite(meta.Longitude), LightGrading.RigOf(meta),
                meta.SensorType, meta.Filter.Name is null ? string.Empty : meta.Filter.IdentityKey,
                (long)Math.Round(meta.ExposureDuration.TotalMilliseconds), meta.BinX);
            Image.TryReadFitsFile(written[0], out var image, out _).ShouldBeTrue();
            image.ShouldNotBeNull();
            try
            {
                var surface = image.SkySurface(image.ReferenceStarChannel);
                (level, gradient) = (surface.Level, surface.GradientStrength);
            }
            finally
            {
                image.Release();
            }
            output.WriteLine($"stored key: {key.Describe()}; the fake's sky level {level:0.#}, gradient {gradient:0.###}");
            level.ShouldBeGreaterThan(0f, "the fake's frames carry a sky the model can be set below");
            gradient.ShouldBeGreaterThan(0f, "the fake's frames carry a gradient the model can be set below");
        }

        await using var veiled = await CreateAsync(Config(cloudWait: TimeSpan.FromHours(2)), TimeSpan.FromMinutes(10), ct, DarkSiteLatitude);
        await StampSiteAsync(veiled, ct);
        var model = new ExpectedSkyModel
        {
            Terms = ["1"],
            LevelCoefficients = [Math.Log(level) - 1.0],
            GradientCoefficients = [Math.Log(gradient) - 1.0],
            LevelScatter = 0.05,
            GradientScatter = 0.2,
            FrameCount = 100,
            OfferedCount = 100,
        };
        (await ExpectedSkyStore.SaveAsync(veiled.External, [new ExpectedSkyEntry(key, model, 2)], ct)).Length.ShouldBe(1);
        foreach (var stale in FramesOf(veiled))
        {
            File.Delete(stale);
        }

        await RunAsync(veiled, _ => ValueTask.CompletedTask, ct);

        var files = FramesOf(veiled);
        var maybe = files.Where(f => Path.GetFileName(f).StartsWith("MAYBE_CLOUD_frame_", StringComparison.Ordinal)).ToArray();
        output.WriteLine($"{maybe.Length} of {files.Length} lights saved as MAYBE_CLOUD_, {veiled.Session.CloudFrameCount} judged cloud");
        veiled.Session.CloudFrameCount.ShouldBeGreaterThanOrEqualTo(files.Length / 2,
            "against the stored sky the clear fake's lights are a veil, and the judge read that sky from the store");
        maybe.Length.ShouldBe(veiled.Session.CloudFrameCount);
    }
}
