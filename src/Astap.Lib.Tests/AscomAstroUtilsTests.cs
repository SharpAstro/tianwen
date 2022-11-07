using Astap.Lib.Astrometry.Ascom;
using Astap.Lib.Astrometry.NOVA;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomAstroUtilsTests : IDisposable
{
    private readonly IAstroUtils _astroUtils;
    private readonly SemaphoreSlim _semaphore;

    public AscomAstroUtilsTests()
    {
        _astroUtils = new AstroUtils();
        _semaphore = new(1, 1);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _astroUtils.Dispose();
    }

    [SkippableTheory]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", EventType.SunRiseSunset, -37.884546970458274d, 145.1663117892053d, false, 1, 1, "2022-11-06T06:07:20+11:00", "2022-11-06T19:59:06+11:00")]
    [InlineData("2012-01-13T00:00:00-05:00", EventType.MoonRiseMoonSet, 75d, -75d, true, 1, 1, "2012-01-13T22:36:21-05:00", "2012-01-13T09:38:29-05:00")]
    [InlineData("2012-01-14T00:00:00-05:00", EventType.MoonRiseMoonSet, 75d, -75d, true, 0, 1, "2012-01-14T09:06:07-05:00")]
    [InlineData("2022-11-06T00:00:00+11:00", EventType.AstronomicalTwilight, -37.8845, 145.1663, false, 1, 1, "2022-11-06T04:27:15+11:00", "2022-11-06T21:39:40+11:00")]
    public async Task GivenDateEventPositionAndOffsetWhenCalcRiseAndSetTimesTheyAreReturned(string dtoStr, EventType eventType, double lat, double @long, bool expAbove, int expRise, int expSet, params string[] dateTimeStrs)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        var dto = DateTimeOffset.Parse(dtoStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        bool actualAboveHorizon;
        IReadOnlyList<DateTimeOffset> actualRiseEvents;
        IReadOnlyList<DateTimeOffset> actualSetEvents;
        await _semaphore.WaitAsync();
        try
        {
            (actualAboveHorizon, actualRiseEvents, actualSetEvents) = _astroUtils.EventTimes(eventType, dto, lat, @long).ShouldNotBeNull();
        }
        finally
        {
            _semaphore.Release();
        }

        actualAboveHorizon.ShouldBe(expAbove);
        actualRiseEvents.Count.ShouldBe(expRise);
        actualSetEvents.Count.ShouldBe(expSet);

        int idx = 0;

        for (var i = 0; i < expRise; i++)
        {
            var expDto = DateTimeOffset.Parse(dateTimeStrs[idx++], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            actualRiseEvents[i].ShouldBeInRange(expDto - TimeSpan.FromMinutes(1), expDto + TimeSpan.FromMinutes(1));
        }

        for (var i = 0; i < expSet; i++)
        {
            var expDto = DateTimeOffset.Parse(dateTimeStrs[idx++], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            actualSetEvents[i].ShouldBeInRange(expDto - TimeSpan.FromMinutes(1), expDto + TimeSpan.FromMinutes(1));
        }
    }
}
