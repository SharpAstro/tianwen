using System;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What the node says of its runs (<see cref="NodeRuns"/>): a run that ended on a fault says so in the node's notifications,
/// which every client's panel shows, and a run that ended on a stop or by itself says nothing there. A Canon whose battery died
/// mid live view said so only in the watching window's status row and the node's log (#1111).
/// </summary>
public class NodeRunsTests
{
    [Fact]
    public void A_run_that_faulted_says_so_in_the_notifications_and_one_that_stopped_says_nothing()
    {
        var node = Substitute.For<IHostedSession>();
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 3, 9, 56, 31, TimeSpan.Zero));

        NodeRuns.NoteFault(node, clock, "The live view", failure: null);
        NodeRuns.NoteFault(node, clock, "The live view", failure: "");
        node.DidNotReceiveWithAnyArgs().AddNotification(default!);

        NodeRuns.NoteFault(node, clock, "The live view", "Canon EDS error InternalError (0x00000002): Failed to start Canon Live View");

        node.Received(1).AddNotification(Arg.Is<NotificationDto>(n =>
            n.Severity == "Warning"
            && n.Message == "The live view stopped: Canon EDS error InternalError (0x00000002): Failed to start Canon Live View"
            && n.TimestampUtc == clock.GetUtcNow()));
    }
}
