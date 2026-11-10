using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// What <see cref="StallWatch"/> says and when, over times the test chooses: a wake that is only busy is silent, and one
/// that is a second late or more is said with the CPU time and GC pause the process spent over the gap, which tell a
/// process the operating system withheld from one that was busy itself (#2023).
/// </summary>
public class StallWatchTests
{
    [Fact]
    public void AWakeThatIsOnlyBusyIsNotSaid()
    {
        var said = new List<string>();
        using var watch = new StallWatch(said.Add, probePool: false);

        watch.Observe(StallWatch.Tick, TimeSpan.Zero, TimeSpan.Zero);
        watch.Observe(StallWatch.Tick * 2 + TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(50), TimeSpan.Zero);

        said.ShouldBeEmpty("a wake 0.9 s late is an ordinary busy runner");
    }

    [Fact]
    public void AWakeASecondLateIsSaidWithWhatTheProcessSpentMeanwhile()
    {
        var said = new List<string>();
        using var watch = new StallWatch(said.Add, probePool: false);
        var first = StallWatch.Tick;
        watch.Observe(first, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));

        watch.Observe(first + TimeSpan.FromSeconds(14.1), TimeSpan.FromSeconds(2.2), TimeSpan.FromMilliseconds(10));

        var line = said.ShouldHaveSingleItem();
        line.ShouldContain("did not run for 14.1 s");
        line.ShouldContain("cpu +0.2 s", customMessage: "the CPU time over the gap, not since the start");
        line.ShouldContain("gc pause +0 ms", customMessage: "the pause over the gap, not since the start");
    }
}
