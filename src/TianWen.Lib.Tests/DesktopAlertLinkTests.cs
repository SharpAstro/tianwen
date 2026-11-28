using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>#2507: a clicked alert's payload comes from outside the process, so it selects a rig only by an exact known id.</summary>
public class DesktopAlertLinkTests
{
    [Fact]
    public void ALinkRoundTripsToItsView()
    {
        var views = new ViewContexts();
        var rig = views.GetOrAddRemote("node-1", "Roof");
        views.GetOrAddRemote("node-2", "Dome");

        DesktopAlertLink.Resolve(DesktopAlertLink.For("node-1"), views).ShouldBeSameAs(rig);
        DesktopAlertLink.Resolve(DesktopAlertLink.For(null), views).ShouldBeSameAs(views.Local);
        DesktopAlertLink.For(null).ShouldBe("notifications");
        DesktopAlertLink.For("node-1").ShouldBe("notifications/node-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notifications/")]
    [InlineData("notifications/unknown")]
    [InlineData("notifications/NODE-1")]
    [InlineData("notifications/node-1/extra")]
    [InlineData("notifications/node-1 ")]
    [InlineData("notificationsnode-1")]
    [InlineData("../../node-1")]
    [InlineData("tab=planner")]
    [InlineData("%s%n{0}")]
    public void AnUnknownOrMalformedPayloadResolvesToThisComputer(string? payload)
    {
        var views = new ViewContexts();
        views.GetOrAddRemote("node-1", "Roof");

        DesktopAlertLink.Resolve(payload, views).ShouldBeSameAs(views.Local);
    }
}
