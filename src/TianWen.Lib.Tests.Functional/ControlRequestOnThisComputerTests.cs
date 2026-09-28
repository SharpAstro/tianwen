using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A request for control of this computer's rig is put to whoever is at it (P6b of docs/plans/hardware-in-the-server.md,
/// #1021): the node pushes that its access changed, the handler's per-frame poll reads it, and the question is drawn over
/// this computer's Live Session view, brought to the front; an answer there is the node's, and a question put away stays
/// away while the same request waits. The GUI's own handler over this computer's node.
/// </summary>
[Collection("NodeProcesses")]
public class ControlRequestOnThisComputerTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task ARequestIsPutToWhoeverIsAtThisComputerAndTheirAllowIsTheNodes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        h.AppState.ActiveTab = GuiTab.Planner;
        var access = h.Node.App.Services.GetRequiredService<NodeAccess>();
        var view = h.Contexts.Local.LiveSession;

        var ticket = access.Request("Laptop, TianWen", IPAddress.Parse("192.168.1.20")).ShouldNotBeNull();
        await h.UntilAsync(() =>
        {
            h.Handler.PollPreviewTelemetry();
            return view.ControlRequest is not null;
        }, ct);

        view.ControlRequest.ShouldNotBeNull().Label.ShouldBe("Laptop, TianWen");
        h.AppState.ActiveTab.ShouldBe(GuiTab.LiveSession, "the question is brought to the front");
        h.Contexts.Active.ShouldBeSameAs(h.Contexts.Local);

        h.Post(new AnswerControlRequestSignal(view.ControlRequest.Id, Allow: true));
        await h.UntilSettledAsync(ct);

        view.ControlRequest.ShouldBeNull();
        var outcome = await access.PollAsync(ticket.Id, ticket.Secret, ct);
        outcome.State.ShouldBe(ControlRequestState.Granted);
    }

    [Fact(Timeout = 60_000)]
    public async Task AQuestionPutAwayStaysAwayWhileTheSameRequestWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var access = h.Node.App.Services.GetRequiredService<NodeAccess>();
        var view = h.Contexts.Local.LiveSession;
        access.Request("Laptop, TianWen", IPAddress.Parse("192.168.1.20")).ShouldNotBeNull();
        await h.UntilAsync(() =>
        {
            h.Handler.PollPreviewTelemetry();
            return view.ControlRequest is not null;
        }, ct);

        h.Post(new DismissControlRequestSignal());
        await h.Local.RefreshAccessNowAsync(ct);
        h.Handler.PollPreviewTelemetry();

        view.ControlRequest.ShouldBeNull("put away, it waits in the Sharing panel");
        h.Local.Access.ShouldNotBeNull().Pending.ShouldNotBeNull("the request still waits, for the Sharing panel to answer");
    }
}
