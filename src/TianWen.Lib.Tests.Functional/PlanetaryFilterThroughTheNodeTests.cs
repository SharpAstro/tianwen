using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Planetary mode's FILTER section (#1618) through the GUI's real signal handler over a real node, its camera, wheel and
/// focuser fakes: a press turns the wheel through the node's one filter change, which stops the running stream for it and
/// starts it again (#1639), and the next recording is named for the filter now in the beam.
/// </summary>
[Collection("NodeProcesses")]
public class PlanetaryFilterThroughTheNodeTests(ITestOutputHelper output)
{
    [Fact(Timeout = 120_000)]
    public async Task AFilterChosenOnThePanelTurnsTheWheelRestartsTheStreamAndNamesTheNextRecording()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiNodeHarness.StartAsync(output, ct, withFilterWheel: true);
        h.Post(new StartVideoCaptureSignal(RoiWidth: 320, RoiHeight: 200));
        await h.UntilSettledAsync(ct);
        var planetary = h.Planetary;
        await h.UntilAsync(() => planetary.Capture.FramesReceived > 0, ct);

        var otas = h.Profile.Data.ShouldNotBeNull().OTAs;
        var wheelUri = otas[0].FilterWheel.ShouldNotBeNull();
        h.Hub.TryGetConnectedDriver<IFilterWheelDriver>(wheelUri, out var wheel).ShouldBeTrue();
        h.Hub.TryGetConnectedDriver<ICameraDriver>(h.CameraUri, out var camera).ShouldBeTrue();
        var streams = ((FakeCameraDriver)camera).VideoStarts.Length;
        (await wheel.GetPositionAsync(ct)).ShouldBe(0, "premise: Luminance in the beam");

        h.Post(new ChangeFilterSignal(0, 1));
        await h.UntilSettledAsync(ct);
        await h.UntilAsync(() => ((FakeCameraDriver)camera).VideoStarts.Length > streams, ct);

        (await wheel.GetPositionAsync(ct)).ShouldBe(1, "the wheel turned to the slot chosen");
        h.AppState.Notifications.ShouldNotContain(static n => n.Severity >= NotificationSeverity.Warning,
            string.Join("; ", h.AppState.Notifications.Select(static n => n.Message)));
        planetary.IsCapturing.ShouldBeTrue("the stream started again after the change");

        h.Post(new StartPlanetaryRecordingSignal(TimeSpan.FromSeconds(2)));
        await h.UntilSettledAsync(ct);
        var recording = planetary.Capture.Recording.ShouldNotBeNull();
        var red = FilterChange.NameOf(wheel.Filters, 1);
        Path.GetFileName(recording.Path).ShouldContain(red.Replace(' ', '_'), Case.Insensitive, "the recording is named for the filter in the beam");

        h.Post(new StopVideoCaptureSignal());
        await h.UntilAsync(() => !planetary.IsCapturing, ct);
    }
}
