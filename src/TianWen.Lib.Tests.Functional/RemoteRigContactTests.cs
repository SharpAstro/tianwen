using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A rig whose node goes away says so, on its Home card and on its own view, in one wording (P5b part 6b of
/// docs/plans/hardware-in-the-server.md, #935): the GUI's own connection to a real node over TCP, and the node then gone.
/// Before this the rig's tabs went on showing the last thing the node said as though it were live.
/// </summary>
[Collection("Hosting")]
public class RemoteRigContactTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task ARigWhoseNodeGoesAwaySaysSoOnItsCardAndItsView()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await NodeHarness.StartAsync(output, ct);
        var nodeUp = true;
        try
        {
            var binding = new RemoteRigBinding
            {
                BindingId = Guid.NewGuid(),
                NodeId = "remote-rig-contact-test",
                Alias = "Contact test rig",
                LastAddress = node.Transport.BaseAddress.ToString(),
            };
            var contexts = new ViewContexts();
            var app = new GuiAppState();
            var rigs = new RemoteRigRegistry();
            rigs.Upsert(binding);
            var clock = new SystemTimeProvider();
            await using var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, clock, NullLogger.Instance, ct)
                .ShouldNotBeNull();
            rigs.Attach(rig);
            contexts.Activate(rig.Context).ShouldBeTrue();

            await NodeWait.UntilAsync("the rig's node to answer", _ => ValueTask.FromResult((
                rig.Mirror.Contact.State is NodeContactState.Answering, rig.Mirror.Contact.State.ToString())), ct);
            HomeBoard.BuildCards(contexts, rigs, app, clock.GetUtcNow())[1].IsOnline.ShouldBeTrue();

            await node.DisposeAsync();
            nodeUp = false;

            await NodeWait.UntilAsync("the rig to notice its node is gone", _ => ValueTask.FromResult((
                rig.Mirror.Contact.State is NodeContactState.NotAnswering, rig.Mirror.Contact.State.ToString())), ct);

            var card = HomeBoard.BuildCards(contexts, rigs, app, clock.GetUtcNow())[1];
            card.IsOnline.ShouldBeFalse();
            card.Status.ShouldStartWith("Not answering (last seen ");

            // The view the Live Session and Guider tabs draw, as the render loop polls it.
            contexts.PollAll();
            RemoteRigActions.DescribeContact(rig.Context.LiveSession.Contact, binding: null, clock.GetUtcNow())
                .ShouldNotBeNull().ShouldStartWith("Not answering (last seen ");
        }
        finally
        {
            if (nodeUp)
            {
                await node.DisposeAsync();
            }
        }
    }
}
