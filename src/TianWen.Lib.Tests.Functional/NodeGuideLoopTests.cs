using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Imaging;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The Guider tab's loop of the guide camera while no session runs (#1915, docs/plans/live-session-preview.md): the node's
/// live view asked for the profile's guide camera claims it alone, and its frames are the guider's source. A profile with
/// no guide camera, or whose guider (PHD2) owns its camera, is refused in words, and a session cannot start over the loop.
/// </summary>
[Collection("Hosting")]
public class NodeGuideLoopTests(ITestOutputHelper outputHelper)
{
    private static readonly FakeDevice Camera = new FakeDevice(DeviceType.Camera, 1);
    private static readonly FakeDevice GuideCamera = new FakeDevice(DeviceType.Camera, 2);

    // Binned 4: the fake's whole sensor is slow to render a frame of in a Debug build.
    private static readonly LiveViewRequestDto Loop = new LiveViewRequestDto { CameraRole = LiveViewCamera.Guider, ExposureMs = 10, Binning = 4 };

    private static TianWenNodeClient ClientOf(NodeHarness node) => new TianWenNodeClient(node.Client);

    private static Task<LiveViewStateDto> UntilStateAsync(TianWenNodeClient client, string what, Func<LiveViewStateDto, bool> done, CancellationToken ct) =>
        UntilAsync<LiveViewStateDto>(what, async token =>
        {
            var state = (await client.GetLiveViewAsync(token)).Value;
            return (state is not null && done(state) ? state : null,
                state is null ? "none" : $"running {state.Running}, {state.FramesReceived} frames");
        }, ct);

    [Fact(Timeout = 90_000)]
    public async Task NodeGuideLoopStreamsTheGuideCameraAloneAndServesItsFramesAsTheGuiderSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct, guideCamera: GuideCamera);
        var client = ClientOf(node);
        await using var window = await NodeWindow.OpenAsync(node, outputHelper, ct);
        var pushed = new ConcurrentQueue<FrameAvailableDto>();
        window.Events.EventReceived += (_, e) =>
        {
            if (FrameAvailableDto.TryFromEvent(e, out var frame))
            {
                pushed.Enqueue(frame);
            }
        };

        var started = await client.StartLiveViewAsync(Loop, ct);
        started.IsSuccess.ShouldBeTrue(started.Error);
        var state = started.Value.ShouldNotBeNull();
        (state.CameraRole, state.Running).ShouldBe((LiveViewCamera.Guider, true));

        await UntilStateAsync(client, "guide frames to arrive", s => s.FramesReceived > 0, ct);

        // The guide camera is the loop's alone: the science camera stays free, as the mount and a guide focuser would.
        var hub = node.App.Services.GetRequiredService<IDeviceHub>();
        hub.TryGetLease(GuideCamera.DeviceUri, out _).ShouldBeTrue();
        hub.TryGetLease(Camera.DeviceUri, out _).ShouldBeFalse("only the guide camera is claimed");

        var frame = await client.GetLatestFrameAsync(FrameSources.Guider, after: null, new FrameReader(), ct);
        frame.Image.ShouldNotBeNull(frame.Error).Release();
        await UntilAsync("FRAME-AVAILABLE for the guider", _ => ValueTask.FromResult((
            pushed.Any(f => f.Source == FrameSources.Guider && f.Number > 0), $"{pushed.Count} pushed")), ct);

        // A new exposure and gain are steered while it runs.
        var steered = await client.SetLiveViewControlsAsync(new LiveViewControlsDto { ExposureMs = 20, Gain = 5 }, ct);
        steered.IsSuccess.ShouldBeTrue(steered.Error);

        // A session cannot start over the loop, and says which run is going on.
        var session = await client.StartSessionAsync(null, null, ct);
        (session.StatusCode, session.Error).ShouldBe((409, "A live view is running"));

        var stopped = await client.StopLiveViewAsync(ct);
        stopped.IsSuccess.ShouldBeTrue(stopped.Error);
        stopped.Value.ShouldNotBeNull().Running.ShouldBeFalse();
        hub.TryGetLease(GuideCamera.DeviceUri, out _).ShouldBeFalse("the camera is given back as the loop ends");
    }

    [Fact(Timeout = 60_000)]
    public async Task NodeGuideLoopRefusesAProfileWithNoGuideCameraInWords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        await node.ActivateRigAsync(Camera, mount: null, ct);
        var client = ClientOf(node);

        var refused = await client.StartLiveViewAsync(Loop, ct);
        (refused.StatusCode, refused.Error).ShouldBe((409, "The profile has no guide camera to loop"));
        node.Node.IsRunning.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task NodeGuideLoopRefusesAGuiderThatOwnsItsCamera()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NodeHarness.StartAsync(outputHelper, ct);
        var phd2 = new OpenPHD2GuiderDevice(DeviceType.Guider, "localhost/1", "PHD2").DeviceUri;
        await node.ActivateRigAsync(Camera, mount: null, ct, guideCamera: GuideCamera, guider: phd2);
        var client = ClientOf(node);

        var refused = await client.StartLiveViewAsync(Loop, ct);
        (refused.StatusCode, refused.Error).ShouldBe((409, "PHD2 owns the guide camera, so there is nothing to loop here: loop it in PHD2"));
        node.App.Services.GetRequiredService<IDeviceHub>().TryGetLease(GuideCamera.DeviceUri, out _).ShouldBeFalse("a refused start claimed nothing");
    }
}
