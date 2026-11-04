using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
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

    private async Task<SessionTestContext> CreateAsync(SessionConfiguration config, TimeSpan duration, System.Threading.CancellationToken ct)
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

        var ctx = await SessionTestHelper.CreateSessionAsync(output, config, observations, cancellationToken: ct);
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
}
