using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
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

    private static OpenPHD2GuiderDriver CreateDriver() => new OpenPHD2GuiderDriver(
        new OpenPHD2GuiderDevice(new Uri("guider://OpenPHD2GuiderDevice/localhost/1#PHD2")),
        Substitute.For<IExternal>(),
        NullLogger.Instance,
        Substitute.For<ITimeProvider>());

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

    [Fact]
    public async Task AGuideStepWithoutSnrLeavesItNullAndDoesNotThrow()
    {
        var driver = CreateDriver();

        await Should.NotThrowAsync(async () => await FeedAsync(driver, GuideStepWithoutSnr));

        driver.GuideStarSNR.ShouldBeNull();
    }
}
