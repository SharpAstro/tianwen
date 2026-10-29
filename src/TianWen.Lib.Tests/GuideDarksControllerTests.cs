using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The Guider tab's darks controller (#1915): the exposures a library covers and the count the panel steps.</summary>
public class GuideDarksControllerTests
{
    [Fact]
    public void ALibraryCoversTheLoopsExposureAndTheLaddersStepEitherSide()
    {
        var exposures = GuideDarksController.ExposuresFor(1.0);

        exposures.ShouldContain(1.0);
        exposures.Length.ShouldBe(3);
        exposures[0].ShouldBeLessThan(1.0);
        exposures[2].ShouldBeGreaterThan(1.0);
        exposures.ShouldBe([.. exposures.Order()]);
    }

    [Fact]
    public void TheCountStepsAlongItsPresetsAndStopsAtTheEnds()
    {
        var darks = new GuideDarksController(new SystemTimeProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        darks.Count.ShouldBe(10);
        darks.StepCount(+1);
        darks.Count.ShouldBe(20);
        darks.StepCount(+1);
        darks.StepCount(+1);
        darks.Count.ShouldBe(30);
        for (var i = 0; i < 5; i++)
        {
            darks.StepCount(-1);
        }
        darks.Count.ShouldBe(5);
        darks.Status.ShouldBe("", "nothing taken, nothing to say");
        darks.IsRunning.ShouldBeFalse();
    }
}
