using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.OpenPHD2;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The PHD2 driver reads its guide star's SNR off the event stream. Event lines follow PHD2's own
/// reference (https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring) and are fed straight to the
/// event handler, so no PHD2 process is needed.
/// </summary>
[Collection("Guider")]
public class OpenPHD2GuideStarSnrTests
{
    private const string GuideStep =
        """{"Event":"GuideStep","Timestamp":1480000000.123,"Host":"obs","Inst":1,"Frame":42,"Time":84.5,"Mount":"EQMOD","dx":0.21,"dy":-0.17,"RADistanceRaw":0.25,"DECDistanceRaw":-0.12,"RADistanceGuide":0.2,"DECDistanceGuide":-0.1,"RADuration":110,"RADirection":"East","DECDuration":40,"DECDirection":"North","StarMass":18230.0,"SNR":37.4,"HFD":2.31,"AvgDist":0.34}""";

    private const string GuideStepWithoutSnr =
        """{"Event":"GuideStep","Timestamp":1480000000.123,"Host":"obs","Inst":1,"Frame":42,"Time":84.5,"Mount":"EQMOD","dx":0.21,"dy":-0.17,"RADistanceRaw":0.25,"DECDistanceRaw":-0.12,"RADistanceGuide":0.2,"DECDistanceGuide":-0.1,"RADuration":110,"RADirection":"East","DECDuration":40,"DECDirection":"North","AvgDist":0.34}""";

    private const string StarLost =
        """{"Event":"StarLost","Timestamp":1480000002.5,"Host":"obs","Inst":1,"Frame":43,"Time":86.5,"StarMass":210.0,"SNR":1.8,"AvgDist":0.34,"ErrorCode":1,"Status":"Star lost - low SNR"}""";

    private const string GuidingStopped =
        """{"Event":"GuidingStopped","Timestamp":1480000003.0,"Host":"obs","Inst":1}""";

    private static OpenPHD2GuiderDriver CreateDriver(ITimeProvider? timeProvider = null) => new OpenPHD2GuiderDriver(
        new OpenPHD2GuiderDevice(new Uri("guider://OpenPHD2GuiderDevice/localhost/1#PHD2")),
        Substitute.For<IExternal>(),
        NullLogger.Instance,
        timeProvider ?? Substitute.For<ITimeProvider>());

