using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A light's guide cards come from a per-exposure tally, not the telemetry ring of 300 samples (#2100).
/// </summary>
[Collection("Session")]
public class SessionExposureGuideCardsTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2025, 6, 15, 22, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(900);

    [Fact(Timeout = 60_000)]
    public async Task AnExposureWithMoreCorrectionsThanTheRingHoldsIsTalliedWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        ct.ThrowIfCancellationRequested();
        await using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, SessionTestHelper.DefaultConfiguration, SessionTestHelper.DefaultScheduledObservations, cancellationToken: ct);

        const int count = 450;
        count.ShouldBeGreaterThan(ISessionTelemetry.GuideSampleCapacity);

        ctx.Session.OpenExposureGuideWindow(0, Start, Exposure);
        double sumSqRa = 0, sumSqDec = 0;
        for (var n = 0; n < count; n++)
        {
            var ra = 0.1 + (n % 7) * 0.05;
            var dec = 0.2 + (n % 5) * 0.03;
            sumSqRa += ra * ra;
            sumSqDec += dec * dec;
            ctx.Session.RecordGuideSample(new GuideErrorSample(Start + TimeSpan.FromSeconds(1 + n * 2), ra, dec));
        }

        ctx.Session.GuideSamples.Length.ShouldBe(ISessionTelemetry.GuideSampleCapacity);

        var stats = ctx.Session.ExposureGuideStats(0);
        stats.ShouldNotBeNull();
        stats.Value.SampleCount.ShouldBe(count);
        var rmsRa = Math.Sqrt(sumSqRa / count);
        var rmsDec = Math.Sqrt(sumSqDec / count);
        stats.Value.RmsRa.ShouldBe((float)rmsRa, 1e-4f);
        stats.Value.RmsDec.ShouldBe((float)rmsDec, 1e-4f);
        stats.Value.RmsTotal.ShouldBe((float)Math.Sqrt(rmsRa * rmsRa + rmsDec * rmsDec), 1e-4f);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASampleOutsideTheWindowIsNotCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        ct.ThrowIfCancellationRequested();
        await using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, SessionTestHelper.DefaultConfiguration, SessionTestHelper.DefaultScheduledObservations, cancellationToken: ct);

        ctx.Session.OpenExposureGuideWindow(0, Start, Exposure);
        ctx.Session.RecordGuideSample(new GuideErrorSample(Start - TimeSpan.FromSeconds(5), 9, 9));
        ctx.Session.RecordGuideSample(new GuideErrorSample(Start + TimeSpan.FromSeconds(10), 0.3, 0.4));
        ctx.Session.RecordGuideSample(new GuideErrorSample(Start + Exposure + TimeSpan.FromSeconds(5), 9, 9));

        var stats = ctx.Session.ExposureGuideStats(0);
        stats.ShouldNotBeNull();
        stats.Value.SampleCount.ShouldBe(1);
        stats.Value.RmsTotal.ShouldBe(0.5f, 1e-4f);
    }

    [Fact(Timeout = 60_000)]
    public async Task OpeningAWindowSeedsFromTheRingAndCountsALaterDeliveryOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        ct.ThrowIfCancellationRequested();
        await using var ctx = await SessionTestHelper.CreateSessionAsync(
            output, SessionTestHelper.DefaultConfiguration, SessionTestHelper.DefaultScheduledObservations, cancellationToken: ct);

        // Two corrections landed before the window opened (one outside it); the ring holds both.
        ctx.Session.RecordGuideSample(new GuideErrorSample(Start - TimeSpan.FromSeconds(5), 9, 9));
        var inside = new GuideErrorSample(Start + TimeSpan.FromSeconds(1), 0.3, 0.4);
        ctx.Session.RecordGuideSample(inside);

        ctx.Session.OpenExposureGuideWindow(0, Start, Exposure);

        ctx.Session.ExposureGuideStats(0).ShouldNotBeNull().SampleCount.ShouldBe(1);

        // The same correction delivered again, and a new one: counted once, and once.
        ctx.Session.RecordGuideSample(inside);
        ctx.Session.RecordGuideSample(new GuideErrorSample(Start + TimeSpan.FromSeconds(3), 0.3, 0.4));

        var stats = ctx.Session.ExposureGuideStats(0).ShouldNotBeNull();
        stats.SampleCount.ShouldBe(2);
        stats.RmsTotal.ShouldBe(0.5f, 1e-4f);
    }
}
