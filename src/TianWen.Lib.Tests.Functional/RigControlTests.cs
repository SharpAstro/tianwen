using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A rig's view gets control by asking (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021): a connection over
/// TCP sees until the rig's machine allows it, then commands and may manage who else may, keeps its grant for the next start,
/// and sees again once the grant is revoked. Against a real node over loopback TCP, which it treats as the LAN; the node's
/// harness client is the rig's owner here.
/// </summary>
[Collection("Hosting")]
public class RigControlTests(ITestOutputHelper outputHelper)
{
    [Fact(Timeout = 90_000)]
    public async Task ARigsViewAsksIsAllowedKeepsItsGrantAndSeesAgainOnceRevoked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var owner = new TianWenNodeClient(node.Client);
        var hub = node.App.Services.GetRequiredService<EventHub>();
        var grants = new NodeGrants(new FileCredentialStore(new FakeExternal(outputHelper)), NullLogger.Instance);
        var binding = await BindingOfAsync(node, owner);

        await using (var rig = Connect(binding, grants, ct))
        {
            await rig.MaybeRefreshAccessAsync(ct);
            rig.MayCommand.ShouldBeFalse("a rig's view over the LAN sees until its owner allows it");
            rig.Access.ShouldBeNull("who may command the rig is for its owner");

            var asking = rig.AskForControlAsync("Laptop, TianWen", ct);
            var pending = await NodeWait.UntilAsync("the request to reach the rig", async token =>
            {
                var waiting = (await owner.GetAccessAsync(token)).Value?.Pending;
                return (waiting, waiting?.Label ?? "none waiting");
            }, ct);
            pending.Label.ShouldBe("Laptop, TianWen");
            rig.Ask.State.ShouldBe(ControlAskState.Asking);

            (await owner.AnswerControlRequestAsync(pending.Id, allow: true, ct)).IsSuccess.ShouldBeTrue();
            await asking;

            rig.Ask.State.ShouldBe(ControlAskState.None);
            rig.MayCommand.ShouldBeTrue();
            rig.Access.ShouldNotBeNull("a client granted control may manage who else may").Grants.ShouldContain(held => held.Label == "Laptop, TianWen");
            grants.TokenOf(binding.NodeId).ShouldNotBeNull("a grant is kept for the next start");
            await NodeWait.UntilAsync("its event socket, reopened with the grant, to count as one that may command", _ =>
                ValueTask.FromResult((hub.CommandingClientCount == 1, $"{hub.CommandingClientCount} commanding")), ct);
        }

        await using var again = Connect(binding, grants, ct);
        await again.MaybeRefreshAccessAsync(ct);
        again.MayCommand.ShouldBeTrue("a grant is remembered until revoked, across restarts of the client");

        var held = (await owner.GetAccessAsync(ct)).Value.ShouldNotBeNull().Grants.Single(grant => grant.Label == "Laptop, TianWen");
        (await owner.RevokeGrantAsync(held.Id, ct)).IsSuccess.ShouldBeTrue();
        await NodeWait.UntilAsync("the revoke to reach the rig's view, pushed", async token =>
        {
            await again.MaybeRefreshAccessAsync(token);
            return (!again.MayCommand, again.MayCommand ? "still commands" : "sees");
        }, ct);
        again.Transport.Grant.IsHeld.ShouldBeFalse("a grant the rig no longer holds is presented no more");
        grants.TokenOf(binding.NodeId).ShouldBeNull("and is not kept for the next start");
        again.Access.ShouldBeNull();
    }

    [Fact(Timeout = 90_000)]
    public async Task ADeclinedAskLeavesTheViewSeeing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var owner = new TianWenNodeClient(node.Client);
        var grants = new NodeGrants(new FileCredentialStore(new FakeExternal(outputHelper)), NullLogger.Instance);
        var binding = await BindingOfAsync(node, owner);
        await using var rig = Connect(binding, grants, ct);

        var asking = rig.AskForControlAsync("Laptop, TianWen", ct);
        var pending = await NodeWait.UntilAsync("the request to reach the rig", async token =>
        {
            var waiting = (await owner.GetAccessAsync(token)).Value?.Pending;
            return (waiting, waiting?.Label ?? "none waiting");
        }, ct);
        (await owner.AnswerControlRequestAsync(pending.Id, allow: false, ct)).IsSuccess.ShouldBeTrue();
        await asking;

        rig.Ask.State.ShouldBe(ControlAskState.Declined);
        rig.MayCommand.ShouldBeFalse();
        grants.TokenOf(binding.NodeId).ShouldBeNull();
    }

    private static async Task<RemoteRigBinding> BindingOfAsync(NodeHarness node, TianWenNodeClient owner) => new RemoteRigBinding
    {
        BindingId = Guid.NewGuid(),
        NodeId = (await owner.GetNodeAsync(TestContext.Current.CancellationToken)).Value.ShouldNotBeNull().NodeId,
        Alias = "Rig",
        LastAddress = node.Transport.BaseAddress.ToString(),
    };

    private static RemoteRigConnection Connect(RemoteRigBinding binding, NodeGrants grants, System.Threading.CancellationToken ct) =>
        RemoteRigConnection.TryConnect(binding, new ViewContexts(), peers: null, grants, new SystemTimeProvider(), NullLogger.Instance, ct)
            .ShouldNotBeNull();
}