    /// <summary>This node's clock in the tests below: nowhere near the GuideSteps' own 2016 <c>Timestamp</c>.</summary>
    private static readonly DateTimeOffset NodeNow = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static async Task FeedAsync(OpenPHD2GuiderDriver driver, string line)
    {
        using var doc = JsonDocument.Parse(line);
        await driver.HandleEventAsync(doc, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GuideStepReportsTheGuideStarSnr()
    {
        var driver = CreateDriver();
        driver.GuideStarSNR.ShouldBeNull();

        await FeedAsync(driver, GuideStep);

        driver.GuideStarSNR.ShouldBe(37.4);
    }

    [Fact]
    public async Task StarLostClearsTheSnrEvenThoughItCarriesOne()
    {
        var driver = CreateDriver();
        await FeedAsync(driver, GuideStep);
        driver.GuideStarSNR.ShouldNotBeNull();

        await FeedAsync(driver, StarLost);

        driver.GuideStarSNR.ShouldBeNull();
    }

    [Fact]
    public async Task GuidingStoppedClearsTheSnr()
    {
        var driver = CreateDriver();
        await FeedAsync(driver, GuideStep);
        driver.GuideStarSNR.ShouldNotBeNull();

        await FeedAsync(driver, GuidingStopped);

        driver.GuideStarSNR.ShouldBeNull();
    }

    /// <summary>
    /// Each GuideStep is one correction, its pixel distances in arcseconds and the pulses signed West / North
    /// positive (#821): the session records it as one guide sample, instead of polling the stats once per
    /// imaging tick. It is stamped on THIS node's clock, at its arrival less half the guide exposure, the frame's
    /// middle. Never with the step's own <c>Timestamp</c>: that is the clock of the computer PHD2 runs on, or the
    /// real one under <c>TIANWEN_NOW</c>, and a session assigns samples to lights by its own clock.
    /// </summary>
    [Fact]
    public async Task AGuideStepIsOneCorrectionStampedOnThisNodesClock()
    {
        var driver = CreateDriver(new FakeTimeProviderWrapper(NodeNow));
        driver.ArcsecPerPixel = 2.0;
        driver.GuideExposure = TimeSpan.FromSeconds(2);
        var corrections = new System.Collections.Generic.List<GuideCorrectionEventArgs>();
        driver.GuideCorrectionEvent += (_, e) => corrections.Add(e);

        await FeedAsync(driver, GuideStep);

        var c = corrections.ShouldHaveSingleItem();
        c.FrameTime.ShouldBe(NodeNow - TimeSpan.FromSeconds(1), "the middle of the guide frame, on this node's clock");
        c.FrameTime.ShouldNotBe(DateTimeOffset.FromUnixTimeMilliseconds(1480000000123), "PHD2's Timestamp is another clock");
        c.RaError.ShouldBe(0.5);
        c.DecError.ShouldBe(-0.24);
        c.RaCorrectionMs.ShouldBe(-110); // East
        c.DecCorrectionMs.ShouldBe(40);  // North
    }

    /// <summary>With the guide exposure not yet read, a step is stamped at its arrival.</summary>
    [Fact]
    public async Task AGuideStepWithNoKnownExposureIsStampedAtItsArrival()
    {
        var driver = CreateDriver(new FakeTimeProviderWrapper(NodeNow));
        var corrections = new System.Collections.Generic.List<GuideCorrectionEventArgs>();
        driver.GuideCorrectionEvent += (_, e) => corrections.Add(e);

        await FeedAsync(driver, GuideStep);

        corrections.ShouldHaveSingleItem().FrameTime.ShouldBe(NodeNow);
    }

    /// <summary>
    /// A subscriber that throws must not take the event reader down with it: the step is still handled, as the
    /// in-process guide loop guards its own subscribers.
    /// </summary>
    [Fact]
    public async Task AThrowingCorrectionSubscriberDoesNotStopTheEventStream()
    {
        var driver = CreateDriver(new FakeTimeProviderWrapper(NodeNow));
        driver.GuideCorrectionEvent += (_, _) => throw new InvalidOperationException("subscriber bug");

        await FeedAsync(driver, GuideStep);

        driver.GuideStarSNR.ShouldBe(37.4);
    }

    /// <summary>
    /// PHD2 accumulates its stats from the GuideSteps' PIXEL distances, and every reader of them shows
    /// arcseconds (the guider tab, the Live Session line, the Home card), so they are converted where the scale
    /// is known, and left as they were where it is not.
    /// </summary>
    [Fact]
    public void ThePhd2StatsAreInArcsecondsWhereTheScaleIsKnown()
    {
        var converted = OpenPHD2GuiderDriver.Completed(
            new GuideStats { RaRMS = 0.3, DecRMS = 0.4, PeakRa = 1.0, PeakDec = 1.5 }, 0.25, -0.12, arcsecPerPixel: 2.0);

        converted.RaRMS.ShouldBe(0.6, 1e-12);
        converted.DecRMS.ShouldBe(0.8, 1e-12);
        converted.TotalRMS.ShouldBe(1.0, 1e-12);
        converted.PeakRa.ShouldBe(2.0, 1e-12);
        converted.PeakDec.ShouldBe(3.0, 1e-12);
        converted.LastRaErr.ShouldNotBeNull().ShouldBe(0.5, 1e-12);
        converted.LastDecErr.ShouldNotBeNull().ShouldBe(-0.24, 1e-12);

        var unscaled = OpenPHD2GuiderDriver.Completed(
            new GuideStats { RaRMS = 0.3, DecRMS = 0.4 }, 0.25, null, arcsecPerPixel: double.NaN);

        unscaled.RaRMS.ShouldBe(0.3, 1e-12);
        unscaled.TotalRMS.ShouldBe(0.5, 1e-12);
        unscaled.LastRaErr.ShouldNotBeNull().ShouldBe(0.25, 1e-12);
        unscaled.LastDecErr.ShouldBeNull();
    }

    /// <summary>
    /// Without a pixel scale a GuideStep's distance is in pixels, which a guide sample in arcseconds must
    /// not carry: the correction is reported unmeasured, and a session records nothing for it.
    /// </summary>
    [Fact]
    public async Task AGuideStepWithNoPixelScaleIsReportedUnmeasured()
    {
        var driver = CreateDriver();
        var corrections = new System.Collections.Generic.List<GuideCorrectionEventArgs>();
        driver.GuideCorrectionEvent += (_, e) => corrections.Add(e);

        await FeedAsync(driver, GuideStep);

        var c = corrections.ShouldHaveSingleItem();
        c.RaError.ShouldBeNull();
        c.DecError.ShouldBeNull();
    }

    [Fact]
    public async Task AGuideStepWithoutSnrLeavesItNullAndDoesNotThrow()
    {
        var driver = CreateDriver();

        await Should.NotThrowAsync(async () => await FeedAsync(driver, GuideStepWithoutSnr));

        driver.GuideStarSNR.ShouldBeNull();
    }
}
