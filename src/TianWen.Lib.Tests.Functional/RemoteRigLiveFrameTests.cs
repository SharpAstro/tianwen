using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A remote rig's Live Session pane shows the rig's own frame, linear and bit for bit (P4 part 4 of
/// docs/plans/hardware-in-the-server.md, #931): the GUI's own connection to a rig reaches a real node over TCP and, while
/// the rig is on screen, fills its view with what the node's slot holds, which the pane then stretches exactly as it
/// does this computer's. Taken off screen, the rig pulls nothing and gives its frame back. Before this, nothing asked a
/// rig for frames at all and a remote rig's panes stayed empty. On screen, a rig pulls only the frames its TAB draws (P5b
/// part 6, #935): the Guider tab draws no OTA frame, so the rig gives that one back there too.
/// </summary>
[Collection("Hosting")]
public class RemoteRigLiveFrameTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task ARigOnScreenShowsTheNodesOwnFrameAndOneTakenOffScreenGivesItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var plane = new float[48, 64];
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                plane[y, x] = 1000 + (x * 7 + y * 13) % 64;
            }
        }
        var frame = new Image([plane], BitDepth.Int16, maxValue: 65535, minValue: 0, pedestal: 0, default);

        await using var node = await NodeHarness.StartAsync(output, ct);
        node.Factory.OnCreated = controlled =>
        {
            RemoteSessionMirrorTests.Observing(controlled.Session);
            controlled.Session.LastCapturedImages.Returns([frame]);
            controlled.Session.LastCapturedImageNumber(0).Returns(1);
        };
        node.Factory.Initialised.TrySetResult();
        await node.StartSessionAsync(ct);

        var binding = new RemoteRigBinding
        {
            BindingId = Guid.NewGuid(),
            NodeId = "remote-rig-frame-test",
            Alias = "Frame test rig",
            LastAddress = node.Transport.BaseAddress.ToString(),
        };
        var contexts = new ViewContexts();
        var app = new GuiAppState { ActiveTab = GuiTab.LiveSession };
        contexts.AttachAppState(app);
        await using var rig = RemoteRigConnection.TryConnect(binding, contexts, peers: null, new SystemTimeProvider(),
            NullLogger.Instance, ct).ShouldNotBeNull();
        contexts.Activate(rig.Context).ShouldBeTrue();

        // The render loop polls every frame; the pane reads the rig's LiveSessionState, as the Live Session tab does.
        var shown = await NodeWait.UntilAsync("the rig's frame in its live pane", _ =>
        {
            contexts.PollAll();
            return ValueTask.FromResult<(Image?, string)>(rig.Context.LiveSession.LastCapturedImages is [{ } image, ..]
                ? (image, "a frame")
                : (null, $"no frame yet (reachable {rig.Mirror.IsNodeReachable}, error {rig.Mirror.LastError ?? "none"})"));
        }, ct);

        shown.GetChannelSpan(0).SequenceEqual(frame.GetChannelSpan(0)).ShouldBeTrue("the pane must get the node's frame bit for bit");
        rig.Mirror.SavedFramePathOnThisMachine.ShouldBeNull("a rig reached over TCP names no file on this machine");

        app.ActiveTab = GuiTab.Guider;
        await NodeWait.UntilAsync("the rig on the Guider tab to give its OTA frame back", _ =>
        {
            contexts.PollAll();
            var released = !shown.TryLease(out var lease);
            lease.Dispose();
            return ValueTask.FromResult((released && rig.Mirror.LastCapturedImages.Length == 0,
                $"slots {rig.Mirror.LastCapturedImages.Length}"));
        }, ct);

        app.ActiveTab = GuiTab.LiveSession;
        shown = await NodeWait.UntilAsync("the rig's frame back on the Live Session tab", _ =>
        {
            contexts.PollAll();
            return ValueTask.FromResult<(Image?, string)>(rig.Context.LiveSession.LastCapturedImages is [{ } image, ..]
                ? (image, "a frame")
                : (null, "no frame yet"));
        }, ct);

        contexts.Activate(contexts.Local).ShouldBeTrue();
        await NodeWait.UntilAsync("the rig off screen to give its frame back", _ =>
        {
            contexts.PollAll();
            var released = !shown.TryLease(out var lease);
            lease.Dispose();
            return ValueTask.FromResult((released && rig.Mirror.LastCapturedImages.Length == 0,
                $"previews {(rig.Mirror.Previews is null ? "off" : "on")}, slots {rig.Mirror.LastCapturedImages.Length}"));
        }, ct);
    }
}
