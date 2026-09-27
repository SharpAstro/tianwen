using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A connected rig's own profile reaches its view (P5b part 8 of docs/plans/hardware-in-the-server.md, #935): the rig's
/// periodic profile refresh reads the profile whole from a real node, the one the rig runs or the one its binding names,
/// and puts it on the rig's view, where the planner plans the rig's nights at its site and the sky map draws its sensor.
/// </summary>
[Collection("Hosting")]
public class RemoteRigProfileTests(ITestOutputHelper output)
{
    private static Profile At(Guid id, string name, double latitude, double longitude) => new Profile(id, name, new ProfileData(
        new Uri("Mount://NoneDevice/none"), new Uri("Guider://NoneDevice/none"), [], SiteLatitude: latitude, SiteLongitude: longitude));

    [Fact(Timeout = 60_000)]
    public async Task ARigsViewGetsTheProfileItRunsAndTheOneItsBindingNamesOverIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(output, ct);
        var running = At(Guid.NewGuid(), "Sydney rig", -33.87, 151.21);
        var named = At(Guid.NewGuid(), "Siding Spring", -31.27, 149.06);
        await running.SaveAsync(node.External, ct);
        await named.SaveAsync(node.External, ct);
        (await new TianWenNodeClient(node.Client).SetActiveProfileAsync(running.ProfileId, ct)).IsSuccess.ShouldBeTrue();

        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = "remote-rig-profile-test",
            Alias = "Profile test rig",
            LastAddress = node.Transport.BaseAddress.ToString(),
        };
        var contexts = new ViewContexts();
        await using (var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, new SystemTimeProvider(), NullLogger.Instance, ct)
            .ShouldNotBeNull())
        {
            (await rig.MaybeRefreshProfileAsync(ct)).ShouldBeTrue();

            rig.ProfileName.ShouldBe("Sydney rig");
            var seen = rig.Context.RigProfile.ShouldNotBeNull();
            seen.ProfileId.ShouldBe(running.ProfileId);
            seen.Data.ShouldNotBeNull().SiteLatitude.ShouldBe(-33.87);
            (await rig.MaybeRefreshProfileAsync(ct)).ShouldBeFalse("not due again for minutes");
        }
        contexts.All[1].RigProfile.ShouldBeNull("a rig let go leaves no profile on its view");

        // A binding that names a profile is planned with that one, whichever the rig runs.
        await using var bound = RemoteRigConnection.TryConnect(binding with { RemoteProfileId = named.ProfileId }, contexts, peers: null,
            new SystemTimeProvider(), NullLogger.Instance, ct).ShouldNotBeNull();
        (await bound.MaybeRefreshProfileAsync(ct)).ShouldBeTrue();
        bound.Context.RigProfile.ShouldNotBeNull().Data.ShouldNotBeNull().SiteLatitude.ShouldBe(-31.27);
    }
}
