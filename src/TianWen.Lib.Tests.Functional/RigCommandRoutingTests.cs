using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A rig's view commands that rig once this client holds control of it (P6b of docs/plans/hardware-in-the-server.md,
/// decision 13, #1021): its Goto, its focuser, its runs go to the rig's node, never this computer's, which they drove until
/// P0b item 9 and which every rig's view then refused. A rig this client only watches refuses and says how to ask. The GUI's
/// own handler over this computer's node, with a real rig of its own over loopback TCP on screen.
/// </summary>
[Collection("NodeProcesses")]
public class RigCommandRoutingTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task ARigThisComputerOnlyWatchesRefusesAGotoAndSaysHowToAsk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var (rigNode, rig, _) = await h.ConnectRigAsync(output, ct);
        rig.MayCommand.ShouldBeFalse();

        h.Post(new SkyMapSlewToObjectSignal("Near the pole", 0, 85, Index: null, ObjectType.Unknown));
        await h.UntilSettledAsync(ct);

        h.ShouldHaveRefused("needs control of it: ask its owner for it");
        (await new TianWenNodeClient(rigNode.Client).GetJobsAsync(ct)).Value.ShouldNotBeNull().ShouldBeEmpty("the rig's mount was slewed");
        (await new TianWenNodeClient(h.Node.Client).GetJobsAsync(ct)).Value.ShouldNotBeNull()
            .ShouldNotContain(job => job.Kind == "slew", "this computer's mount was slewed from the rig's view");
    }

    [Fact(Timeout = 60_000)]
    public async Task AGrantedRigsGotoIsThatRigsSlewNeverThisComputers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var (rigNode, rig, profile) = await h.ConnectRigAsync(output, ct);
        await GuiNodeHarness.GrantAsync(rigNode, rig, ct);

        // A goto the rig's fake mount lands in seconds: beside where it points, near the pole.
        var rigHub = rigNode.App.Services.GetRequiredService<IDeviceHub>();
        var rigMount = profile.Data.ShouldNotBeNull().Mount;
        rigHub.TryGetConnectedDriver<IMountDriver>(rigMount, out var mount).ShouldBeTrue();
        h.Post(new SkyMapSlewToObjectSignal("Near the pole", await mount.GetRightAscensionAsync(ct), 85, Index: null, ObjectType.Unknown));
        await h.UntilSettledAsync(ct);

        (await new TianWenNodeClient(rigNode.Client).GetJobsAsync(ct)).Value.ShouldNotBeNull()
            .ShouldContain(job => job.Kind == "slew" && DeviceBase.SameDevice(new Uri(job.DeviceUri!), rigMount), "the rig slewed its own mount");
        (await new TianWenNodeClient(h.Node.Client).GetJobsAsync(ct)).Value.ShouldNotBeNull()
            .ShouldNotContain(job => job.Kind == "slew", "this computer's mount was slewed from the rig's view");
    }

    [Fact(Timeout = 60_000)]
    public async Task AGrantedRigsFocuserJogMovesThatRigsFocuser()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct);
        var (rigNode, rig, profile) = await h.ConnectRigAsync(output, ct);
        await GuiNodeHarness.GrantAsync(rigNode, rig, ct);
        var rigFocuser = profile.Data.ShouldNotBeNull().OTAs[0].Focuser.ShouldNotBeNull();
        var rigHub = rigNode.App.Services.GetRequiredService<IDeviceHub>();
        rigHub.TryGetConnectedDriver<IFocuserDriver>(rigFocuser, out var focuser).ShouldBeTrue();
        var before = await focuser.GetPositionAsync(ct);
        h.Hub.TryGetConnectedDriver<IFocuserDriver>(h.FocuserUri, out var local).ShouldBeTrue();
        var localBefore = await local.GetPositionAsync(ct);

        h.Post(new JogFocuserSignal(OtaIndex: 0, Steps: 25));
        await h.UntilSettledAsync(ct);

        (await focuser.GetPositionAsync(ct)).ShouldBe(before + 25, "the rig's focuser moved");
        (await local.GetPositionAsync(ct)).ShouldBe(localBefore, "this computer's focuser moved from the rig's view");
    }
}
